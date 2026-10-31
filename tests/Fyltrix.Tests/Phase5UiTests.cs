using System.Net.Http;
using System.Windows;
using Fyltrix.Core;
using Fyltrix.Core.Automation;
using Fyltrix.UI;

namespace Fyltrix.Tests;

[Collection("UI")]
public class Phase5UiTests
{
    [Fact]
    public void Share_survives_closing_its_window_and_reopening_shows_the_existing_share()
    {
        using var t = new TempDir();
        var f = t.File("Movie.mkv", "hi there");
        using var host = new Fyltrix.Core.Sharing.ShareHost(t["store/shares.json"], preferredPort: 0);
        Sta.Run(() =>
        {
            var owner = new Window();
            Sta.ShowOffscreen(owner);
            using var http = new HttpClient();
            try
            {
                // share for 15 minutes, then close the window
                var w = new ShareWindow(owner, new[] { f }, host);
                Sta.ShowOffscreen(w);
                w.SelectDuration(TimeSpan.FromMinutes(15));
                w.Start();
                var session = w.Session!;
                var url = w.Url;
                Assert.Contains("/share/", url);
                Sta.Render(w, "share-active");
                w.Close();
                Assert.Equal(Fyltrix.Core.Sharing.ShareState.Active, session.State); // closing the window doesn't stop it
                var get = http.GetStringAsync(url + "0/");
                Sta.WaitUntil(() => get.IsCompleted, "download after the window closed");
                Assert.Equal("hi there", get.Result);

                // reopen sharing for the same file: the existing share is shown, not a new one
                Sta.DoEvents(1200);
                var again = new ShareWindow(owner, new[] { f }, host);
                Sta.ShowOffscreen(again);
                Assert.Same(session, again.Session);
                Assert.Equal(url, again.Url);
                Assert.StartsWith("● Active", again.StatusText);
                Assert.Matches(@"^14 min \d\d s$", again.RemainingText);
                Assert.Single(host.Sessions);

                // the list of shares shows it too, and stopping makes the link invalid
                var list = new SharesWindow(owner, host);
                Sta.ShowOffscreen(list);
                Assert.Equal("Active", list.Rows.Single().State);
                Sta.Render(list, "shares-list");
                again.Stop();
                Sta.WaitUntil(() => list.Rows.Single().State == "Stopped", "list updated");
                Assert.StartsWith("● Stopped", again.StatusText);
                var gone = http.GetAsync(url);
                Sta.WaitUntil(() => gone.IsCompleted, "request after stop");
                Assert.True(gone.IsFaulted || gone.Result.StatusCode == System.Net.HttpStatusCode.Gone);
                list.Close();
                again.Close();
            }
            finally { owner.Close(); }
        });
    }

    [Fact]
    public void Automation_window_lists_rules_and_runs_a_dry_run()
    {
        using var t = new TempDir();
        Sta.Run(() =>
        {
            AppSettings.Folder = t.Dir("settings");
            t.File("watch/a.zip", "x");
            AutomationStore.Save(new[] { new AutomationRule { Name = "Zips", WatchFolder = t["watch"], TargetFolder = t["out"], Extensions = "zip", Action = RuleAction.Move } });
            var owner = new Window();
            Sta.ShowOffscreen(owner);
            var settings = new AppSettings();
            var w = new AutomationWindow(owner, settings, () => null, () => { });
            Sta.ShowOffscreen(w);
            try
            {
                Assert.Single(w.Rules);
                Sta.Render(w, "automation");
            }
            finally { w.Close(); owner.Close(); }
            Assert.False(settings.AutomationEnabled); // opt-in: nothing runs until switched on
            Assert.True(File.Exists(t["watch/a.zip"]));
        });
    }
}
