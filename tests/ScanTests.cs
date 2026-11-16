using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
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
    sealed class Handler : HttpMessageHandler
    {
        public readonly List<TaskCompletionSource<HttpResponseMessage>> Calls = new List<TaskCompletionSource<HttpResponseMessage>>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var result = new TaskCompletionSource<HttpResponseMessage>();
            Calls.Add(result); // Deliberately ignore cancellation until the test releases this response.
            return result.Task;
        }
        public void Complete(int index, string id)
        {
            Calls[index].SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "<feed xmlns='http://www.w3.org/2005/Atom'><entry><id>https://rutracker.org/t/" + id +
                "</id><title>Scan " + id + " [1 GB]</title><link rel='enclosure' href='magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567'/></entry></feed>") });
        }
    }
    sealed class Fixture : IDisposable
    {
        readonly Dictionary<FieldInfo, object> originals = new Dictionary<FieldInfo, object>();
        readonly SynchronizationContext context = SynchronizationContext.Current;
        public readonly Handler Handler = new Handler();
        public frmMain Form;
        HttpClient client;
        public int Saves, Sends;
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
                var http = Get(Form, "HTTP");
                var field = typeof(clsHTTP).GetField("http_request", Instance);
                ((HttpClient)field.GetValue(http)).Dispose();
                client = new HttpClient(Handler); field.SetValue(http, client);
                Form.GetType().GetField("ScanSaveSettings", Instance).SetValue(Form, (Action)(() => Saves++));
                Form.GetType().GetField("ScanOpenMagnet", Instance).SetValue(Form, (Action<string>)(value => Sends++));
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
    public static int Run()
    {
        checks = 0;
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "requires STA");
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
        return checks;
    }
}
