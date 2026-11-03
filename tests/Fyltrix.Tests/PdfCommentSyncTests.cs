using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using Fyltrix.Core;
using Fyltrix.UI;

namespace Fyltrix.Tests;

[Collection("UI")]
public class PdfCommentSyncTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public PdfCommentSyncTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private static Task<double> Percent(IntPtr handle, double? value = null) => Task.Run(() =>
    {
        if (value != null)
        {
            // Exercise source input from a separate accessibility client, like an assistive
            // tool. Establish only this source position, retrying ignored native input actions;
            // target synchronization is never driven or retried by the input helper.
            var start=new ProcessStartInfo("powershell.exe") { UseShellExecute=false, CreateNoWindow=true,
                RedirectStandardOutput=true, RedirectStandardError=true };
            foreach(var argument in new[]{"-NoProfile","-NonInteractive","-Command"})start.ArgumentList.Add(argument);
            string goal=value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.ArgumentList.Add("$ErrorActionPreference='Stop'; Add-Type -AssemblyName UIAutomationClient; Add-Type -AssemblyName UIAutomationTypes; " +
                "$root=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]"+handle.ToInt64()+"); "+
                "foreach($element in $root.FindAll([System.Windows.Automation.TreeScope]::Subtree,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Document))) { "+
                "$pattern=$null; if($element.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern,[ref]$pattern) -and $pattern.Current.VerticallyScrollable) { "+
                "$pattern.SetScrollPercent(-1,"+goal+"); $watch=[Diagnostics.Stopwatch]::StartNew(); $lastSend=0; do {Start-Sleep -Milliseconds 20; $current=$pattern.Current.VerticalScrollPercent; "+
                "if([Math]::Abs($current-"+goal+") -gt .2 -and $watch.ElapsedMilliseconds-$lastSend -ge 500){$pattern.SetScrollPercent(-1,"+goal+");$lastSend=$watch.ElapsedMilliseconds}}while([Math]::Abs($current-"+goal+") -gt .2 -and $watch.ElapsedMilliseconds -lt 5000); "+
                "if([Math]::Abs($current-"+goal+") -gt .2){throw 'Source scroll action did not complete.'}; exit 0 }}; throw 'Native scroll surface not found.'");
            using var process=Process.Start(start)!;
            var errors=process.StandardError.ReadToEndAsync();
            var output=process.StandardOutput.ReadToEndAsync();
            if(!process.WaitForExit(10000)) {process.Kill();throw new TimeoutException("External native input driver timed out.");}
            if(process.ExitCode!=0)throw new InvalidOperationException("External native input: "+errors.GetAwaiter().GetResult());
            output.GetAwaiter().GetResult();
        }
        var root = AutomationElement.FromHandle(handle);
        var watch=Stopwatch.StartNew();
        while(watch.ElapsedMilliseconds<8000)
        {
            foreach (AutomationElement element in root.FindAll(TreeScope.Subtree,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Document)))
                if(element.TryGetCurrentPattern(ScrollPattern.Pattern,out var candidate) && ((ScrollPattern)candidate).Current.VerticallyScrollable)
                {
                    var pattern=(ScrollPattern)candidate;
                    return pattern.Current.VerticalScrollPercent;
                }
            Thread.Sleep(20);
        }
        throw new TimeoutException("Native document scroll surface not ready.");
    });

    private static double Wait(Task<double> task)
    {
        Sta.WaitUntil(() => task.IsCompleted,"public native scroll provider",15000);
        return task.GetAwaiter().GetResult();
    }

    private static void At(IntPtr handle, double percent)
    {
        var watch=Stopwatch.StartNew();
        while(watch.ElapsedMilliseconds<10000)
        {
            if(Math.Abs(Wait(Percent(handle))-percent)<.2)return;
            Sta.DoEvents(30);
        }
        Assert.Fail($"Native PDF did not reach {percent}% (actual {Wait(Percent(handle))}).");
    }

    [Fact]
    public void Native_pdfs_synchronize_both_directions_without_feedback_and_can_be_disabled()
    {
        using var temp=new TempDir();
        NativePdfCompareTests.Pdf(temp["left.pdf"],"Left",3);
        NativePdfCompareTests.Pdf(temp["right.pdf"],"Right",8);
        Sta.Run(() =>
        {
            var owner=new Window();Sta.ShowOffscreen(owner);
            var compare=new CompareWindow(owner,temp["left.pdf"],temp["right.pdf"]){Width=1400,Height=900};
            NativePdfAccess? access=null;
            var trace=new System.Collections.Concurrent.ConcurrentQueue<string>();
            try
            {
                Sta.ShowOffscreen(compare);
                Sta.WaitUntil(()=>compare.SyncScrollCheck.IsEnabled,"native accessibility ready",30000);
                access=compare.PdfAccess;
                Assert.NotNull(access);
                access.Trace=trace.Enqueue;
                var left=compare.LeftPane.Web.Handle;var right=compare.RightPane.Web.Handle;
                Wait(Percent(left,37));At(left,37);At(right,37);
                Wait(Percent(right,72));At(right,72);At(left,72);
                compare.SyncScrolling=false;
                Wait(Percent(left,19));At(left,19);Sta.DoEvents(200);At(right,72);
                compare.SyncScrolling=true;At(right,19);
                // Repeatedly replace native accessibility documents on both sides. Registration
                // must stay on the same background apartment across old/new session lifetimes.
                for (int reload = 0; reload < 3; reload++)
                {
                    var core=(reload % 2 == 0 ? compare.LeftPane : compare.RightPane).Web.CoreWebView2;
                    bool reloaded=false;
                    EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs> completed=(_,args)=>reloaded=args.IsSuccess;
                    core.NavigationCompleted+=completed;
                    try
                    {
                        core.Reload();
                        Sta.WaitUntil(()=>reloaded&&compare.SyncScrollCheck.IsEnabled,"PDF reload reconnected",30000);
                    }
                    finally { core.NavigationCompleted-=completed; }
                    Assert.Same(access,compare.PdfAccess);
                    Wait(Percent(left,48));At(left,48);At(right,48);
                    Wait(Percent(right,61));At(right,61);At(left,61);
                    Sta.DoEvents(300);
                    Assert.InRange(access.AppliedUpdates,1,40);
                    _output.WriteLine($"Reload {reload+1}: left={Wait(Percent(left))}, right={Wait(Percent(right))}; target updates={access.AppliedUpdates}.");
                }
                // Choosing a comment page in the opposite pane must supersede a wheel's
                // quiet-period correction; the old wheel must not pull that page back.
                access.BeginNativeWheel(0);
                access.GoToPage(1,3,Enumerable.Repeat(792d,8).ToArray());
                Sta.WaitUntil(() => Wait(Percent(right)) is > 25 and < 31,"explicit comment navigation supersedes wheel",10000);
                double navigation = Wait(Percent(right));
                At(left,navigation);
                Sta.DoEvents(800);
                At(right,navigation);At(left,navigation);
                // A transient wheel can leave the peer behind while the requested source
                // page is already selected. Clicking that same comment must still align it.
                access.BeginNativeWheel(0);
                Wait(Percent(left,15));At(left,15);
                access.GoToPage(1,3,Enumerable.Repeat(792d,8).ToArray());
                At(left,navigation);At(right,navigation);
                _output.WriteLine($"Bidirectional relative synchronization: {access.AppliedUpdates} target updates; page counts 3 and 8.");
            }
            finally {_output.WriteLine(string.Join("\n",trace));compare.Close();owner.Close();}
            if(access!=null)Sta.WaitUntil(()=>access.Completion.Task.IsCompleted,"native event subscriptions released",10000);
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Comments_belong_to_their_document_and_empty_state_is_readable(bool rightComment)
    {
        using var temp=new TempDir();
        var left=PdfCommentTests.Fixture(temp,"left.pdf","Visible left comment","Left author");
        if(rightComment)PdfCommentTests.Fixture(temp,"right.pdf","Different right comment","Right author");
        else NativePdfCompareTests.Pdf(temp["right.pdf"],"Right");
        Sta.Run(() =>
        {
            var owner=new Window();Sta.ShowOffscreen(owner);
            var compare=new CompareWindow(owner,left,temp["right.pdf"]){Width=1400,Height=900};
            try
            {
                Sta.ShowOffscreen(compare);
                Sta.WaitUntil(()=>compare.LeftComments.LoadTask.IsCompleted&&compare.RightComments.LoadTask.IsCompleted,"comments loaded");
                Assert.Equal("Visible left comment",Assert.Single(compare.LeftComments.Comments).Text);
                Assert.Equal("Left author",compare.LeftComments.Comments[0].Author);
                Assert.False(compare.LeftCommentsFlyout.Popup.IsOpen);
                Assert.False(compare.RightCommentsFlyout.Popup.IsOpen);
                Assert.Equal(Visibility.Visible, compare.LeftCommentsFlyout.Button.Visibility);
                Assert.Contains(VisualTree.FindDescendants<System.Windows.Controls.TextBlock>(compare.LeftCommentsFlyout.Button),
                    text => text.Text == "Comments (1)");
                Assert.Empty(VisualTree.FindDescendants<PdfCommentsPanel>(compare)); // no permanent panel under either PDF
                if(rightComment)
                {
                    Assert.Equal("Different right comment",Assert.Single(compare.RightComments.Comments).Text);
                    Assert.Equal("Right author",compare.RightComments.Comments[0].Author);
                }
                else
                {
                    Assert.Empty(compare.RightComments.Comments);
                    Assert.Contains("No comments",compare.RightComments.Status);
                }
                Sta.Render(compare,"pdf-comments-flyouts-closed");
            }
            finally{compare.Close();owner.Close();}
        });
    }

    [Fact]
    public void Replacing_comment_source_ignores_a_previous_load()
    {
        using var temp=new TempDir();
        var first=PdfCommentTests.Fixture(temp,"first.pdf","First comment");
        var second=PdfCommentTests.Fixture(temp,"second.pdf","Second comment");
        Sta.Run(()=>
        {
            var panel=new PdfCommentsPanel();
            panel.ShowFile(first);
            panel.ShowFile(second);
            Sta.WaitUntil(()=>panel.LoadTask.IsCompleted,"replacement comments");
            Assert.Equal("Second comment",Assert.Single(panel.Comments).Text);
            panel.ShowFile(temp["not-pdf.txt"]);
            Assert.Empty(panel.Comments);
            Assert.Equal(Visibility.Collapsed,panel.Visibility);
        });
    }
}
