using System.Net.Http;
using Fyltrix.Core.Sharing;
using Fyltrix.UI;

namespace Fyltrix.Tests;

/// <summary>Closing the main window while something is shared (acceptance test 5). No real tray icon is created.</summary>
[Collection("UI")]
public class SharingLifecycleUiTests
{
    [Fact]
    public void Keep_sharing_hides_the_window_and_Fyltrix_exits_when_the_last_share_ends()
    {
        using var t = new TempDir();
        var f = t.File("work/report.pdf", "%PDF-1.4 test");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t["work"]);
            w.NoTrayForTest = true;
            Assert.NotNull(w.Icon); // the logo is the window icon (title bar, taskbar, Alt+Tab)
            bool closed = false;
            w.Closed += (_, _) => closed = true;
            try
            {
                var host = ShareHost.Current;
                var s = host.Start(new[] { f }, new ShareOptions { Lifetime = TimeSpan.FromMinutes(15) });
                var url = host.Urls(s)[0];

                w.CloseChoiceForTest = () => "keep";
                w.Close();
                Assert.False(closed);
                Assert.False(w.IsVisible);
                Assert.True(w.InBackground);
                using var http = new HttpClient();
                var get = http.GetStringAsync(url + "0/");
                Sta.WaitUntil(() => get.IsCompleted, "download while in the background");
                Assert.Equal("%PDF-1.4 test", get.Result);

                // the last share ends → Fyltrix closes completely, and the link is dead
                host.Stop(s);
                Sta.WaitUntil(() => closed, "Fyltrix exits when sharing ends");
                var after = http.GetAsync(url);
                Sta.WaitUntil(() => after.IsCompleted, "request after exit");
                Assert.True(after.IsFaulted);
            }
            finally { if (!closed) { w.CloseChoiceForTest = () => "stop"; w.Close(); } }
        });
    }

    [Fact]
    public void Stop_and_exit_ends_the_shares_and_cancel_keeps_the_window()
    {
        using var t = new TempDir();
        var f = t.File("work/a.txt", "a");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t["work"]);
            w.NoTrayForTest = true;
            bool closed = false;
            w.Closed += (_, _) => closed = true;
            try
            {
                var s = ShareHost.Current.Start(new[] { f }, new ShareOptions());
                w.CloseChoiceForTest = () => "cancel";
                w.Close();
                Assert.False(closed);
                Assert.True(w.IsVisible);
                Assert.Equal(ShareState.Active, s.State);

                w.CloseChoiceForTest = () => "stop";
                w.Close();
                Assert.True(closed);
                Assert.Equal(ShareState.Stopped, s.State);
            }
            finally { if (!closed) w.Close(); }
        });
    }
}
