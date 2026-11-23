using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TorrentChecker;

public static class UpdateTests
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    static int checks;
    static object Get(object target, string name)
    {
        var field = target.GetType().GetField(name, Instance);
        return field != null ? field.GetValue(target) : target.GetType().GetProperty(name, Instance).GetValue(target, null);
    }
    static void Check(bool value, string message) { if (!value) throw new Exception("Updates: " + message); checks++; }
    static void Pump(Func<bool> done)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!done()) { if (DateTime.UtcNow > until) throw new Exception("Update test timed out"); Application.DoEvents(); Thread.Sleep(1); }
    }
    sealed class Handler : HttpMessageHandler
    {
        public readonly List<TaskCompletionSource<HttpResponseMessage>> Calls = new List<TaskCompletionSource<HttpResponseMessage>>();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Check(request.RequestUri.AbsoluteUri == mdlMain.AppRepository["repository_api"], "uses configured release endpoint");
            var pending = new TaskCompletionSource<HttpResponseMessage>(); Calls.Add(pending); return pending.Task;
        }
    }
    sealed class Fixture : IDisposable
    {
        readonly SynchronizationContext oldContext = SynchronizationContext.Current;
        readonly Dictionary<string, object> oldOptions = mdlMain.AppOptions;
        readonly Dictionary<string, object> oldProxy = mdlMain.ProxySettings;
        public readonly Handler Handler = new Handler();
        public readonly frmMain Form;
        readonly HttpClient client;
        public ToolStripMenuItem Menu { get { return (ToolStripMenuItem)Get(Form, "msiCheckUpdates"); } }
        public Label Label { get { return (Label)Get(Form, "lblShowNewVersion"); } }
        public bool Busy { get { return (bool)Get(Form, "updateCheckRunning"); } }
        public Fixture()
        {
            mdlMain.AppOptions = new Dictionary<string, object>(oldOptions); mdlMain.AppOptions["check_for_updates"] = false;
            mdlMain.ProxySettings = new Dictionary<string, object> { {"use_proxy", false} };
            // No Show/Load: do not read settings, save files or use the external network.
            Form = new frmMain();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ((NotifyIcon)Get(Form, "trayIcon")).Visible = false;
            ((NotifyIcon)Get(Form, "trayIconPM")).Visible = false;
            var logHandle = ((RichTextBox)Get(Form, "rtbLogTextBox")).Handle;
            var http = Get(Form, "HTTPUpdate");
            var field = typeof(clsHTTP).GetField("http_request", Instance);
            ((HttpClient)field.GetValue(http)).Dispose();
            client = new HttpClient(Handler); field.SetValue(http, client);
        }
        public Task Start(bool force) { return (Task)Form.GetType().GetMethod("CheckLatestUpdateAsync", Instance).Invoke(Form, new object[] {force}); }
        public void Complete(string json, HttpStatusCode code = HttpStatusCode.OK)
        {
            Handler.Calls[Handler.Calls.Count - 1].SetResult(new HttpResponseMessage(code) { Content = new StringContent(json) });
            Pump(() => !Busy);
        }
        public void Dispose()
        {
            Form.Dispose(); client.Dispose(); mdlMain.AppOptions = oldOptions; mdlMain.ProxySettings = oldProxy;
            SynchronizationContext.SetSynchronizationContext(oldContext);
        }
    }
    public static int Run()
    {
        var ui = typeof(frmMain).Assembly.GetType("TorrentChecker.UiText");
        var select = ui.GetMethod("SelectLanguage", Static);
        var originalLanguage = ui.GetProperty("ActiveCode", Static).GetValue(null, null);
        try
        {
            select.Invoke(null, new object[]{"ru"});
            return RunChecks();
        }
        finally { select.Invoke(null, new object[]{originalLanguage}); }
    }
    static int RunChecks()
    {
        checks = 0;
        using (var f = new Fixture())
        {
            Check(f.Menu.OwnerItem == Get(f.Form, "msiHelp"), "action belongs to Help menu");
            f.Start(false).GetAwaiter().GetResult();
            Check(f.Handler.Calls.Count == 0, "automatic check respects disabled preference");
            f.Menu.PerformClick();
            Check(f.Busy && f.Handler.Calls.Count == 1 && !f.Menu.Enabled, "menu forces check and disables reentry");
            Check(f.Label.Text == "Проверка обновлений..." && f.Label.Cursor == Cursors.Default, "manual progress is not a link");
            f.Menu.PerformClick(); f.Start(true).GetAwaiter().GetResult();
            f.Start(false).GetAwaiter().GetResult();
            Check(f.Handler.Calls.Count == 1, "all entry points share single-flight guard");
            f.Complete("{ \n \"tag_name\" : \"v99.0.0\" }");
            Check(f.Menu.Enabled && f.Label.Text.Contains("v99.0.0") && f.Label.Cursor == Cursors.Hand && f.Label.Font.Underline, "new release becomes a link");
            Check(!(bool)mdlMain.AppOptions["check_for_updates"], "forced check preserves preference");
            foreach (var tag in new[] {mdlMain.APP_VERSION, "1.0", mdlMain.APP_VERSION + ".0"})
            {
                f.Menu.PerformClick(); f.Complete("{\"tag_name\":\"" + tag + "\"}");
                Check(f.Label.Text == "Обновлений нет" && f.Label.Cursor == Cursors.Default && !f.Label.Font.Underline && f.Menu.Enabled, "equal/older release is not a link");
                f.Form.GetType().GetMethod("lblShowNewVersion_Click", Instance).Invoke(f.Form, new object[]{null, EventArgs.Empty});
            }
            foreach (var json in new[] {"", "{", "null", "[]", "{}", "{\"tag_name\":5}", "{\"tag_name\":\"99.0-beta\"}"})
            {
                f.Menu.PerformClick(); f.Complete(json);
                Check(f.Menu.Enabled && f.Label.Text == "Ошибка проверки обновлений" && Get(f.Form, "availableVersion") == null, "invalid response reports error and permits retry");
            }
            foreach (var code in new[] {HttpStatusCode.Forbidden, HttpStatusCode.InternalServerError})
            {
                f.Menu.PerformClick(); f.Complete("{\"tag_name\":\"99.0\"}", code);
                Check(f.Label.Text == "Ошибка проверки обновлений" && f.Menu.Enabled, "HTTP failure is not accepted as a release");
            }
            Check(((RichTextBox)Get(f.Form, "rtbLogTextBox")).Text.Contains("Ошибка проверки обновлений"), "errors are logged");
            f.Menu.PerformClick();
            f.Handler.Calls[f.Handler.Calls.Count - 1].SetException(new HttpRequestException("test network failure")); Pump(() => !f.Busy);
            Check(f.Menu.Enabled && f.Label.Text == "Ошибка проверки обновлений", "network failure restores action");
            f.Menu.PerformClick(); f.Complete("{\"tag_name\":\"99.0\"}");
            Check(f.Label.Cursor == Cursors.Hand, "successful retry recovers from errors");
            mdlMain.AppOptions["check_for_updates"] = true;
            var automatic = f.Start(false);
            Check(!f.Menu.Enabled && !(bool)Get(f.Form, "showUpdateStatus"), "background request disables menu without manual feedback");
            f.Complete("{\"tag_name\":\"1.0\"}"); automatic.GetAwaiter().GetResult();
            Check(Get(f.Form, "availableVersion") == null && !(bool)Get(f.Form, "showUpdateStatus"), "background no-update remains silent");
        }
        var ui = typeof(frmMain).Assembly.GetType("TorrentChecker.UiText");
        var select = ui.GetMethod("SelectLanguage", Static);
        try
        {
            foreach (var pair in new[] {new[]{"ru", "Проверить обновления"}, new[]{"en", "Check for updates"}, new[]{"uk", "Перевірити оновлення"}, new[]{"kk", "Жаңартуларды тексеру"}, new[]{"be", "Праверыць абнаўленні"}})
            using (var f = new Fixture())
            {
                select.Invoke(null, new object[]{pair[0]});
                f.Form.GetType().GetMethod("RefreshLanguage", Instance).Invoke(f.Form, null);
                Check(f.Menu.Text == pair[1], "translated menu: " + pair[0]);
                f.Menu.PerformClick();
                select.Invoke(null, new object[]{"en"}); f.Form.GetType().GetMethod("RefreshLanguage", Instance).Invoke(f.Form, null);
                Check(f.Label.Text == "Checking for updates..." && !f.Menu.Enabled, "language refresh preserves active check");
                f.Complete("{\"tag_name\":\"1.0\"}");
                Check(f.Label.Text == "No updates", "completion uses current language");
            }
        }
        finally { select.Invoke(null, new object[]{"ru"}); }
        foreach (var dispose in new[] {false, true})
        foreach (var fail in new[] {false, true})
        using (var f = new Fixture())
        {
            var task = f.Start(true);
            var count = Get(f.Form, "unread_errors_count");
            if (dispose) f.Form.Dispose();
            else f.Form.GetType().GetField("isFormQueryToUnload", Instance).SetValue(f.Form, true);
            if (fail) f.Handler.Calls[0].SetException(new HttpRequestException("late error"));
            else f.Handler.Calls[0].SetResult(new HttpResponseMessage(HttpStatusCode.OK) {Content = new StringContent("{\"tag_name\":\"99.0\"}")});
            Pump(() => task.IsCompleted); task.GetAwaiter().GetResult();
            Check(Get(f.Form, "availableVersion") == null && count.Equals(Get(f.Form, "unread_errors_count")), "late success/error cannot publish or log after exit/dispose");
        }
        return checks;
    }
}
