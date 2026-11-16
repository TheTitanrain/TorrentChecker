using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using TorrentChecker;

public static class OptionsTests
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks;
    static void Check(bool value, string message) { if (!value) throw new Exception("Options: " + message); checks++; }
    static Control Control(frmAppOptions form, string name) { return form.Controls.Find(name, true)[0]; }
    static object Invoke(frmAppOptions form, string name) { return form.GetType().GetMethod(name, Instance).Invoke(form, null); }

    public static int Run()
    {
        checks = 0;
        Check(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "requires STA");
        var mapping = mdlMain.KwListToParamsMapping;
        var rules = mdlMain.KeyWordsParams;
        var trackers = mdlMain.TrackerParams;
        var context = SynchronizationContext.Current;
        try
        {
            var isolatedTrackers = new Dictionary<mdlMain.Trackers, object>();
            foreach (var item in trackers)
            {
                var tracker = new Dictionary<object, object>((Dictionary<object, object>)item.Value);
                var credentials = new Dictionary<string, string>((Dictionary<string, string>)tracker["credentials"]);
                foreach (var key in new List<string>(credentials.Keys)) credentials[key] = "";
                tracker["credentials"] = credentials;
                isolatedTrackers.Add(item.Key, tracker);
            }
            mdlMain.TrackerParams = isolatedTrackers;
            // Never Show/Load or Save: avoid settings, registry and network side effects.
            using (var form = new frmAppOptions())
            {
                Control(form, "txtCheckInterval").Text = "5";
                Control(form, "txtTorrentsStoreTime").Text = "30";
                Control(form, "txtMaxVisibleResults").Text = "100";
                var auto = (CheckBox)Control(form, "chkAutoChecking");
                foreach (var selection in new[]{"rutracker", "kinozal", "mixed"})
                {
                    mdlMain.KwListToParamsMapping = new[]{0, 1};
                    mdlMain.KeyWordsParams = new Dictionary<int, Dictionary<string, string>> {
                        {0, new Dictionary<string, string>()},
                        {1, new Dictionary<string, string> {
                            {"rutracker", (selection != "kinozal").ToString()},
                            {"kinozal", (selection != "rutracker").ToString()}}}};
                    auto.Checked = true;
                    Check((bool)Invoke(form, "MakeChecks"), selection + " permits auto-check with empty logins");
                    Check(auto.Checked, selection + " retains auto-check selection");
                }
                mdlMain.KwListToParamsMapping = new[]{0};
                mdlMain.KeyWordsParams = new Dictionary<int, Dictionary<string, string>> {{0, new Dictionary<string, string>()}};
                // Exercise the validator used by MakeChecks without opening a modal error dialog.
                Check(!String.IsNullOrEmpty((string)Invoke(form, "GetAutoCheckingError")), "auto-check still requires a rule");
                auto.Checked = false;
                Check(Invoke(form, "GetAutoCheckingError") == null, "disabled auto-check permits no rules");
                Check((bool)Invoke(form, "MakeChecks"), "normal options remain valid without rules");
                foreach (var invalid in new[]{"0", "-1", "1.5", "abc", "", "2147483648"})
                {
                    Control(form, "txtTorrentsStoreTime").Text = invalid;
                    Check(!String.IsNullOrEmpty((string)Invoke(form, "GetRetentionError")), "reject retention " + invalid);
                }
                foreach (var valid in new[]{"1", "30", "99", "2147483647"})
                {
                    Control(form, "txtTorrentsStoreTime").Text = valid;
                    Check(Invoke(form, "GetRetentionError") == null, "accept retention " + valid);
                    Check((bool)Invoke(form, "MakeChecks"), "form accepts retention " + valid);
                }
            }
        }
        finally
        {
            mdlMain.KwListToParamsMapping = mapping;
            mdlMain.KeyWordsParams = rules;
            mdlMain.TrackerParams = trackers;
            SynchronizationContext.SetSynchronizationContext(context);
        }
        return checks;
    }
}
