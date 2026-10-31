using System.Diagnostics;
using System.Text.Json;

namespace Fyltrix.Tests;

/// <summary>
/// Popups open over Fyltrix on Fyltrix' own monitor, even when that is a second monitor with a different scale. Runs
/// Fyltrix.PlacementProbe, which is per-monitor DPI aware like Fyltrix (the test host can't be), puts an invisible
/// (DWM-cloaked, never activated) owner on the secondary monitor and reports where the popup landed. Passes trivially on a
/// single-monitor machine.
/// </summary>
public class MonitorPlacementTests
{
    private static JsonElement Probe(string scenario, bool maximized)
    {
        // same configuration and framework folder as this test assembly, in the probe project
        var exe = Path.Combine(AppContext.BaseDirectory.Replace(@"\Fyltrix.Tests\bin\", @"\Fyltrix.PlacementProbe\bin\"), "Fyltrix.PlacementProbe.exe");
        Assert.True(File.Exists(exe), "probe not built: " + exe);
        using var t = new TempDir();
        t.File("a.txt", "a");
        var outFile = t["result.json"];
        var work = t.Dir("work");
        t.File("work/a.txt", "a");
        using var p = Process.Start(new ProcessStartInfo(exe) { ArgumentList = { scenario, maximized ? "max" : "normal", work, outFile }, UseShellExecute = false })!;
        Assert.True(p.WaitForExit(60000), "probe timed out");
        var json = JsonDocument.Parse(File.ReadAllText(outFile)).RootElement.Clone();
        Assert.False(json.TryGetProperty("error", out var err), err.ToString());
        return json;
    }

    private static int[] Box(JsonElement e, string name) => e.GetProperty(name).EnumerateArray().Select(x => x.GetInt32()).ToArray();
    private static string Show(int[] r) => $"({r[0]},{r[1]})-({r[2]},{r[3]})";
    private static bool Inside(int[] outer, int[] r) => r[0] >= outer[0] && r[1] >= outer[1] && r[2] <= outer[2] && r[3] <= outer[3];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Command_palette_opens_over_the_app_on_its_monitor(bool maximized)
    {
        var r = Probe("palette", maximized);
        if (r.TryGetProperty("skipped", out _)) return;
        int[] work = Box(r, "monitorWork"), visible = Box(r, "visible"), popup = Box(r, "popup");
        string info = $"palette {Show(popup)}, app {Show(visible)}, monitor {Show(work)}; {r}";
        Assert.Equal(maximized, r.GetProperty("ownerZoomed").GetBoolean());
        Assert.True(Inside(work, popup), "not on the app's monitor: " + info);
        Assert.True(Inside(visible, popup), "not inside the app: " + info);
        Assert.InRange((popup[0] + popup[2]) / 2 - (visible[0] + visible[2]) / 2, -3, 3); // centred
        double scale = r.GetProperty("ownerDpi").GetInt32() / 96.0;
        Assert.InRange(popup[1] - visible[1], (int)(70 * scale) - 3, (int)(70 * scale) + 3); // 70 DIPs below the app's top
        Assert.InRange(popup[2] - popup[0], (int)(640 * scale) - 3, (int)(640 * scale) + 3); // 640 DIPs wide at that monitor's scale
    }

    [Theory]
    [InlineData("dialog", false)]
    [InlineData("dialog", true)]
    [InlineData("tool", false)]
    [InlineData("tool", true)]
    [InlineData("compare", false)]
    [InlineData("compare", true)]
    public void Dialogs_and_tool_windows_open_centred_on_the_app(string kind, bool maximized)
    {
        var r = Probe(kind, maximized);
        if (r.TryGetProperty("skipped", out _)) return;
        int[] monitor = Box(r, "monitorWork"), visible = Box(r, "visible"), popup = Box(r, "popup");
        string info = $"{kind} {Show(popup)}, app {Show(visible)}, monitor {Show(monitor)}";
        Assert.True(Inside(monitor, popup) || kind == "compare" && popup[0] >= monitor[0] - 60 && popup[2] <= monitor[2] + 60, "not on the app's monitor: " + info);
        Assert.InRange((popup[0] + popup[2]) / 2 - (visible[0] + visible[2]) / 2, -4, 4);
        Assert.InRange((popup[1] + popup[3]) / 2 - (visible[1] + visible[3]) / 2, -4, 4);
    }

    /// <summary>Clicking the app outside the palette, Esc and running a command all close the palette and leave the app active.
    /// Before, closing during the activation change left no window active and Windows brought another app forward, so Fyltrix
    /// seemed to minimise for a moment.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Closing_the_palette_keeps_the_app_active(bool maximized)
    {
        var r = Probe("paletteclick", maximized);
        string info = r.GetProperty("log").ToString();
        Assert.Equal("app", r.GetProperty("afterClickAway").GetString());
        Assert.Equal("app", r.GetProperty("afterEscape").GetString());
        Assert.Equal("app", r.GetProperty("afterCommand").GetString());
        Assert.True(r.GetProperty("commandRan").GetBoolean(), info);
        Assert.False(r.GetProperty("appMinimized").GetBoolean(), info);
    }
}
