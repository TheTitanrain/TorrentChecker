Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Runtime.InteropServices
Imports System.Security.Cryptography
Imports System.Text

Friend NotInheritable Class clsSettingsStore
    Private Const FormatVersion As Integer = 1
    Private Const MoveReplaceExisting As Integer = 1
    Private Const MoveWriteThrough As Integer = 8
    Private ReadOnly directory As String
    Private ReadOnly settingsPath As String
    Private ReadOnly credentialPrefix As String
    Private historyId As String
    Private credentialId As String

    <DllImport("kernel32.dll", EntryPoint:="MoveFileExW", ExactSpelling:=True, CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function MoveFileEx(source As String, destination As String, flags As Integer) As Boolean
    End Function

    Friend Sub New(directory As String)
        Me.directory = Path.GetFullPath(directory)
        settingsPath = Path.Combine(Me.directory, "settings.json")
        credentialPrefix = "TorrentChecker/" & Hash(Me.directory.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()) & "/"
    End Sub

    Friend Function AcquireLock() As FileStream
        Return New FileStream(Path.Combine(directory, ".settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
    End Function

    Friend Function Load(confirmImport As Func(Of Boolean), Optional selectLanguage As Action(Of Object) = Nothing) As Dictionary(Of String, Object)
        Dim json As String
        Try
            json = File.ReadAllText(settingsPath, Encoding.UTF8)
        Catch ex As FileNotFoundException
            Dim legacy = LoadLegacy(confirmImport)
            If legacy Is Nothing Then Return Nothing
            Save(legacy, True)
            json = File.ReadAllText(settingsPath, Encoding.UTF8)
        End Try
        Dim serializer = JsonSerializer()
        Dim wire = serializer.Deserialize(Of Dictionary(Of String, Object))(json)
        If selectLanguage IsNot Nothing Then
            Dim section As Object = Nothing
            Dim language As Object = Nothing
            wire.TryGetValue("options", section)
            Dim options = TryCast(section, Dictionary(Of String, Object))
            If options IsNot Nothing Then options.TryGetValue("language", language)
            selectLanguage(language)
        End If
        If Not Object.Equals(wire("version"), FormatVersion) Then Throw New InvalidDataException(UiText.T("Версия settings.json не поддерживается."))
        historyId = ReadId(wire, "historyId")
        credentialId = ReadId(wire, "credentialId")
        Dim data = DecodeSettings(wire)
        Dim selectedHistory As String = Nothing
        Dim history = ReadHistory(historyId, selectedHistory)
        data("dtFoundTorrents") = DecodeHistory(DirectCast(history("found"), Dictionary(Of String, Object)), True)
        data("dtStoredTorrents") = DecodeHistory(DirectCast(history("stored"), Dictionary(Of String, Object)), False)

        Dim endpoint = ProxyEndpoint(DirectCast(data("proxy"), Dictionary(Of String, Object)))
        data("proxyCredential") = If(endpoint Is Nothing, New NetworkCredential(),
                                    ReadCredential(Target(credentialId, "proxy"), endpoint.AbsoluteUri))
        AddTrackerCredentials(data, Trackers.rutracker, "login_username", "login_password")
        AddTrackerCredentials(data, Trackers.kinozal, "username", "password")
        Return data
    End Function

    Private Function LoadLegacy(confirmImport As Func(Of Boolean)) As Dictionary(Of String, Object)
        Dim stream As FileStream
        Try
            stream = File.OpenRead(Path.Combine(directory, "settings.dat"))
        Catch ex As FileNotFoundException
            Return Nothing
        End Try
        Using stream
            If stream.Length = 0 Then Return Nothing
            If Not confirmImport() Then Throw New OperationCanceledException()
            'Compatibility only. The user must recognize the source file before using BinaryFormatter.
            Try
                Using gzip As New GZipStream(stream, CompressionMode.Decompress)
                    Dim formatter As New Runtime.Serialization.Formatters.Binary.BinaryFormatter()
                    Return DirectCast(formatter.Deserialize(gzip), Dictionary(Of String, Object))
                End Using
            Catch ex As Exception
                Throw New InvalidDataException(UiText.T("Не удалось прочитать старый settings.dat. Исходный файл сохранён."))
            End Try
        End Using
    End Function

    Friend Sub Save(data As Dictionary(Of String, Object), writeSecrets As Boolean)
        'Finish a committed save before reusing the fixed staging filename.
        If historyId IsNot Nothing Then
            Dim selectedHistory As String = Nothing
            ReadHistory(historyId, selectedHistory)
            If selectedHistory = PendingHistoryPath Then PublishHistory()
        End If
        Dim serializer = JsonSerializer()
        Dim wire = EncodeSettings(data)
        Dim nextHistory = Guid.NewGuid().ToString("N")
        Dim stageCredentials = writeSecrets OrElse credentialId Is Nothing
        Dim nextCredentials = If(stageCredentials, Guid.NewGuid().ToString("N"), credentialId)
        wire.Add("version", FormatVersion)
        wire.Add("historyId", nextHistory)
        wire.Add("credentialId", nextCredentials)

        Dim settingsJson = serializer.Serialize(wire)
        Dim proxy = ProxyEndpoint(DirectCast(wire("proxy"), Dictionary(Of String, Object)))
        Dim history = New With {
            .historyId = nextHistory,
            .found = EncodeHistory(HistoryTable(data, "dtFoundTorrents", True)),
            .stored = EncodeHistory(HistoryTable(data, "dtStoredTorrents", False))
        }
        Dim historyJson = serializer.Serialize(history)

        If writeSecrets AndAlso credentialId Is Nothing Then
            'Validate the converted legacy data before publishing the new settings.
            DecodeSettings(serializer.Deserialize(Of Dictionary(Of String, Object))(settingsJson))
            Dim converted = serializer.Deserialize(Of Dictionary(Of String, Object))(historyJson)
            DecodeHistory(DirectCast(converted("found"), Dictionary(Of String, Object)), True)
            DecodeHistory(DirectCast(converted("stored"), Dictionary(Of String, Object)), False)
        End If

        Try
            WriteFile(PendingHistoryPath, historyJson, File.Exists(PendingHistoryPath))
            If stageCredentials Then SaveCredentials(data, nextCredentials, proxy)
            WriteFile(settingsPath, FormatSettings(settingsJson), historyId IsNot Nothing)
        Catch
            TryDeleteFile(PendingHistoryPath)
            If stageCredentials Then DeleteCredentials(nextCredentials)
            Throw
        End Try

        'The manifest now selects the new files and credentials.
        Dim oldHistory = historyId
        Dim oldCredentials = credentialId
        historyId = nextHistory
        credentialId = nextCredentials
        Try
            PublishHistory()
        Catch ex As ComponentModel.Win32Exception
            'Settings already committed: keep the pending history for Load and the next Save.
        End Try
        'The committed history is durable in either the main or pending file.
        DeleteHistory(oldHistory)
        If stageCredentials Then DeleteCredentials(oldCredentials)
    End Sub

    Private Shared Function HistoryTable(data As Dictionary(Of String, Object), key As String, found As Boolean) As DataTable
        If data.ContainsKey(key) Then Return DirectCast(data(key), DataTable)
        Return CreateHistoryTable(found)
    End Function

    Private Shared Sub WriteFile(path As String, contents As String, replace As Boolean)
        Dim temporary = path & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            Using stream As New FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                Using writer As New StreamWriter(stream, New UTF8Encoding(False), 4096, True)
                    writer.Write(contents)
                End Using
                stream.Flush(True)
            End Using
            'Both paths share a directory. Do not allow a copy/delete fallback across volumes.
            MoveFile(temporary, path, replace)
        Finally
            TryDeleteFile(temporary)
        End Try
    End Sub

    Private Shared Sub MoveFile(source As String, destination As String, replace As Boolean)
        Dim flags = MoveWriteThrough Or If(replace, MoveReplaceExisting, 0)
        If Not MoveFileEx(source, destination, flags) Then
            Throw New ComponentModel.Win32Exception(Marshal.GetLastWin32Error())
        End If
    End Sub

    Private ReadOnly Property PendingHistoryPath As String
        Get
            Return Path.Combine(directory, "history.pending.json")
        End Get
    End Property

    Private Sub PublishHistory()
        MoveFile(PendingHistoryPath, Path.Combine(directory, "history.json"), True)
    End Sub

    Private Function ReadHistory(id As String, ByRef selectedPath As String) As Dictionary(Of String, Object)
        Dim readFailure As Exception = Nothing
        For Each candidate In {Path.Combine(directory, "history.json"), PendingHistoryPath}
            Try
                Dim history = JsonSerializer().Deserialize(Of Dictionary(Of String, Object))(File.ReadAllText(candidate, Encoding.UTF8))
                Dim candidateId As Object = Nothing
                If history IsNot Nothing AndAlso history.TryGetValue("historyId", candidateId) AndAlso Object.Equals(candidateId, id) Then
                    selectedPath = candidate
                    Return history
                End If
            Catch ex As FileNotFoundException
                'An absent candidate is expected before the first publication.
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                       TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidOperationException
                'A damaged or unavailable old main must not hide a committed pending file.
                If readFailure Is Nothing Then readFailure = ex
            End Try
        Next
        'Read existing installations without changing their files until a successful save.
        selectedPath = HistoryPath(id)
        Try
            Return JsonSerializer().Deserialize(Of Dictionary(Of String, Object))(File.ReadAllText(selectedPath, Encoding.UTF8))
        Catch ex As FileNotFoundException
            Throw New InvalidDataException(UiText.T("Неверная ссылка в settings.json."), If(readFailure, ex))
        End Try
    End Function

    Private Shared Sub TryDeleteFile(path As String)
        Try
            File.Delete(path)
        Catch ex As IOException
        Catch ex As UnauthorizedAccessException
        End Try
    End Sub

    Private Function HistoryPath(id As String) As String
        Return Path.Combine(directory, "history-" & id & ".json")
    End Function

    Private Sub DeleteHistory(id As String)
        If id IsNot Nothing Then TryDeleteFile(HistoryPath(id))
    End Sub

    Private Shared Function ReadId(wire As Dictionary(Of String, Object), name As String) As String
        Dim id As Guid
        Dim value = TryCast(wire(name), String)
        If value Is Nothing OrElse value.Length <> 32 OrElse Not Guid.TryParseExact(value, "N", id) Then
            Throw New InvalidDataException(UiText.T("Неверная ссылка в settings.json."))
        End If
        Return id.ToString("N")
    End Function

    Private Shared Function Hash(value As String) As String
        Using algorithm = SHA256.Create()
            Return BitConverter.ToString(algorithm.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "")
        End Using
    End Function

    Friend Shared Function ProxyEndpoint(proxy As Dictionary(Of String, Object)) As Uri
        Dim host = CStr(proxy("proxy_address"))
        Dim port As Integer
        If String.IsNullOrWhiteSpace(host) OrElse Not Integer.TryParse(CStr(proxy("proxy_port")), port) OrElse
           port < 1 OrElse port > 65535 OrElse Uri.CheckHostName(host) = UriHostNameType.Unknown Then Return Nothing
        Return New UriBuilder("http", host, port).Uri
    End Function

    Private Function Target(id As String, service As String) As String
        Return credentialPrefix & id & "/" & service
    End Function

    Private Sub AddTrackerCredentials(data As Dictionary(Of String, Object), tracker As Trackers, userKey As String, passwordKey As String)
        Dim service = tracker.ToString()
        Dim value = ReadCredential(Target(credentialId, service))
        Dim defaults = DirectCast(TrackerParams(tracker)("credentials"), Dictionary(Of String, String))
        Dim fields As New Dictionary(Of String, String)(defaults)
        fields(userKey) = value.UserName
        fields(passwordKey) = value.Password
        data(service & "_credentials") = fields
    End Sub

    Private Sub SaveCredentials(data As Dictionary(Of String, Object), id As String, proxy As Uri)
        SaveTracker(data, id, "rutracker", "login_username", "login_password")
        SaveTracker(data, id, "kinozal", "username", "password")
        Dim value As NetworkCredential = If(data.ContainsKey("proxyCredential"), DirectCast(data("proxyCredential"), NetworkCredential), Nothing)
        If value IsNot Nothing AndAlso (value.UserName.Length <> 0 OrElse value.Password.Length <> 0) Then
            If proxy Is Nothing Then Throw New InvalidDataException(UiText.T("Некорректный адрес или порт прокси."))
            WriteCredential(Target(id, "proxy"), value.UserName, value.Password, proxy.AbsoluteUri)
        End If
    End Sub

    Private Sub SaveTracker(data As Dictionary(Of String, Object), id As String, service As String, userKey As String, passwordKey As String)
        Dim key = service & "_credentials"
        If Not data.ContainsKey(key) Then Return
        Dim value = DirectCast(data(key), Dictionary(Of String, String))
        Dim user As String = Nothing
        Dim password As String = Nothing
        value.TryGetValue(userKey, user)
        value.TryGetValue(passwordKey, password)
        WriteCredential(Target(id, service), user, password)
    End Sub

    Private Sub DeleteCredentials(id As String)
        If id Is Nothing Then Return
        For Each service In {"rutracker", "kinozal", "proxy"}
            Try
                DeleteCredential(Target(id, service))
            Catch ex As ComponentModel.Win32Exception
                'An unused entry does not affect the committed settings.
            End Try
        Next
    End Sub
End Class
