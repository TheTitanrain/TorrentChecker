Public Class frmCredentials
    Private HTTP As New clsHTTP

    Public Sub New()
        InitializeComponent()

        TabPage_rutracker.Text = TrackerParams(Trackers.rutracker)("tracker_name")
        TabPage_kinozal.Text = TrackerParams(Trackers.kinozal)("tracker_name")

        For Each ctrl As Control In Me.Controls
            ctrl.Font = FONT_NORMAL
        Next

        lblRegistration_rt.Font = FONT_UNDERLINE
        lblRegistration_kz.Font = FONT_UNDERLINE
    End Sub

    Private Sub frmCredentials_KeyDown(sender As Object, e As KeyEventArgs) Handles Me.KeyDown
        If e.KeyCode = Keys.Escape Then Me.Hide()
    End Sub

    Private Sub frmCredentials_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Me.Icon = TRAY_ICON
        Me.Text = "Авторизация"

        chkUseProxy.Checked = ProxySettings("use_proxy")
        txtProxyAddress.Text = ProxySettings("proxy_address")
        txtProxyPort.Text = ProxySettings("proxy_port")
        txtProxyLogin.Text = ProxyCredential.UserName
        txtProxyPassword.Text = ProxyCredential.Password

        txtLogin_rt.Text = TrackerParams(Trackers.rutracker)("credentials")("login_username")
        txtPassword_rt.Text = TrackerParams(Trackers.rutracker)("credentials")("login_password")
        txtLogin_kz.Text = TrackerParams(Trackers.kinozal)("credentials")("username")
        txtPassword_kz.Text = TrackerParams(Trackers.kinozal)("credentials")("password")

        Me.Show()
        tbCredentialsControl.SelectedIndex = CredentialsControl_SELINDEX
        Select Case CredentialsControl_SELINDEX
            Case 0
                txtLogin_rt.SelectAll()
                txtLogin_rt.Focus()
            Case 1
                txtLogin_kz.SelectAll()
                txtLogin_kz.Focus()
        End Select
    End Sub
    Private Function MakeChecks(proxy As Dictionary(Of String, Object)) As Boolean
        If (chkUseProxy.Checked OrElse txtProxyLogin.TextLength > 0 OrElse txtProxyPassword.TextLength > 0) AndAlso
           clsSettingsStore.ProxyEndpoint(proxy) Is Nothing Then
            MsgBox("Некорректный адрес или порт прокси-сервера.", MsgBoxStyle.Critical + MsgBoxStyle.OkOnly)
            txtProxyAddress.Focus()
            Return False
        End If

        If txtLogin_rt.TextLength > 0 And txtPassword_rt.TextLength = 0 Then
            MsgBox("Пароль от " & TrackerParams(Trackers.rutracker)("tracker_name") & " не может быть пустым!", MsgBoxStyle.Critical + MsgBoxStyle.OkOnly)
            tbCredentialsControl.SelectedTab = TabPage_rutracker
            txtPassword_rt.Focus()
            Return False
        End If

        If txtLogin_rt.TextLength = 0 And txtPassword_rt.TextLength > 0 Then
            MsgBox("Логин от " & TrackerParams(Trackers.rutracker)("tracker_name") & " не может быть пустым!", MsgBoxStyle.Critical + MsgBoxStyle.OkOnly)
            tbCredentialsControl.SelectedTab = TabPage_rutracker
            txtLogin_rt.Focus()
            Return False
        End If

        If txtLogin_kz.TextLength > 0 And txtPassword_kz.TextLength = 0 Then
            MsgBox("Пароль от " & TrackerParams(Trackers.kinozal)("tracker_name") & " не может быть пустым!", MsgBoxStyle.Critical + MsgBoxStyle.OkOnly)
            tbCredentialsControl.SelectedTab = TabPage_kinozal
            txtPassword_kz.Focus()
            Return False
        End If

        If txtLogin_kz.TextLength = 0 And txtPassword_kz.TextLength > 0 Then
            MsgBox("Логин от " & TrackerParams(Trackers.kinozal)("tracker_name") & " не может быть пустым!", MsgBoxStyle.Critical + MsgBoxStyle.OkOnly)
            tbCredentialsControl.SelectedTab = TabPage_kinozal
            txtLogin_kz.Focus()
            Return False
        End If

        If txtLogin_kz.TextLength = 0 And txtPassword_kz.TextLength = 0 Then
            For Each idx As Integer In KeyWordsParams.Keys
                If idx = 0 Then Continue For
                If CBool(KeyWordsParams(idx)(Trackers.kinozal.ToString)) Then
                    MsgBox("Нельзя удалять данные от " & TrackerParams(Trackers.kinozal)("tracker_name") & ", т.к. он используется для " & KeyWordsParams(idx)("label"), MsgBoxStyle.Critical + MsgBoxStyle.OkOnly)
                    tbCredentialsControl.SelectedTab = TabPage_kinozal
                    txtLogin_kz.Focus()
                    Return False
                End If
            Next
        End If

        Return True
    End Function

    Private Sub txtProxyPort_GotFocus(sender As Object, e As EventArgs) Handles txtProxyPort.GotFocus
        txtProxyPort.SelectAll()
    End Sub

    Private Sub txtProxyPort_KeyPress(sender As Object, e As KeyPressEventArgs) Handles txtProxyPort.KeyPress
        If Not Char.IsDigit(e.KeyChar) And Not Char.IsControl(e.KeyChar) Then
            e.Handled = True
        End If
    End Sub

    Private Sub btnCancel_Click(sender As Object, e As EventArgs) Handles btnCancel.Click
        Me.Hide()
    End Sub

    Private Sub btnSave_Click(sender As Object, e As EventArgs) Handles btnSave.Click
        Dim proxy = ProxyFromForm()
        If Not MakeChecks(proxy) Then Exit Sub
        Dim proxyAuth As New Net.NetworkCredential(txtProxyLogin.Text, txtProxyPassword.Text)
        Dim ru = UpdatedCredential(Trackers.rutracker, "login_username", txtLogin_rt.Text, "login_password", txtPassword_rt.Text)
        Dim kz = UpdatedCredential(Trackers.kinozal, "username", txtLogin_kz.Text, "password", txtPassword_kz.Text)
        If Not SaveCredentialsAndProxy(proxy, proxyAuth, ru, kz) Then Return

        If TrackerParams(Trackers.rutracker)("credentials")("login_username") <> txtLogin_rt.Text _
            Or TrackerParams(Trackers.rutracker)("credentials")("login_password") <> txtPassword_rt.Text Then
            TrackerParams(Trackers.rutracker)("is_logged") = False
            HTTP.ClearCookies(New Uri(TrackerParams(Trackers.rutracker)("login_url")))
        End If

        If TrackerParams(Trackers.kinozal)("credentials")("username") <> txtLogin_kz.Text _
            Or TrackerParams(Trackers.kinozal)("credentials")("password") <> txtPassword_kz.Text Then
            TrackerParams(Trackers.kinozal)("is_logged") = False
            HTTP.ClearCookies(New Uri(TrackerParams(Trackers.kinozal)("login_url")))
        End If

        ProxySettings = proxy
        ProxyCredential = proxyAuth
        TrackerParams(Trackers.rutracker)("credentials") = ru
        TrackerParams(Trackers.kinozal)("credentials") = kz

        Me.Hide()
    End Sub

    Private Function ProxyFromForm() As Dictionary(Of String, Object)
        Return New Dictionary(Of String, Object) From {
            {"use_proxy", chkUseProxy.Checked}, {"proxy_address", txtProxyAddress.Text.Trim()}, {"proxy_port", txtProxyPort.Text}
        }
    End Function

    Private Shared Function UpdatedCredential(tracker As Trackers, userKey As String, user As String,
                                              passwordKey As String, password As String) As Dictionary(Of String, String)
        Dim fields As New Dictionary(Of String, String)(DirectCast(TrackerParams(tracker)("credentials"), Dictionary(Of String, String)))
        fields(userKey) = user
        fields(passwordKey) = password
        Return fields
    End Function

    Private Sub chkUseProxy_CheckedChanged(sender As Object, e As EventArgs) Handles chkUseProxy.CheckedChanged
        txtProxyAddress.Enabled = chkUseProxy.Checked
        txtProxyPort.Enabled = chkUseProxy.Checked
        txtProxyLogin.Enabled = chkUseProxy.Checked
        txtProxyPassword.Enabled = chkUseProxy.Checked
    End Sub

    Private Sub ProxyEndpoint_Changed(sender As Object, e As EventArgs) Handles txtProxyAddress.TextChanged, txtProxyPort.TextChanged
        txtProxyLogin.Clear()
        txtProxyPassword.Clear()
    End Sub

    Private Sub txtProxyAddress_GotFocus(sender As Object, e As EventArgs) Handles txtProxyAddress.GotFocus
        txtProxyAddress.SelectAll()
    End Sub


    Private Sub txtLogin_rt_GotFocus(sender As Object, e As EventArgs) Handles txtLogin_rt.GotFocus
        txtLogin_rt.SelectAll()
    End Sub

    Private Sub txtLogin_rt_LostFocus(sender As Object, e As EventArgs) Handles txtLogin_rt.LostFocus
        txtLogin_rt.Text.Trim()
    End Sub

    Private Sub txtLogin_kz_GotFocus(sender As Object, e As EventArgs) Handles txtLogin_kz.GotFocus
        txtLogin_kz.SelectAll()
    End Sub

    Private Sub txtLogin_kz_LostFocus(sender As Object, e As EventArgs) Handles txtLogin_kz.LostFocus
        txtLogin_kz.Text.Trim()
    End Sub

    Private Sub txtPassword_rt_GotFocus(sender As Object, e As EventArgs) Handles txtPassword_rt.GotFocus
        txtPassword_rt.SelectAll()
    End Sub

    Private Sub txtPassword_kz_GotFocus(sender As Object, e As EventArgs) Handles txtPassword_kz.GotFocus
        txtPassword_kz.SelectAll()
    End Sub

    Private Sub tbCredentialsControl_SelectedIndexChanged(sender As Object, e As EventArgs) Handles tbCredentialsControl.SelectedIndexChanged
        Select Case tbCredentialsControl.SelectedIndex
            Case 0
                txtLogin_rt.SelectAll()
                txtLogin_rt.Focus()
            Case 1
                txtLogin_kz.SelectAll()
                txtLogin_kz.Focus()
        End Select
    End Sub

    Private Sub lblRegistration_rt_Click(sender As Object, e As EventArgs) Handles lblRegistration_rt.Click
        Try
            Dim url As String = TrackerParams(Trackers.rutracker)("registration_url")
            System.Diagnostics.Process.Start(url)
        Catch ex As Exception
            MsgBox("Не могу открыть браузер" & vbCrLf & GetProperExceptionText(ex), MsgBoxStyle.Critical + MsgBoxStyle.OkOnly)
        End Try
    End Sub

    Private Sub lblRegistration_kz_Click(sender As Object, e As EventArgs) Handles lblRegistration_kz.Click
        Try
            Dim url As String = TrackerParams(Trackers.kinozal)("registration_url")
            System.Diagnostics.Process.Start(url)
        Catch ex As Exception
            MsgBox("Не могу открыть браузер" & vbCrLf & GetProperExceptionText(ex), MsgBoxStyle.Critical + MsgBoxStyle.OkOnly)
        End Try
    End Sub
End Class
