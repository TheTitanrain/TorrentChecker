Imports System.Globalization
Imports System.IO
Imports System.Text

Friend Module UiText
    Friend ReadOnly Codes As String() = {"ru", "en", "uk", "kk", "be"}
    Friend ReadOnly Names As String() = {"Русский", "English", "Українська", "Қазақша", "Беларуская"}
    Private translations As New Dictionary(Of String, String)(StringComparer.Ordinal)

    Private selectedCode As String = "ru"
    Friend ReadOnly Property ActiveCode As String
        Get
            Return selectedCode
        End Get
    End Property

    Friend ReadOnly Property IsRussian As Boolean
        Get
            Return ActiveCode = "ru"
        End Get
    End Property

    Friend Function Normalize(value As Object) As String
        Dim code = TryCast(value, String)
        Return If(Array.IndexOf(Codes, code) >= 0, code, "auto")
    End Function

    Friend Sub SelectLanguage(value As Object)
        Dim code = Normalize(value)
        If code = "auto" Then
            code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            If Array.IndexOf(Codes, code) < 0 Then code = "ru"
        End If
        If code = ActiveCode Then Return
        Dim index = Array.IndexOf(Codes, code)
        Dim selected As New Dictionary(Of String, String)(StringComparer.Ordinal)
        If index > 0 Then
            Dim assembly = GetType(UiText).Assembly
            For Each name In assembly.GetManifestResourceNames().Where(Function(n) n.EndsWith(".tsv", StringComparison.Ordinal))
                Using reader As New StreamReader(assembly.GetManifestResourceStream(name), Encoding.UTF8)
                    While Not reader.EndOfStream
                        Dim fields = reader.ReadLine().Split(ChrW(9))
                        selected(fields(0)) = fields(index)
                    End While
                End Using
            Next
        End If
        translations = selected
        selectedCode = code
    End Sub

    Friend Function T(source As String) As String
        Dim translated As String = Nothing
        Return If(translations.TryGetValue(source, translated), translated, source)
    End Function
End Module
