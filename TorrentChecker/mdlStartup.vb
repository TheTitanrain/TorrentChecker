Imports System.IO

Friend Module mdlStartup
    <STAThread>
    Public Sub Main()
        Try
            UiText.SelectLanguage(Nothing)
            Application.EnableVisualStyles()
            Application.SetCompatibleTextRenderingDefault(False)
            Using settingsLock = SettingsStore.AcquireLock()
                LoadSettingsData()
                Application.Run(My.Forms.frmMain)
            End Using
        Catch ex As OperationCanceledException
            Return
        Catch ex As Exception
            Environment.ExitCode = 1
            Dim message As String = ex.GetBaseException().Message
            Try
                Dim logPath As String = Path.Combine(Path.GetTempPath(), "TorrentChecker-startup.log")
                File.WriteAllText(logPath, ex.ToString())
                message &= vbCrLf & vbCrLf & logPath
            Catch
                'The error dialog is still available if the temporary directory is unwritable.
            End Try
            MessageBox.Show(message, UiText.T("Ошибка запуска TorrentChecker"), MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub
End Module
