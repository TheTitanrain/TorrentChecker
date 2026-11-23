using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TorrentChecker;

public static class ScanTests
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value, string message) { if (!value) throw new Exception("Scan: " + message); checks++; }
    static object Get(object target, string name)
    {
        var field = target.GetType().GetField(name, Instance);
        return field != null ? field.GetValue(target) : target.GetType().GetProperty(name, Instance).GetValue(target, null);
    }
    static object Invoke(object target, string name, params object[] args) { return target.GetType().GetMethod(name, Instance).Invoke(target, args); }
    static void Pump(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.ElapsedMilliseconds > 5000) throw new Exception("Scan message pump timed out");
            Application.DoEvents();
            Thread.Sleep(1);
        }
    }
    static void Finish(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    const string ClientCommandKey = "client_command_template";
    const string Magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=Test%20Torrent";
    static readonly byte[] TorrentBody = System.Text.Encoding.UTF8.GetBytes("d4:infod4:name8:test.mkvee");
    static readonly Type CommandModule = typeof(frmMain).Assembly.GetType("TorrentChecker.clsTorrentClientCommand", true);

    static string[] ReadArguments(ProcessStartInfo info)
    {
        return ((string[])CommandModule.GetMethod("SplitCommand", Static).
            Invoke(null, new object[] { "client.exe " + info.Arguments })).Skip(1).ToArray();
    }
    sealed class Handler : HttpMessageHandler
    {
        public readonly List<TaskCompletionSource<HttpResponseMessage>> Calls = new List<TaskCompletionSource<HttpResponseMessage>>();
        public readonly List<string> Requests = new List<string>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var result = new TaskCompletionSource<HttpResponseMessage>();
            Requests.Add(request.Method + " " + request.RequestUri.AbsoluteUri);
            Calls.Add(result); // Deliberately ignore cancellation until the test releases this response.
            return result.Task;
        }
        public void Complete(int index, string id)
        {
            CompleteTitles(index, new[]{id}, new[]{"Scan " + id});
        }
        public void CompleteTitles(int index, string[] ids, string[] titles)
        {
            var entries = new System.Text.StringBuilder();
            for (int i = 0; i < ids.Length; i++)
                entries.Append("<entry><id>https://rutracker.org/t/" + ids[i] + "</id><title>" +
                    System.Security.SecurityElement.Escape(titles[i]) + " [1 GB]</title>" +
                    "<link rel='enclosure' href='" + System.Security.SecurityElement.Escape(Magnet) + "'/></entry>");
            Calls[index].SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "<feed xmlns='http://www.w3.org/2005/Atom'>" + entries + "</feed>") });
        }
    }

    sealed class Fixture : IDisposable
    {
        readonly Dictionary<FieldInfo, object> originals = new Dictionary<FieldInfo, object>();
        readonly SynchronizationContext context = SynchronizationContext.Current;
        public readonly Handler Handler = new Handler();
        public frmMain Form;
        HttpClient client;
        public int Saves;
        public readonly List<ProcessStartInfo> Launches = new List<ProcessStartInfo>();
        public int Sends { get { return Launches.Count; } }
        public Action<ProcessStartInfo> OnLaunch;
        public DataGridView Grid { get { return (DataGridView)Get(Form, "dgvKeyWords"); } }
        public System.Windows.Forms.Timer Timer { get { return (System.Windows.Forms.Timer)Get(Form, "tmrStartChecking"); } }
        public string Log { get { return ((RichTextBox)Get(Form, "rtbLogTextBox")).Text; } }
        void Set(Type type, string name, object value)
        {
            var field = type.GetField(name, Static);
            originals.Add(field, field.GetValue(null)); field.SetValue(null, value);
        }
        public Fixture(int ruleCount)
        {
            try
            {
                var main = typeof(mdlMain);
                var settings = main.Assembly.GetType("TorrentChecker.mdlSettings", true);
                Set(settings, "settingsReady", false);
                Set(main, "APP_PAUSED", false);
                Set(main, "KeyWordsSelectedItem", 0);
                Set(main, "ForumsSelected", new Dictionary<mdlMain.Trackers, string>(mdlMain.ForumsSelected));
                Set(main, "FormatsSelected", new Dictionary<mdlMain.Trackers, string>(mdlMain.FormatsSelected));
                Set(main, "LAST_CHECK_TIME", mdlMain.UNIX_TIME_ORIGIN);
                Set(main, "ProxySettings", new Dictionary<string, object> { {"use_proxy", false} });
                var options = new Dictionary<string, object>(mdlMain.AppOptions);
                options["write_fatal_errors"] = false; options["play_sound"] = false;
                Set(main, "AppOptions", options);
                var trackers = new Dictionary<mdlMain.Trackers, object>();
                foreach (var item in mdlMain.TrackerParams)
                    trackers.Add(item.Key, new Dictionary<object, object>((Dictionary<object, object>)item.Value));
                Set(main, "TrackerParams", trackers);
                var schema = main.Assembly.GetType("TorrentChecker.clsSettingsJson").GetMethod("CreateHistoryTable", Static);
                Set(main, "dtFoundTorrents", schema.Invoke(null, new object[]{true}));
                Set(main, "dtStoredTorrents", schema.Invoke(null, new object[]{false}));
                var rules = new Dictionary<int, Dictionary<string,string>>();
                rules[0] = new Dictionary<string,string>{{"label", "All"}, {"count_of_new", "0"}};
                var mapping = new int[ruleCount + 1];
                for (int i = 1; i <= ruleCount; i++)
                {
                    mapping[i] = i;
                    rules[i] = new Dictionary<string,string> { {"label", "Rule " + i}, {"count_of_new", "0"},
                        {"paused", "False"}, {"rutracker", "True"}, {"kinozal", "False"}, {"forums_rutracker", (485+i).ToString()},
                        {"search", ""}, {"additional_filter", "False"}, {"topic_id", ""}, {"torrent_min_size", "0"},
                        {"torrent_max_size", "0"}, {"uniqueness", "False"}, {"send_to_client", "True"} };
                }
                Set(main, "KeyWordsParams", rules); Set(main, "KwListToParamsMapping", mapping);
                // Construct without Show/Load: never read user settings, check updates or contact the network.
                Form = new frmMain();
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Invoke(Form, "InitializeKeyWordsView");
                for (int i = 1; i <= ruleCount; i++) Grid.Rows.Add(null, "Rule " + i);
                ((NotifyIcon)Get(Form, "trayIcon")).Visible = false;
                // RichTextBox needs its native handle to retain AppendText without showing the form.
                var logHandle = ((RichTextBox)Get(Form, "rtbLogTextBox")).Handle;
                var http = Get(Form, "HTTP");
                var field = typeof(clsHTTP).GetField("http_request", Instance);
                ((HttpClient)field.GetValue(http)).Dispose();
                client = new HttpClient(Handler); field.SetValue(http, client);
                Form.GetType().GetField("ScanSaveSettings", Instance).SetValue(Form, (Action)(() => Saves++));
                Form.GetType().GetField("StartTorrentClient", Instance).SetValue(Form, (Action<ProcessStartInfo>)(info =>
                {
                    Launches.Add(info);
                    if (OnLaunch != null) OnLaunch(info);
                }));
            }
            catch { Dispose(); throw; }
        }
        public Task Start() { return (Task)Invoke(Form, "ScanAsync"); }
        public void Dispose()
        {
            if (Form != null) Form.Dispose();
            if (client != null) client.Dispose();
            foreach (var item in originals) item.Key.SetValue(null, item.Value);
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    static void CheckMixedClientCommand()
    {
        using (var f = new Fixture(1))
        {
            var rule = mdlMain.KeyWordsParams[1];
            rule["kinozal"] = "True";
            rule["forums_kinozal"] = "0";
            rule["formats_kinozal"] = "0";
            rule[ClientCommandKey] = @"%ClientPath% /directory ""C:\Downloads\%TorrentName%"" %TorrentSource%";
            var kinozal = (Dictionary<object, object>)mdlMain.TrackerParams[mdlMain.Trackers.kinozal];
            kinozal["is_logged"] = true;
            string magnetClient = @"C:\Magnet Client\client.exe", torrentClient = @"C:\Torrent Client\client.exe";
            var associations = new List<string>();
            f.Form.GetType().GetField("ResolveClient", Instance).SetValue(f.Form, (Func<string, string>)(association =>
            {
                associations.Add(association);
                return association == "magnet" ? magnetClient : torrentClient;
            }));
            string file = null;
            byte[] launchedTorrent = null;
            f.OnLaunch = info =>
            {
                if (info.FileName == torrentClient)
                {
                    file = ReadArguments(info)[2];
                    // Open exclusively to verify TorrentChecker has already closed the written file.
                    using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                    using (var reader = new BinaryReader(stream))
                        launchedTorrent = reader.ReadBytes(TorrentBody.Length + 1);
                }
            };
            try
            {
                var task = f.Start();
                f.Handler.CompleteTitles(0, new[] { "101" }, new[] { "Тест: Раздача" });
                Pump(() => f.Handler.Calls.Count == 2 || task.IsCompleted);
                Check(f.Handler.Calls.Count == 2, "mixed scan requests Kinozal results");
                const string results = @"<table class=""t_peer w100p""><tr><td onclick=""cat(1);"">forum</td><a href=""/details.php?id=202"" class=""r0"">Kinozal test</a><td></td><td>1 GB</td><td></td><td></td><td>today</td><td><a>author</a></td></tr></table>";
                f.Handler.Calls[1].SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(results) });
                Pump(() => f.Handler.Calls.Count == 3 || task.IsCompleted);
                Check(f.Handler.Calls.Count == 3, "mixed scan requests Kinozal info hash: " + f.Log);
                f.Handler.Calls[2].SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<li>Инфо хеш: 9876543210abcdef9876543210abcdef98765432</li>") });
                Pump(() => f.Handler.Calls.Count == 4 || task.IsCompleted);
                Check(f.Handler.Calls.Count == 4 && f.Handler.Requests[3].EndsWith("202"), "mixed scan downloads the Kinozal result");
                var content = new ByteArrayContent(TorrentBody);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-bittorrent");
                f.Handler.Calls[3].SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                Finish(task);

                Check(launchedTorrent != null && launchedTorrent.SequenceEqual(TorrentBody), "client can read the completed torrent at launch");
                Check(associations.Count == 2 && associations.Contains("magnet") && associations.Contains(".torrent"), "mixed rule resolves both associations independently");
                Check(f.Launches.Count == 2 && f.Launches.All(info => !info.UseShellExecute), "mixed template launches each source directly once");
                var magnetInfo = f.Launches.Single(info => info.FileName == magnetClient);
                Check(ReadArguments(magnetInfo).SequenceEqual(new[] { "/directory", @"C:\Downloads\Тест_ Раздача", Magnet }), "same /directory command sends complete magnet and sanitized Atom name");
                var torrentInfo = f.Launches.Single(info => info.FileName == torrentClient);
                Check(file != null && Path.GetExtension(file) == ".torrent" &&
                    ReadArguments(torrentInfo).SequenceEqual(new[] { "/directory", @"C:\Downloads\test.mkv", file }), "same /directory command sends the full .torrent path and torrent name");

                var grid = (DataGridView)Get(f.Form, "dgvTorrents");
                grid.BindingContext = new BindingContext();
                grid.DataSource = mdlMain.dtFoundTorrents;
                grid.ClearSelection();
                foreach (DataGridViewRow row in grid.Rows)
                {
                    var item = row.DataBoundItem as DataRowView;
                    if (item != null && (mdlMain.Trackers)item["tracker_id"] == mdlMain.Trackers.rutracker)
                        row.Selected = true;
                }
                ((ToolStripMenuItem)Get(f.Form, "openMagnetMenu")).PerformClick();
                Check(f.Launches.Count == 3 && f.Launches[2].UseShellExecute && f.Launches[2].FileName == Magnet, "context menu uses MAGNET association despite filled template");
            }
            finally { if (file != null) File.Delete(file); }
        }
    }

    static void CheckClientArgumentsAndFileErrors()
    {
        using (var f = new Fixture(1))
        {
            f.Form.GetType().GetField("ResolveClient", Instance).SetValue(f.Form,
                (Func<string, string>)(association => { throw new Exception("Unexpected association lookup"); }));
            const string executable = @"C:\Клиент с пробелами\client.exe";
            const string name = @"Пример ""имени""\%TorrentSource%\";
            string command = "\"" + executable + "\" --label=%TorrentName% \"\" %TorrentSource%";
            Invoke(f.Form, "OpenMagnet", Magnet, command, name);
            var info = f.Launches.Single();
            Check(!info.UseShellExecute && info.FileName == executable &&
                ReadArguments(info).SequenceEqual(new[] { "--label=" + name, "", Magnet }), "explicit executable and round-trip arguments preserve quotes, trailing slash and literal token");

            Invoke(f.Form, "SaveTorrent", TorrentBody, "  ", "Kinozal");
            var torrent = f.Launches[1];
            try
            {
                Check(torrent.UseShellExecute && Path.GetExtension(torrent.FileName) == ".torrent" &&
                    File.ReadAllBytes(torrent.FileName).SequenceEqual(TorrentBody), "blank command delegates real .torrent file to Windows");
            }
            finally { File.Delete(torrent.FileName); }

            string temp = Environment.GetEnvironmentVariable("TMP");
            try
            {
                Environment.SetEnvironmentVariable("TMP", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
                Invoke(f.Form, "SaveTorrent", TorrentBody, "", "Kinozal");
                Check(f.Launches.Count == 2 && f.Log.Contains("Ошибка записи при сохранении торрент-файла"), "failed torrent write is logged without client launch");
            }
            finally { Environment.SetEnvironmentVariable("TMP", temp); }
        }
    }

    static void CheckRuleCommandEditor()
    {
        using (var f = new Fixture(1))
        using (var editor = new frmKeyWord())
        {
            Invoke(editor, "ResetFields");
            var command = (TextBox)Get(editor, "txtTCCommandLine");
            var label = (TextBox)Get(editor, "txtKeyWordLabel");
            var send = (CheckBox)Get(editor, "chkSendToClient");
            var help = (Label)Get(editor, "lblTCCommandLineHelp");
            var presets = new[] { "btnResetToUT", "btnResetToQB" }
                .Select(name => (Button)Get(editor, name)).ToArray();
            var ruTracker = (CheckBox)Get(editor, "chkRuTracker");
            var kinozalTracker = (CheckBox)Get(editor, "chkKinozal");
            label.Text = "New command rule";
            Check(command.Text == "" && !command.Enabled, "new rule starts with a blank disabled command");
            Check(presets.All(button => !button.Enabled), "client preset buttons are disabled without a tracker");
            var kinozal = (Dictionary<object, object>)mdlMain.TrackerParams[mdlMain.Trackers.kinozal];
            kinozal["credentials"] = new Dictionary<string, string> { { "username", "test" } };
            kinozalTracker.Checked = true;
            Check(presets.All(button => !button.Enabled), "client preset buttons require automatic sending");
            send.Checked = true;
            Check(command.Enabled && presets.All(button => button.Enabled), "Kinozal enables shared command and preset buttons");

            Control position = command;
            var group = command.Parent;
            group.PerformLayout();
            Check(group.Controls.OfType<Button>().Count() == presets.Length, "the behavior section offers only the two expected preset buttons");
            foreach (var button in presets)
            {
                Check(button.TabStop && group.GetNextControl(position, true) == button, "preset buttons follow the command in tab order");
                Check(group.ClientRectangle.Contains(button.Bounds) && button.Top >= help.Bottom &&
                    button.PreferredSize.Width <= button.Width && button.PreferredSize.Height <= button.Height &&
                    !presets.Any(other => other != button && other.Bounds.IntersectsWith(button.Bounds)), "preset button and caption fit below command help");
                position = button;
            }
            Check(help.Right <= group.ClientSize.Width, "command help fits inside the behavior section: " + help.Bounds + ", " + group.ClientSize);
            var save = (Button)Get(editor, "btnSave");
            Check(group.Bottom <= save.Top, "behavior section fits above Save: " + group.Bounds + ", " + save.Bounds);
            Check(editor.ClientRectangle.Contains(group.Bounds), "behavior section fits inside the editor");
            Invoke(editor, "btnSave_Click", null, EventArgs.Empty);
            Check(mdlMain.KeyWordsParams[2][ClientCommandKey] == "", "Kinozal rule saves blank command");

            ruTracker.Checked = true;
            const string customCommand = "%ClientPath% --skip-dialog=true %TorrentSource%";
            command.Text = customCommand;
            Invoke(editor, "btnSave_Click", null, EventArgs.Empty);
            Check(command.Enabled && mdlMain.KeyWordsParams[3][ClientCommandKey] == customCommand,
                "mixed rule saves the manually entered command");
            kinozalTracker.Checked = false;
            Check(command.Enabled && presets.All(button => button.Enabled), "RuTracker-only rule enables the same command and presets");
            ruTracker.Checked = false;
            Check(!command.Enabled && presets.All(button => !button.Enabled), "automatic sending still requires a tracker for client presets");
            ruTracker.Checked = true;
            send.Checked = false;
            Check(!command.Enabled && presets.All(button => !button.Enabled), "turning off automatic sending disables the command and presets");
        }
    }

    static void CheckMissingClientAssociation()
    {
        using (var f = new Fixture(1))
        {
            mdlMain.KeyWordsParams[1][ClientCommandKey] = "%ClientPath% %TorrentSource%";
            f.Form.GetType().GetField("ResolveClient", Instance).SetValue(f.Form, (Func<string, string>)(association => ""));
            var task = f.Start();
            f.Handler.Complete(0, "103");
            Finish(task);
            Check(f.Sends == 0 && f.Log.Contains("Не удалось определить программу для magnet"),
                "missing MAGNET executable logs failure without falling back to another client");
        }
    }

    public static int Run()
    {
        checks = 0;
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "requires STA");
        CheckMixedClientCommand();
        CheckClientArgumentsAndFileErrors();
        CheckRuleCommandEditor();
        CheckMissingClientAssociation();
        using (var f = new Fixture(1))
        {
            mdlMain.KeyWordsParams[1]["rutracker"] = "False";
            mdlMain.KeyWordsParams[1]["kinozal"] = "True";
            var kinozal = (Dictionary<object, object>)mdlMain.TrackerParams[mdlMain.Trackers.kinozal];
            kinozal["credentials"] = new Dictionary<string, string>{{"username", ""}, {"password", ""}};
            kinozal["is_logged"] = false;
            kinozal["is_tracker_failure"] = false;
            var task = f.Start();
            Check(f.Handler.Requests.Count == 1 && f.Handler.Requests[0] == "POST " + kinozal["login_url"], "Kinozal still attempts normal login");
            f.Handler.Calls[0].SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Login rejected") });
            Finish(task);
            Check(!(bool)kinozal["is_logged"] && (bool)kinozal["is_tracker_failure"], "Kinozal failed login is not bypassed");
            Check(f.Handler.Requests.Count == 1 && f.Log.Contains("Ошибка авторизации"), "Kinozal cannot search after rejected login");
            Check(mdlMain.dtFoundTorrents.Rows.Count == 0 && f.Sends == 0, "rejected login cannot publish or send results");
        }
        foreach (string completion in new[]{"cancel", "fault", "success"})
        using (var f = new Fixture(1))
        {
            var a = f.Start(); var b = f.Start();
            Check(f.Handler.Calls.Count == 2 && !a.IsCompleted && !b.IsCompleted, completion + ": overlapping scans");
            var icon = f.Grid.Rows[1].Cells["kw_img"].Value;
            Check(Object.ReferenceEquals(icon, mdlMain.IMAGE_CLOCK), "B working icon");
            if (completion == "cancel") f.Handler.Calls[0].SetCanceled();
            else if (completion == "fault") f.Handler.Calls[0].SetException(new HttpRequestException("obsolete error"));
            else f.Handler.Complete(0, "101");
            Finish(a);
            Check(!b.IsCompleted && !f.Timer.Enabled, completion + ": old finalizer cannot enable timer");
            Check(mdlMain.LAST_CHECK_TIME == mdlMain.UNIX_TIME_ORIGIN, completion + ": old finalizer cannot set time");
            Check(Object.ReferenceEquals(icon, f.Grid.Rows[1].Cells["kw_img"].Value), completion + ": old scan cannot clear B icon");
            Check(mdlMain.dtFoundTorrents.Rows.Count == 0 && mdlMain.dtStoredTorrents.Rows.Count == 0 && f.Saves == 0 && f.Sends == 0 && f.Log == "", completion + ": no stale publication, save, send or log");
            f.Handler.Complete(1, "202"); Finish(b);
            Check(f.Timer.Enabled && mdlMain.LAST_CHECK_TIME > mdlMain.UNIX_TIME_ORIGIN && f.Saves == 1, "B finalizes normally");
            Check(mdlMain.dtFoundTorrents.Rows.Count == 1 && (string)mdlMain.dtFoundTorrents.Rows[0]["topic_id"] == "202" && f.Sends == 1, "only B publishes and sends");
            Check(f.Grid.Rows[1].Cells["kw_img"].Value == null && f.Log == "", "B clears own icon without errors");
        }
        foreach (bool fault in new[]{false, true})
        using (var f = new Fixture(1))
        {
            var task = f.Start();
            Invoke(f.Form, "ApplicationExit"); f.Form.Dispose();
            if (fault) f.Handler.Calls[0].SetException(new HttpRequestException("closed error"));
            else f.Handler.Complete(0, "303");
            Finish(task);
            Check(f.Saves == 0 && f.Sends == 0 && mdlMain.dtFoundTorrents.Rows.Count == 0 && mdlMain.LAST_CHECK_TIME == mdlMain.UNIX_TIME_ORIGIN, "closed scan continuation has no effects");
            Check(f.Start().IsCompleted && f.Handler.Calls.Count == 1, "disposed form cannot start scan");
        }
        using (var f = new Fixture(2))
        {
            var task = f.Start(); Invoke(f.Form, "AppPause", true, false);
            f.Handler.Complete(0, "404"); Finish(task);
            Check(f.Handler.Calls.Count == 1 && mdlMain.dtFoundTorrents.Rows.Count == 1 && f.Sends == 1, "pause finishes current rule only");
            Check(!f.Timer.Enabled && mdlMain.APP_PAUSED && f.Saves == 1, "pause keeps timer stopped");
            var resumed = f.Start();
            Check(!mdlMain.APP_PAUSED && f.Handler.Calls.Count == 2, "resume starts fresh generation");
            f.Handler.Complete(1, "404"); Pump(() => f.Handler.Calls.Count == 3);
            f.Handler.Complete(2, "405"); Finish(resumed);
            Check(f.Timer.Enabled && f.Saves == 2 && f.Sends == 2 && mdlMain.dtFoundTorrents.Rows.Count == 2, "resumed scan completes remaining rules and deduplicates history");
        }
        foreach (string mode in new[]{"syntax", "timeout", "valid"})
        using (var f = new Fixture(2))
        {
            // Seed real published history before introducing a saved rule's invalid filter.
            var seed = f.Start(); f.Handler.CompleteTitles(0, new[]{"501"}, new[]{new string('a', 10000) + "!"});
            Pump(() => f.Handler.Calls.Count == 2); f.Handler.Complete(1, "502"); Finish(seed);
            var existing = mdlMain.dtFoundTorrents.Select("keyword_id=1")[0];
            existing["found_datetime"] = "unchanged existing display";
            var original = existing.ItemArray;
            var stored = mdlMain.dtStoredTorrents.Select("topic_id='501'")[0];
            stored["found_unix_datetime"] = Convert.ToInt64(stored["found_unix_datetime"]) - 60;
            var storedOriginal = stored.ItemArray;
            mdlMain.KeyWordsParams[1]["additional_filter"] = "True";
            mdlMain.KeyWordsParams[1]["additional_filter_text"] = mode == "syntax" ? "[" : "^(a+)+$";
            f.Saves = 0;
            f.Launches.Clear();
            var timeout = mdlMain.REGEX_TIMEOUT;
            try
            {
                mdlMain.REGEX_TIMEOUT = TimeSpan.FromMilliseconds(20);
                var task = f.Start();
                // First title matches; the second must invalidate that earlier match on timeout.
                f.Handler.CompleteTitles(2, new[]{"601", "602", "501"}, new[]{"aaa", mode == "timeout" ? new string('a', 10000) + "!" : "no match", new string('a', 10000) + "!"});
                Pump(() => f.Handler.Calls.Count == 4 || task.IsCompleted);
                Check(!task.IsCompleted && f.Handler.Calls.Count == 4, mode + ": advances to next rule");
                Check(f.Grid.Rows[1].Cells["kw_img"].Value == null, mode + ": clears failed rule icon");
                if (mode == "syntax" || mode == "timeout")
                {
                    Check(f.Sends == 0 && mdlMain.dtFoundTorrents.Rows.Count == 2 && mdlMain.dtStoredTorrents.Rows.Count == 2, mode + ": no partial publication, history or dispatch");
                    Check(System.Linq.Enumerable.SequenceEqual(original, existing.ItemArray), mode + ": existing result unchanged");
                    Check(System.Linq.Enumerable.SequenceEqual(storedOriginal, stored.ItemArray), mode + ": existing history unchanged");
                    Check(mdlMain.KeyWordsParams[1]["count_of_new"] == "1", mode + ": existing unread count unchanged");
                    Check(f.Log.Contains("Ошибка выражения для ручного фильтра"), mode + ": filter error logged: " + f.Log);
                }
                f.Handler.Complete(3, "603"); Finish(task);
                Check(mdlMain.dtFoundTorrents.Select("topic_id='603' AND keyword_id=2").Length == 1 && f.Saves == 1 && f.Timer.Enabled, mode + ": next rule publishes and scan finalizes");
                Check(f.Sends == (mode == "valid" ? 2 : 1), mode + ": only approved results dispatched");
                Check(mdlMain.dtFoundTorrents.Select("topic_id='601'").Length == (mode == "valid" ? 1 : 0) &&
                    mdlMain.dtStoredTorrents.Select("topic_id='601'").Length == (mode == "valid" ? 1 : 0) &&
                    mdlMain.dtFoundTorrents.Select("topic_id='602'").Length == 0 && mdlMain.dtStoredTorrents.Select("topic_id='602'").Length == 0,
                    mode + ": filter result is atomic and valid pattern retains its match");
                Check(f.Grid.Rows[2].Cells["kw_img"].Value == null, mode + ": next rule clears icon");
                if (mode == "valid")
                {
                    Check(f.Log == "", "valid pattern logs no error, stored pathological duplicate is not matched");
                    Check(Convert.ToInt64(stored["found_unix_datetime"]) > Convert.ToInt64(storedOriginal[stored.Table.Columns["found_unix_datetime"].Ordinal]), "valid filter refreshes duplicate history even when its title would not match");
                }
            }
            finally { mdlMain.REGEX_TIMEOUT = timeout; }
        }
        return checks;
    }
}
