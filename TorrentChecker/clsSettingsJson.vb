Imports System.Globalization
Imports System.IO
Imports System.Text
Imports System.Web.Script.Serialization

Friend Module clsSettingsJson
    Friend Function JsonSerializer() As JavaScriptSerializer
        Return New JavaScriptSerializer With {.MaxJsonLength = Integer.MaxValue}
    End Function

    Private Function Section(data As Dictionary(Of String, Object), key As String) As Dictionary(Of String, Object)
        If Not data.ContainsKey(key) Then Return New Dictionary(Of String, Object)
        Return DirectCast(data(key), Dictionary(Of String, Object))
    End Function

    Private Function Fields(data As Dictionary(Of String, Object), defaults As Dictionary(Of String, Object)) As Dictionary(Of String, Object)
        Return defaults.ToDictionary(Function(field) field.Key,
                                     Function(field) If(data.ContainsKey(field.Key), data(field.Key), field.Value))
    End Function

    Private Function FontFields(font As Font) As Dictionary(Of String, Object)
        Return New Dictionary(Of String, Object) From {
            {"family", font.FontFamily.Name}, {"size", font.Size}, {"style", CInt(font.Style)},
            {"unit", CInt(font.Unit)}, {"charset", font.GdiCharSet}, {"vertical", font.GdiVerticalFont}
        }
    End Function

    Friend Function EncodeSettings(data As Dictionary(Of String, Object)) As Dictionary(Of String, Object)
        Dim window = Fields(Section(data, "frmMain"), MainFormSettings)
        window("windowstate") = CInt(window("windowstate"))
        Dim grid = Section(data, "dgvTorrentsColumns")
        Dim columns As New Dictionary(Of String, Object)
        For Each col In dgvTorrentsColumns
            Dim defaults = DirectCast(col.Value, Dictionary(Of String, Object))
            Dim layout = Fields(Section(grid, col.Key), defaults)
            layout.Remove("type")
            layout.Remove("technical")
            columns.Add(col.Key, layout)
        Next
        Dim sorting = Fields(Section(data, "dgvTorrentsSorting"), dgvTorrentsSorting)
        sorting("sort_order") = CInt(sorting("sort_order"))

        Dim options = Fields(Section(data, "AppOptions"), AppOptions)
        For Each key In {"dgv_font_bold", "dgv_font_normal"}
            options(key) = FontFields(DirectCast(options(key), Font))
        Next

        Dim rules As New Dictionary(Of String, Object)
        If data.ContainsKey("KeyWordsParams") Then
            For Each rule In DirectCast(data("KeyWordsParams"), Dictionary(Of Integer, Dictionary(Of String, String)))
                rules.Add(rule.Key.ToString(CultureInfo.InvariantCulture), rule.Value)
            Next
        Else
            rules.Add("0", KeyWordsParams(0))
        End If
        Return New Dictionary(Of String, Object) From {
            {"window", window}, {"proxy", Fields(Section(data, "proxy"), ProxySettings)},
            {"columns", columns}, {"sorting", sorting}, {"options", options},
            {"ruleOrder", If(data.ContainsKey("KwListToParamsMapping"), data("KwListToParamsMapping"), New Integer() {0})},
            {"rules", rules}, {"checkInterval", If(data.ContainsKey("CHECK_INTERVAL"), data("CHECK_INTERVAL"), CHECK_INTERVAL)},
            {"historyRetention", If(data.ContainsKey("TORRENTS_MAX_STORETIME"), data("TORRENTS_MAX_STORETIME"), TORRENTS_MAX_STORETIME)},
            {"maxResults", If(data.ContainsKey("TORRENTS_MAX_RESULTS"), data("TORRENTS_MAX_RESULTS"), TORRENTS_MAX_RESULTS)}
        }
    End Function

    Friend Function DecodeSettings(wire As Dictionary(Of String, Object)) As Dictionary(Of String, Object)
        Dim window = Fields(Section(wire, "window"), MainFormSettings)
        window("windowstate") = EnumSetting(Of FormWindowState)(window("windowstate"))
        For Each key In {"width", "height", "top", "left", "splitter_distance"}
            'Defaults from older versions can contain half pixels in top/left.
            If (key = "top" OrElse key = "left") AndAlso (TypeOf window(key) Is Decimal OrElse TypeOf window(key) Is Double) Then
                window(key) = CInt(window(key))
            Else
                window(key) = CInt(WholeNumber(window(key)))
            End If
        Next
        If CInt(window("width")) <= 0 OrElse CInt(window("height")) <= 0 OrElse CInt(window("splitter_distance")) < 0 Then
            Throw New InvalidDataException("Неверные размеры окна.")
        End If
        Dim columns As New Dictionary(Of String, Object)
        Dim layouts = Section(wire, "columns")
        Dim indexes As New HashSet(Of Integer)
        For Each col In dgvTorrentsColumns
            Dim defaults = DirectCast(col.Value, Dictionary(Of String, Object))
            Dim column = Fields(Section(layouts, col.Key), defaults)
            Dim index = CInt(WholeNumber(column("index")))
            If index < 0 OrElse index >= dgvTorrentsColumns.Count OrElse Not indexes.Add(index) Then
                Throw New InvalidDataException("Неверный порядок столбцов.")
            End If
            Dim width = CInt(WholeNumber(column("width")))
            If width < 0 OrElse width > 65536 Then Throw New InvalidDataException("Неверная ширина столбца.")
            column("index") = index
            column("width") = width
            column("visibility") = DirectCast(column("visibility"), Boolean)
            column("name") = DirectCast(column("name"), String)
            column("type") = defaults("type")
            column("technical") = defaults("technical")
            columns.Add(col.Key, column)
        Next
        Dim sorting = Fields(Section(wire, "sorting"), dgvTorrentsSorting)
        If Not columns.ContainsKey(DirectCast(sorting("column_name"), String)) Then Throw New InvalidDataException("Неизвестный столбец сортировки.")
        sorting("sort_order") = EnumSetting(Of SortOrder)(sorting("sort_order"))

        Dim options = Fields(Section(wire, "options"), AppOptions)
        For Each key In AppOptions.Keys
            If key <> "dgv_font_bold" AndAlso key <> "dgv_font_normal" Then
                options(key) = DirectCast(options(key), Boolean)
                Continue For
            End If
            If TypeOf options(key) Is Font Then Continue For
            Dim font = DirectCast(options(key), Dictionary(Of String, Object))
            options(key) = New Font(CStr(font("family")), CSng(font("size")), CType(CInt(font("style")), FontStyle),
                                    CType(CInt(font("unit")), GraphicsUnit), CByte(font("charset")), CBool(font("vertical")))
        Next

        Dim rules As New Dictionary(Of Integer, Dictionary(Of String, String))
        For Each rule In Section(wire, "rules")
            rules.Add(Integer.Parse(rule.Key, CultureInfo.InvariantCulture),
                      DirectCast(rule.Value, Dictionary(Of String, Object)).ToDictionary(Function(p) p.Key, Function(p) DirectCast(p.Value, String)))
        Next
        If Not rules.ContainsKey(0) Then rules.Add(0, KeyWordsParams(0))
        Dim order = If(wire.ContainsKey("ruleOrder"), DirectCast(wire("ruleOrder"), IList).
                       Cast(Of Object)().Select(Function(id) CInt(WholeNumber(id))).ToArray(), New Integer() {0})
        If order.Length = 0 OrElse order(0) <> 0 OrElse order.Distinct().Count() <> order.Length OrElse
           rules.Count <> order.Length OrElse order.Any(Function(id) id < 0 OrElse Not rules.ContainsKey(id)) Then
            Throw New InvalidDataException("Неверный порядок ключевых фраз.")
        End If
        For Each id In order
            Dim rule = rules(id)
            If id <> 0 Then Boolean.Parse(rule("paused"))
            Integer.Parse(rule("count_of_new"), CultureInfo.InvariantCulture)
            If rule("label") Is Nothing Then Throw New InvalidDataException("Неверная ключевая фраза.")
        Next
        Dim proxy = Fields(Section(wire, "proxy"), ProxySettings)
        proxy("use_proxy") = DirectCast(proxy("use_proxy"), Boolean)
        proxy("proxy_address") = DirectCast(proxy("proxy_address"), String)
        proxy("proxy_port") = DirectCast(proxy("proxy_port"), String)
        Dim interval = If(wire.ContainsKey("checkInterval"), CInt(WholeNumber(wire("checkInterval"))), CHECK_INTERVAL)
        Dim retention = If(wire.ContainsKey("historyRetention"), WholeNumber(wire("historyRetention")), TORRENTS_MAX_STORETIME)
        Dim maxResults = If(wire.ContainsKey("maxResults"), CShort(WholeNumber(wire("maxResults"))), TORRENTS_MAX_RESULTS)
        If interval <= 0 Then Throw New InvalidDataException("Неверный интервал проверки.")
        Return New Dictionary(Of String, Object) From {
            {"frmMain", window}, {"proxy", proxy},
            {"dgvTorrentsColumns", columns}, {"dgvTorrentsSorting", sorting}, {"AppOptions", options},
            {"KwListToParamsMapping", order}, {"KeyWordsParams", rules},
            {"CHECK_INTERVAL", interval}, {"TORRENTS_MAX_STORETIME", retention}, {"TORRENTS_MAX_RESULTS", maxResults}
        }
    End Function

    Private Function WholeNumber(value As Object) As Long
        If TypeOf value Is Integer OrElse TypeOf value Is Long OrElse TypeOf value Is Short Then Return CLng(value)
        Throw New InvalidDataException("Ожидается целое число.")
    End Function

    Private Function EnumSetting(Of T As Structure)(value As Object) As T
        If TypeOf value Is T Then Return DirectCast(value, T)
        Dim numericValue = CInt(WholeNumber(value))
        If Not [Enum].IsDefined(GetType(T), numericValue) Then Throw New InvalidDataException("Неизвестное значение параметра.")
        Return DirectCast([Enum].ToObject(GetType(T), numericValue), T)
    End Function

    Friend Function CreateHistoryTable(found As Boolean) As DataTable
        Dim table As New DataTable
        If found Then table.Columns.Add("tracker_name")
        table.Columns.Add("tracker_id", GetType(Trackers))
        If found Then table.Columns.Add("found_datetime")
        table.Columns.Add("found_unix_datetime", GetType(Long))
        If found Then
            table.Columns.Add("torrent_status")
            table.Columns.Add("forum_name")
        End If
        table.Columns.Add("topic_id")
        If found Then
            table.Columns.Add("topic_name")
            table.Columns.Add("topic_author")
            table.Columns.Add("torrent_dl")
            table.Columns.Add("torrent_size_dimension")
        End If
        table.Columns.Add("torrent_unique_identifier")
        If found Then
            table.Columns.Add("keyword_id", GetType(Integer))
            table.Columns.Add("marked_as_new", GetType(Boolean))
            table.Columns.Add("torrent_size_bytes", GetType(Double))
        End If
        Return table
    End Function

    Private ReadOnly HistoryTypes As Type() = {GetType(String), GetType(Trackers), GetType(Long), GetType(Integer),
                                                GetType(Boolean), GetType(Double)}

    Friend Function EncodeHistory(table As DataTable) As Object
        Dim schema As New Dictionary(Of String, String)
        For Each col As DataColumn In table.Columns
            If Not HistoryTypes.Contains(col.DataType) Then Throw New InvalidDataException("Неизвестный тип столбца истории.")
            schema.Add(col.ColumnName, col.DataType.Name)
        Next
        Dim rows As New List(Of Object())
        For Each row As DataRow In table.Rows
            rows.Add(row.ItemArray.Select(Function(value) If(value Is DBNull.Value, Nothing, value)).ToArray())
        Next
        'Column order is explicit because JSON object property order is not part of the format.
        Return New With {.columns = schema.Keys.ToArray(), .types = schema, .rows = rows}
    End Function

    Friend Function DecodeHistory(wire As Dictionary(Of String, Object), found As Boolean) As DataTable
        Dim table = CreateHistoryTable(found)
        Dim names = DirectCast(wire("columns"), IList).Cast(Of String)().ToArray()
        If names.Distinct(StringComparer.Ordinal).Count() <> names.Length Then Throw New InvalidDataException("Повторяющийся столбец истории.")
        Dim schema = Section(wire, "types")
        For Each name In names
            Dim columnType = HistoryTypes.SingleOrDefault(Function(t) t.Name = CStr(schema(name)))
            If columnType Is Nothing Then Throw New InvalidDataException("Неизвестный тип столбца истории.")
            If Not table.Columns.Contains(name) Then
                table.Columns.Add(name, columnType)
            ElseIf table.Columns(name).DataType IsNot columnType AndAlso
                   Not (table.Columns(name).DataType Is GetType(Trackers) AndAlso columnType Is GetType(Integer)) Then
                Throw New InvalidDataException("Неверный тип столбца истории.")
            End If
        Next
        For Each raw As IList In DirectCast(wire("rows"), IList)
            If raw.Count <> names.Length Then Throw New InvalidDataException("Неверная строка истории.")
            Dim row = table.NewRow()
            For index = 0 To names.Length - 1
                If raw(index) Is Nothing Then Continue For
                Dim column = table.Columns(names(index))
                row(column) = If(column.DataType Is GetType(Trackers),
                                 CObj(CInt(raw(index))), Convert.ChangeType(raw(index), column.DataType, CultureInfo.InvariantCulture))
            Next
            table.Rows.Add(row)
        Next
        table.AcceptChanges()
        Return table
    End Function

    Friend Function FormatSettings(json As String) As String
        Dim result As New StringBuilder
        Dim depth = 0
        Dim quoted = False
        Dim escaped = False
        For index = 0 To json.Length - 1
            Dim ch = json(index)
            If quoted Then
                result.Append(ch)
                If escaped Then
                    escaped = False
                ElseIf ch = "\"c Then
                    escaped = True
                ElseIf ch = """"c Then
                    quoted = False
                End If
            Else
                Select Case ch
                    Case """"c
                        quoted = True
                        result.Append(ch)
                    Case "{"c, "["c
                        result.Append(ch)
                        depth += 1
                        If json(index + 1) <> "}"c AndAlso json(index + 1) <> "]"c Then result.AppendLine().Append(" "c, depth * 2)
                    Case "}"c, "]"c
                        depth -= 1
                        If json(index - 1) <> "{"c AndAlso json(index - 1) <> "["c Then result.AppendLine().Append(" "c, depth * 2)
                        result.Append(ch)
                    Case ","c
                        result.Append(ch).AppendLine().Append(" "c, depth * 2)
                    Case ":"c
                        result.Append(": ")
                    Case Else
                        result.Append(ch)
                End Select
            End If
        Next
        Return result.AppendLine().ToString()
    End Function
End Module
