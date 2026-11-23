Imports System.ComponentModel
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Text.RegularExpressions
Imports Microsoft.Win32

Friend Module clsTorrentClientCommand
    Friend Const CommandKey As String = "client_command_template"
    Friend Const ClientToken As String = "%ClientPath%"
    Friend Const SourceToken As String = "%TorrentSource%"
    Private ReadOnly Tokens As New Regex("%(?:ClientPath|TorrentSource|TorrentName)%", RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)

    <DllImport("shell32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function CommandLineToArgvW(command As String, ByRef count As Integer) As IntPtr
    End Function

    <DllImport("kernel32.dll")>
    Private Function LocalFree(memory As IntPtr) As IntPtr
    End Function

    <DllImport("shlwapi.dll", CharSet:=CharSet.Unicode, ExactSpelling:=True)>
    Private Function AssocQueryStringW(flags As UInteger, kind As UInteger, association As String, verb As String,
                                       result As StringBuilder, ByRef size As UInteger) As Integer
    End Function

    Friend Function SplitCommand(command As String) As String()
        If String.IsNullOrWhiteSpace(command) OrElse command.Contains(ChrW(0)) Then
            Throw New FormatException("Некорректная командная строка торрент-клиента.")
        End If

        Dim count As Integer
        Dim argv = CommandLineToArgvW(command.TrimStart(), count)
        If argv = IntPtr.Zero Then Throw New Win32Exception()
        Try
            Return Enumerable.Range(0, count).
                Select(Function(index) Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size))).ToArray()
        Finally
            LocalFree(argv)
        End Try
    End Function

    Private Function QuoteArgument(value As String) As String
        ' Escape backslashes before embedded quotes and before the closing quote.
        Dim result As New StringBuilder
        result.Append(""""c)
        Dim slashes As Integer = 0
        For Each character In value
            If character = "\"c Then
                slashes += 1
                Continue For
            End If
            If character = """"c Then slashes = slashes * 2 + 1
            result.Append("\"c, slashes).Append(character)
            slashes = 0
        Next
        Return result.Append("\"c, slashes * 2).Append(""""c).ToString()
    End Function

    Private Function IsLiteralExecutable(executable As String) As Boolean
        Return Not String.IsNullOrWhiteSpace(executable) AndAlso Not Tokens.IsMatch(executable)
    End Function

    Friend Function CreateExplicitClientTemplate(executable As String) As String
        If Not IsLiteralExecutable(executable) OrElse
            Not String.Equals(IO.Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase) Then Return Nothing
        Return QuoteArgument(executable) & " " & SourceToken
    End Function

    Friend Function CreateClientStartInfo(command As String, source As String, torrentName As String, association As String,
                                          resolve As Func(Of String, String)) As ProcessStartInfo
        If String.IsNullOrWhiteSpace(command) Then Return New ProcessStartInfo(source) With {.UseShellExecute = True}

        Dim arguments = SplitCommand(command)
        Dim executable = arguments(0)
        Dim clientPath As String = Nothing
        Dim getClientPath As Func(Of String) =
            Function()
                If clientPath Is Nothing Then clientPath = resolve(association)
                If String.IsNullOrWhiteSpace(clientPath) Then
                    Throw New InvalidOperationException("Не удалось определить программу для " & association &
                                                        ". Укажите путь к торрент-клиенту явно.")
                End If
                Return clientPath
            End Function
        If String.Equals(executable, ClientToken, StringComparison.OrdinalIgnoreCase) Then
            executable = getClientPath()
        ElseIf Not IsLiteralExecutable(executable) Then
            Throw New FormatException("Укажите программу торрент-клиента или %ClientPath% в начале команды.")
        End If

        Dim expanded = arguments.Skip(1).Select(Function(argument) QuoteArgument(Tokens.Replace(argument,
            Function(token)
                Select Case token.Value.ToUpperInvariant()
                    Case "%CLIENTPATH%"
                        Return getClientPath()
                    Case "%TORRENTNAME%"
                        Return torrentName
                    Case Else
                        Return source
                End Select
            End Function)))
        Return New ProcessStartInfo(executable, String.Join(" ", expanded)) With {.UseShellExecute = False}
    End Function

    Friend Function ResolveTorrentClient(association As String) As String
        Const ExecutableString As UInteger = 2
        Const IgnoreUnknown As UInteger = &H400
        Const IsProtocol As UInteger = &H1000
        Dim flags As UInteger = IgnoreUnknown
        If association = "magnet" Then
            If Environment.OSVersion.Version >= New Version(6, 2) Then
                flags = flags Or IsProtocol
            Else
                ' Windows 7 predates ASSOCF_IS_PROTOCOL. Resolve the selected ProgID or the registered scheme.
                Using choice = Registry.CurrentUser.OpenSubKey("Software\Microsoft\Windows\Shell\Associations\UrlAssociations\magnet\UserChoice")
                    If choice IsNot Nothing Then association = CStr(choice.GetValue("ProgId", association))
                End Using
            End If
        End If

        Dim size As UInteger
        AssocQueryStringW(flags, ExecutableString, association, Nothing, Nothing, size)
        If size = 0 Then Return ""
        Dim result As New StringBuilder(CInt(size))
        Return If(AssocQueryStringW(flags, ExecutableString, association, Nothing, result, size) = 0, result.ToString(), "")
    End Function
End Module
