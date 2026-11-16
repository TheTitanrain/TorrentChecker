Imports System.ComponentModel
Imports System.Runtime.InteropServices
Imports System.Text

Friend Module clsCredentialStore
    Private Const GenericCredential As Integer = 1
    Private Const PersistLocalMachine As Integer = 2
    Private Const NotFound As Integer = 1168

    <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
    Private Structure Credential
        Public Flags As Integer
        Public Kind As Integer
        Public Target As String
        Public Comment As String
        Public LastWritten As ComTypes.FILETIME
        Public BlobSize As Integer
        Public Blob As IntPtr
        Public Persist As Integer
        Public AttributeCount As Integer
        Public Attributes As IntPtr
        Public TargetAlias As String
        Public UserName As String
    End Structure

    <DllImport("advapi32.dll", EntryPoint:="CredReadW", ExactSpelling:=True, CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function CredRead(target As String, kind As Integer, flags As Integer, ByRef result As IntPtr) As Boolean
    End Function

    <DllImport("advapi32.dll", EntryPoint:="CredWriteW", ExactSpelling:=True, CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function CredWrite(ByRef value As Credential, flags As Integer) As Boolean
    End Function

    <DllImport("advapi32.dll", EntryPoint:="CredDeleteW", ExactSpelling:=True, CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Function CredDelete(target As String, kind As Integer, flags As Integer) As Boolean
    End Function

    <DllImport("advapi32.dll", ExactSpelling:=True)>
    Private Sub CredFree(buffer As IntPtr)
    End Sub

    Friend Function ReadCredential(target As String) As Net.NetworkCredential
        Dim buffer As IntPtr
        If Not CredRead(target, GenericCredential, 0, buffer) Then
            Dim code = Marshal.GetLastWin32Error()
            If code = NotFound Then Return New Net.NetworkCredential()
            Throw New Win32Exception(code)
        End If
        Try
            Dim value = Marshal.PtrToStructure(Of Credential)(buffer)
            Dim password = If(value.BlobSize = 0, "", Marshal.PtrToStringUni(value.Blob, value.BlobSize \ 2))
            Return New Net.NetworkCredential(value.UserName, password)
        Finally
            CredFree(buffer)
        End Try
    End Function

    Friend Sub WriteCredential(target As String, userName As String, password As String)
        If String.IsNullOrEmpty(userName) AndAlso String.IsNullOrEmpty(password) Then Return
        Dim buffer = Marshal.StringToCoTaskMemUni(If(password, ""))
        Try
            Dim value As New Credential With {
                .Kind = GenericCredential, .Target = target, .UserName = If(userName, ""),
                .Blob = buffer, .BlobSize = Encoding.Unicode.GetByteCount(If(password, "")),
                .Persist = PersistLocalMachine
            }
            If Not CredWrite(value, 0) Then Throw New Win32Exception(Marshal.GetLastWin32Error())
        Finally
            Marshal.ZeroFreeCoTaskMemUnicode(buffer)
        End Try
    End Sub

    Friend Sub DeleteCredential(target As String)
        If CredDelete(target, GenericCredential, 0) Then Return
        Dim code = Marshal.GetLastWin32Error()
        If code <> NotFound Then Throw New Win32Exception(code)
    End Sub
End Module
