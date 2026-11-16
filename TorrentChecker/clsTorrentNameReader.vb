Imports System.IO
Imports System.Text

' Reads only the name, but validates/skips every bencode value before returning it.
Friend NotInheritable Class clsTorrentNameReader
    Private Const MaxDepth As Integer = 64
    Private ReadOnly data As Byte()
    Private position As Integer
    Private foundInfo As Boolean
    Private foundName As Boolean
    Private foundUtf8Name As Boolean
    Private name As ArraySegment(Of Byte)
    Private utf8Name As ArraySegment(Of Byte)

    Public Sub New(body As Byte())
        data = body
    End Sub

    Public Function ReadName() As String
        If data Is Nothing OrElse data.Length = 0 OrElse data(0) <> 100 Then Fail()
        ReadValue(0, 1)
        If position <> data.Length OrElse Not foundInfo OrElse Not (foundName OrElse foundUtf8Name) Then Fail()
        Dim chosen = If(utf8Name.Count > 0, utf8Name, name)
        If chosen.Count = 0 Then Return ""
        Return Encoding.UTF8.GetString(data, chosen.Offset, chosen.Count)
    End Function

    ' context: 1 = root dictionary, 2 = its info dictionary, 0 = unrelated value.
    Private Sub ReadValue(depth As Integer, context As Integer)
        If position >= data.Length Then Fail()
        Dim token = data(position)
        If token = 100 OrElse token = 108 Then
            If depth >= MaxDepth Then Fail()
            position += 1
            While position < data.Length AndAlso data(position) <> 101
                If token = 100 Then
                    Dim key = ReadString()
                    If context = 1 AndAlso KeyEquals(key, "info") Then
                        If foundInfo OrElse position >= data.Length OrElse data(position) <> 100 Then Fail()
                        foundInfo = True
                        ReadValue(depth + 1, 2)
                    ElseIf context = 2 AndAlso KeyEquals(key, "name") Then
                        If foundName Then Fail()
                        foundName = True
                        name = ReadString()
                    ElseIf context = 2 AndAlso KeyEquals(key, "name.utf-8") Then
                        If foundUtf8Name Then Fail()
                        foundUtf8Name = True
                        utf8Name = ReadString()
                    Else
                        ReadValue(depth + 1, 0)
                    End If
                Else
                    ReadValue(depth + 1, 0)
                End If
            End While
            If position >= data.Length Then Fail()
            position += 1
        ElseIf token = 105 Then
            ReadInteger()
        Else
            ReadString()
        End If
    End Sub

    Private Function ReadString() As ArraySegment(Of Byte)
        If position >= data.Length OrElse Not IsDigit(data(position)) Then Fail()
        Dim start = position
        Dim length As Integer = 0
        While position < data.Length AndAlso IsDigit(data(position))
            Dim digit As Integer = data(position) - 48
            If length > (Integer.MaxValue - digit) \ 10 Then Fail()
            length = length * 10 + digit
            position += 1
        End While
        If position - start > 1 AndAlso data(start) = 48 Then Fail()
        If position >= data.Length OrElse data(position) <> 58 Then Fail()
        position += 1
        If length > data.Length - position Then Fail()
        Dim result As New ArraySegment(Of Byte)(data, position, length)
        position += length
        Return result
    End Function

    Private Sub ReadInteger()
        position += 1
        Dim negative = position < data.Length AndAlso data(position) = 45
        If negative Then position += 1
        Dim start = position
        While position < data.Length AndAlso IsDigit(data(position))
            position += 1
        End While
        If position = start OrElse position >= data.Length OrElse data(position) <> 101 Then Fail()
        If data(start) = 48 AndAlso (negative OrElse position - start > 1) Then Fail()
        ' Unrelated integers need no conversion or machine-sized numeric limit.
        position += 1
    End Sub

    Private Function KeyEquals(key As ArraySegment(Of Byte), expected As String) As Boolean
        If key.Count <> expected.Length Then Return False
        For i As Integer = 0 To key.Count - 1
            If data(key.Offset + i) <> AscW(expected(i)) Then Return False
        Next
        Return True
    End Function

    Private Shared Function IsDigit(value As Byte) As Boolean
        Return value >= 48 AndAlso value <= 57
    End Function

    Private Shared Sub Fail()
        Throw New InvalidDataException("Invalid torrent bencode.")
    End Sub
End Class
