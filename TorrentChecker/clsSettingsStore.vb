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

    Friend Function Load(confirmImport As Func(Of Boolean)) As Dictionary(Of String, Object)
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
        If Not Object.Equals(wire("version"), FormatVersion) Then Throw New InvalidDataException("Версия settings.json не поддерживается.")
        historyId = ReadId(wire, "historyId")
        credentialId = ReadId(wire, "credentialId")
        Dim data = DecodeSettings(wire)
        Dim history = serializer.Deserialize(Of Dictionary(Of String, Object))(File.ReadAllText(HistoryPath(historyId), Encoding.UTF8))
        data("dtFoundTorrents") = DecodeHistory(DirectCast(history("found"), Dictionary(Of String, Object)), True)
        data("dtStoredTorrents") = DecodeHistory(DirectCast(history("stored"), Dictionary(Of String, Object)), False)

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
                Throw New InvalidDataException("Не удалось прочитать старый settings.dat. Исходный файл сохранён.")
            End Try
        End Using
    End Function

    Friend Sub Save(data As Dictionary(Of String, Object), writeSecrets As Boolean)
        Dim serializer = JsonSerializer()
        Dim wire = EncodeSettings(data)
        Dim nextHistory = Guid.NewGuid().ToString("N")
        Dim stageCredentials = writeSecrets OrElse credentialId Is Nothing
        Dim nextCredentials = If(stageCredentials, Guid.NewGuid().ToString("N"), credentialId)
        wire.Add("version", FormatVersion)
        wire.Add("historyId", nextHistory)
        wire.Add("credentialId", nextCredentials)

        Dim settingsJson = serializer.Serialize(wire)
        Dim history = New With {
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
            WriteFile(HistoryPath(nextHistory), historyJson, False)
            If stageCredentials Then SaveCredentials(data, nextCredentials)
            WriteFile(settingsPath, FormatSettings(settingsJson), historyId IsNot Nothing)
        Catch
            DeleteHistory(nextHistory)
            If stageCredentials Then DeleteCredentials(nextCredentials)
            Throw
        End Try

        'The manifest now selects the new files and credentials.
        Dim oldHistory = historyId
        Dim oldCredentials = credentialId
        historyId = nextHistory
        credentialId = nextCredentials
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
            Dim flags = MoveWriteThrough Or If(replace, MoveReplaceExisting, 0)
            If Not MoveFileEx(temporary, path, flags) Then
                Throw New ComponentModel.Win32Exception(Marshal.GetLastWin32Error())
            End If
        Finally
            TryDeleteFile(temporary)
        End Try
    End Sub

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
            Throw New InvalidDataException("Неверная ссылка в settings.json.")
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

    Private Sub SaveCredentials(data As Dictionary(Of String, Object), id As String)
        SaveTracker(data, id, "rutracker", "login_username", "login_password")
        SaveTracker(data, id, "kinozal", "username", "password")
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
        For Each service In {"rutracker", "kinozal"}
            Try
                DeleteCredential(Target(id, service))
            Catch ex As ComponentModel.Win32Exception
                'An unused entry does not affect the committed settings.
            End Try
        Next
    End Sub
End Class
