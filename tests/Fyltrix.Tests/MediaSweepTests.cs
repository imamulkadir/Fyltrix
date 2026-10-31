using System.Windows;
using Fyltrix.Preview;

namespace Fyltrix.Tests;

/// <summary>Opt-in (FL_MEDIA=folder): previews every file in a folder and records what each one ended up showing.</summary>
[Collection("UI")]
public class MediaSweepTests
{
    [Fact]
    public void Sweep()
    {
        var dir = Environment.GetEnvironmentVariable("FL_MEDIA");
        if (dir == null) return;
        var log = Path.Combine(dir, "sweep.log");
        File.WriteAllText(log, "");
        Sta.Run(() =>
        {
            using var h = new PreviewUiTests.Host();
            var native = h.Pane.NativePlayerForTest;
            foreach (var f in Directory.GetFiles(dir).Where(f => !f.EndsWith(".log")).OrderBy(f => f))
            {
                h.Pane.Show(f, delayMs: 0);
                var until = DateTime.Now.AddSeconds(20);
                string state = "";
                while (DateTime.Now < until)
                {
                    Sta.DoEvents(250);
                    if (native.IsVisible)
                    {
                        var media = FindChild<System.Windows.Controls.MediaElement>(native)!;
                        var texts = AllText(native).ToList();
                        if (media.NaturalDuration.HasTimeSpan) { state = $"native: playing {media.NaturalVideoWidth}x{media.NaturalVideoHeight} {media.NaturalDuration.TimeSpan:mm\\:ss}"; break; }
                        if (texts.Any(t => t.StartsWith("Windows can't play"))) { state = "native: message: " + texts.First(t => t.StartsWith("Windows can't play")).Replace("\n", " "); break; }
                        continue;
                    }
                    state = h.Eval("(() => { const v = document.querySelector('video,audio'); if (v) return 'web: ' + v.tagName.toLowerCase() + ' ready=' + v.readyState + ' ' + (v.videoWidth||0) + 'x' + (v.videoHeight||0) + ' err=' + (v.error ? v.error.code : '-'); const i = document.querySelector('.stage img, img'); if (i) return 'web: image ' + i.naturalWidth + 'x' + i.naturalHeight; return 'web: ' + (document.getElementById('root')?.innerText||'').slice(0,80).replace(/\\n/g,' '); })()");
                    if (state.Contains("ready=4") || state.Contains("image ") && !state.Contains(" 0x0")) break;
                }
                File.AppendAllText(log, $"{Path.GetFileName(f),-22} {state}\n");
            }
        });
    }

    private static T? FindChild<T>(DependencyObject d) where T : DependencyObject
    {
        foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
        {
            if (c is T t) return t;
            if (FindChild<T>(c) is { } r) return r;
        }
        return null;
    }

    private static IEnumerable<string> AllText(DependencyObject d)
    {
        foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
        {
            if (c is System.Windows.Controls.TextBlock tb && !string.IsNullOrEmpty(tb.Text)) yield return tb.Text;
            foreach (var s in AllText(c)) yield return s;
        }
    }
}
