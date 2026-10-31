using System.ComponentModel;
using System.Windows;
using Fyltrix.Core;
using Fyltrix.Core.Automation;
using Fyltrix.Core.Sharing;
using Fyltrix.UI;

namespace Fyltrix.Tests;

[Collection("UI")]
public class UpdateShutdownTests
{
    [Fact]
    public void Disposing_automation_does_not_hide_a_write_that_has_not_finished()
    {
        using var t = new TempDir(); string source = t.File("work/a.txt", "content");
        Sta.Run(() =>
        {
            var owner = MainWindowUiTests.OpenWindow(t, t["work"]);
            using var engine = new AutomationEngine();
            try
            {
                var rule = new AutomationRule { Action = RuleAction.Copy, TargetFolder = t.Dir("output") };
                var write = engine.ProcessAsync(rule, source, false, CancellationToken.None);
                engine.Dispose();
                Assert.True(UpdateWork.RunningCount > 0);
                Assert.False(owner.TryCloseForUpdate());
                Sta.WaitUntil(() => write.IsCompleted, "automation write completion");
                Assert.True(File.Exists(t["output/a.txt"]));
                Assert.Equal(0, UpdateWork.RunningCount); Assert.True(owner.TryCloseForUpdate());
            }
            finally { if (owner.IsVisible) owner.Close(); }
        });
    }
    [Fact]
    public void A_closed_image_tool_does_not_hide_its_unfinished_save_from_update_shutdown()
    {
        using var t = new TempDir();
        Sta.Run(() =>
        {
            MediaFixtures.Png(t["pic.png"], MediaFixtures.Marker(400, 200));
            var owner = MainWindowUiTests.OpenWindow(t, t.Path);
            var tool = new ImageToolsWindow(owner, t["pic.png"]); Sta.ShowOffscreen(tool);
            try
            {
                Sta.WaitUntil(() => tool.Current != null, "image load");
                var save = tool.SaveAsync();
                tool.Close();
                Assert.False(save.IsCompleted);
                Assert.True(UpdateWork.RunningCount > 0);
                Assert.False(owner.TryCloseForUpdate()); Assert.True(owner.IsVisible);
                Sta.WaitUntil(() => save.IsCompleted, "image save after tool close");
                Assert.NotNull(save.GetAwaiter().GetResult());
                Assert.Equal(0, UpdateWork.RunningCount);
                Assert.True(owner.TryCloseForUpdate());
            }
            finally { if (tool.IsVisible) tool.Close(); if (owner.IsVisible) owner.Close(); }
        });
    }
    [Fact]
    public void Update_close_preserves_tab_confirmation_and_event_cancellation()
    {
        using var t = new TempDir(); t.File("work/a.txt", "a");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t["work"]);
            try
            {
                w.OpenNewTab(); w.Settings.ConfirmCloseTabs = true;
                int asked = 0; w.ConfirmForTest = n => { asked = n; return false; };
                Assert.False(w.TryCloseForUpdate()); Assert.True(asked >= 2); Assert.True(w.IsVisible);
                w.ConfirmForTest = _ => true;
                CancelEventHandler veto = (_, e) => e.Cancel = true;
                w.Closing += veto;
                Assert.False(w.TryCloseForUpdate()); Assert.True(w.IsVisible);
                w.Closing -= veto;
                Assert.True(w.TryCloseForUpdate());
                Assert.True(File.Exists(Path.Combine(Fyltrix.Core.AppSettings.Folder, "settings.json")));
            }
            finally { if (w.IsVisible) { w.Settings.ConfirmCloseTabs = false; w.Close(); } }
        });
    }
    [Fact]
    public void Update_close_does_not_stop_sharing_or_hide_in_the_tray()
    {
        using var t = new TempDir(); string file = t.File("work/a.txt", "a");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t["work"]); w.NoTrayForTest = true;
            try
            {
                var share = ShareHost.Current.Start([file], new ShareOptions());
                w.CloseChoiceForTest = () => throw new Exception("Update preflight must not enter sharing close choices");
                Assert.False(w.TryCloseForUpdate()); Assert.True(w.IsVisible); Assert.False(w.InBackground);
                Assert.Equal(ShareState.Active, share.State);
                ShareHost.Current.Stop(share);
                Assert.True(w.TryCloseForUpdate());
            }
            finally { if (w.IsVisible) { w.CloseChoiceForTest = () => "stop"; w.Close(); } }
        });
    }
    [Fact]
    public void Update_close_refuses_automation_and_owned_tools_without_changing_preferences()
    {
        using var t = new TempDir(); t.File("work/a.txt", "a");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t["work"]);
            try
            {
                w.Settings.AutomationEnabled = true;
                Assert.False(w.TryCloseForUpdate()); Assert.True(w.Settings.AutomationEnabled);
                w.Settings.AutomationEnabled = false;
                var tool = new Window { Owner = w }; Sta.ShowOffscreen(tool);
                Assert.False(w.TryCloseForUpdate()); Assert.True(tool.IsVisible);
                tool.Close();
                Assert.True(w.TryCloseForUpdate());
            }
            finally { if (w.IsVisible) w.Close(); }
        });
    }
    [Fact]
    public void Update_close_does_not_cancel_an_active_job()
    {
        using var t = new TempDir(); t.File("work/a.txt", "a");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t["work"]);
            using var release = new ManualResetEventSlim(); bool cancelled = false;
            var job = JobWindow.RunAsync(w, "Isolated short job", (_, ct) => { release.Wait(); cancelled = ct.IsCancellationRequested; });
            try
            {
                Assert.True(JobWindow.RunningCount > 0);
                Assert.False(w.TryCloseForUpdate()); Assert.True(w.IsVisible);
            }
            finally { release.Set(); }
            Sta.WaitUntil(() => job.IsCompleted, "job completion"); job.GetAwaiter().GetResult();
            Assert.False(cancelled); Assert.True(w.TryCloseForUpdate());
        });
    }
    [Fact]
    public void Update_safety_is_rechecked_after_a_modal_tab_confirmation()
    {
        using var t = new TempDir(); t.File("work/a.txt", "a");
        Sta.Run(() =>
        {
            var w = MainWindowUiTests.OpenWindow(t, t["work"]);
            try
            {
                w.OpenNewTab(); w.Settings.ConfirmCloseTabs = true;
                w.ConfirmForTest = _ => { w.Settings.AutomationEnabled = true; return true; };
                Assert.False(w.TryCloseForUpdate()); Assert.True(w.IsVisible);
                w.Settings.AutomationEnabled = false; w.ConfirmForTest = _ => true;
                Assert.True(w.TryCloseForUpdate());
            }
            finally { if (w.IsVisible) { w.Settings.ConfirmCloseTabs = false; w.Close(); } }
        });
    }
}
