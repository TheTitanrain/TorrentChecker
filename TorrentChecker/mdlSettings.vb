Imports System.IO
Imports System.Net

Module mdlSettings
    Public LogPath As String = Path.Combine(Application.StartupPath, "log.txt")
    Friend ReadOnly SettingsStore As New clsSettingsStore(Application.StartupPath)
    Friend ProxyCredential As New NetworkCredential()
    Private loaded As Dictionary(Of String, Object)
    Private settingsReady As Boolean

    Friend Sub LoadSettingsData()
        loaded = SettingsStore.Load(
            Function() MessageBox.Show("Импортировать старый settings.dat? Продолжайте, только если файл создан вашей копией TorrentChecker и вы доверяете его источнику.",
                                       "Перенос настроек TorrentChecker", MessageBoxButtons.YesNo,
                                       MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) = DialogResult.Yes, AddressOf UiText.SelectLanguage)
    End Sub

    Public Sub SaveSettings()
        If Not settingsReady Then Return
        Try
            WriteSettings(ProxySettings, ProxyCredential, Nothing, Nothing)
        Catch ex As Exception
            ShowSaveError(ex)
        End Try
    End Sub

    Friend Function SaveCredentialsAndProxy(proxy As Dictionary(Of String, Object), proxyAuth As NetworkCredential,
                                            ruTracker As Dictionary(Of String, String), kinozal As Dictionary(Of String, String)) As Boolean
        If Not settingsReady Then Return False
        Try
            WriteSettings(proxy, proxyAuth, ruTracker, kinozal)
            Return True
        Catch ex As Exception
            ShowSaveError(ex)
            Return False
        End Try
    End Function

    Private Sub ShowSaveError(ex As Exception)
        MsgBox(UiText.T("Ошибка при записи настроек!") & vbCrLf & GetProperExceptionText(ex), MsgBoxStyle.Critical + MsgBoxStyle.OkOnly)
    End Sub

    Private Sub WriteSettings(proxy As Dictionary(Of String, Object), proxyAuth As NetworkCredential,
                              ruTracker As Dictionary(Of String, String), kinozal As Dictionary(Of String, String))
        Dim scrollIndex = frmMain.dgvTorrents.FirstDisplayedScrollingRowIndex
        Try
            dtFoundTorrents.AcceptChanges()
            dtStoredTorrents.AcceptChanges()
        Finally
            Dim grid = frmMain.dgvTorrents
            If scrollIndex >= 0 AndAlso scrollIndex < grid.Rows.Count AndAlso grid.Rows(scrollIndex).Visible Then
                grid.FirstDisplayedScrollingRowIndex = scrollIndex
            End If
        End Try

        For Each col In dgvTorrentsColumns
            Dim gridColumn = frmMain.dgvTorrents.Columns(col.Key)
            col.Value("index") = gridColumn.DisplayIndex
            col.Value("width") = gridColumn.Width
            col.Value("visibility") = gridColumn.Visible
        Next
        If frmMain.dgvTorrents.SortedColumn IsNot Nothing AndAlso frmMain.dgvTorrents.SortOrder <> SortOrder.None Then
            dgvTorrentsSorting("column_name") = frmMain.dgvTorrents.SortedColumn.Name
            dgvTorrentsSorting("sort_order") = frmMain.dgvTorrents.SortOrder
        End If

        Dim data As New Dictionary(Of String, Object) From {
            {"frmMain", MainFormSettings}, {"proxy", proxy}, {"proxyCredential", proxyAuth},
            {"dtFoundTorrents", dtFoundTorrents}, {"dtStoredTorrents", dtStoredTorrents},
            {"dgvTorrentsColumns", dgvTorrentsColumns}, {"dgvTorrentsSorting", dgvTorrentsSorting},
            {"KwListToParamsMapping", KwListToParamsMapping}, {"KeyWordsParams", KeyWordsParams},
            {"CHECK_INTERVAL", CHECK_INTERVAL}, {"TORRENTS_MAX_STORETIME", TORRENTS_MAX_STORETIME},
            {"TORRENTS_MAX_RESULTS", TORRENTS_MAX_RESULTS}, {"AppOptions", AppOptions}
        }
        If ruTracker IsNot Nothing Then
            data.Add("rutracker_credentials", ruTracker)
            data.Add("kinozal_credentials", kinozal)
        End If
        SettingsStore.Save(data, ruTracker IsNot Nothing)
    End Sub

    Public Sub ReadSettings()
        If loaded Is Nothing Then
            dtFoundTorrents = CreateHistoryTable(True)
            dtStoredTorrents = CreateHistoryTable(False)
        Else
            MainFormSettings = loaded("frmMain")
            TrackerParams(Trackers.rutracker)("credentials") = loaded("rutracker_credentials")
            TrackerParams(Trackers.kinozal)("credentials") = loaded("kinozal_credentials")
            ProxySettings = loaded("proxy")
            ProxyCredential = loaded("proxyCredential")
            dtFoundTorrents = loaded("dtFoundTorrents")
            dtStoredTorrents = loaded("dtStoredTorrents")
            KwListToParamsMapping = loaded("KwListToParamsMapping")
            KeyWordsParams = loaded("KeyWordsParams")
            dgvTorrentsColumns = loaded("dgvTorrentsColumns")
            dgvTorrentsSorting = loaded("dgvTorrentsSorting")
            CHECK_INTERVAL = loaded("CHECK_INTERVAL")
            TORRENTS_MAX_STORETIME = loaded("TORRENTS_MAX_STORETIME")
            TORRENTS_MAX_RESULTS = loaded("TORRENTS_MAX_RESULTS")
            AppOptions = loaded("AppOptions")
            loaded = Nothing
        End If

        For rowIndex = 1 To KwListToParamsMapping.Length - 1
            Dim rule = KeyWordsParams(KwListToParamsMapping(rowIndex))
            Dim image = If(CBool(rule("paused")), IMAGE_PAUSE, Nothing)
            frmMain.dgvKeyWords.Rows.Add(image, rule("label"))
            frmMain.dgvKeyWords.Rows(rowIndex).Cells("kw_item").Style.Padding = ITEM_PADDING
        Next
        frmMain.grpKWBox.Text = String.Format(UiText.T("Ключевые фразы: {0}"), UBound(KwListToParamsMapping))

        Dim columns(dgvTorrentsColumns.Count - 1) As DataGridViewTextBoxColumn
        For Each column In dgvTorrentsColumns
            Dim layout = column.Value
            columns(CInt(layout("index"))) = New DataGridViewTextBoxColumn With {
                .HeaderText = UiText.T(CStr(layout("name"))), .DataPropertyName = column.Key, .Name = column.Key,
                .Width = layout("width"), .Visible = layout("visibility"), .DisplayIndex = layout("index")
            }
        Next
        frmMain.dgvTorrents.Columns.AddRange(columns)
        DGV_BOLD = New DataGridViewCellStyle With {.Font = New Font(DirectCast(AppOptions("dgv_font_bold"), Font), FontStyle.Bold)}
        DGV_NORMAL = New DataGridViewCellStyle With {.Font = New Font(DirectCast(AppOptions("dgv_font_normal"), Font), FontStyle.Regular)}
    End Sub

    Friend Sub MarkSettingsReady()
        settingsReady = True
    End Sub
End Module
