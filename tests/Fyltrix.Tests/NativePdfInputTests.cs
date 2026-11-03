using System.Windows;
using Fyltrix.Preview;
using Fyltrix.UI;

namespace Fyltrix.Tests;

[Collection("UI")]
public class NativePdfInputTests
{
    [Theory]
    [InlineData(0x20a, 0, true)]
    [InlineData(0x20a, 8, false)] // Ctrl-wheel must retain independent PDF zoom
    [InlineData(0x20a, 4, false)] // Shift/horizontal gestures keep native behavior
    [InlineData(0x20a, 1, false)] // selected-text dragging is not a mirrored wheel gesture
    [InlineData(0x20e, 0, false)]
    [InlineData(0x100, 0, false)]
    public void Only_plain_vertical_wheel_input_is_forwarded(int message, int modifiers, bool expected)
        => Assert.Equal(expected, NativePdfWheelMirror.PlainWheel(message, new IntPtr((-120 << 16) | modifiers)));

    [Fact]
    public void Native_wheel_coordinates_preserve_negative_monitor_positions()
    {
        int packed = unchecked((int)((uint)(ushort)-30 << 16 | (ushort)-1920));
        Assert.Equal(new Point(-1920, -30), NativePdfWheelMirror.ScreenPoint(new IntPtr(packed)));
    }

    [Theory]
    [InlineData(0x0201, 0, true)] // a new scrollbar/selection gesture supersedes the wheel's owner
    [InlineData(0x0100, 0, true)]
    [InlineData(0x020A, 8, true)] // independent Ctrl-wheel zoom supersedes a preceding wheel
    [InlineData(0x0200, 1, true)]
    [InlineData(0x0200, 0, false)] // passive hovering must not cancel native inertia
    [InlineData(0x000F, 0, false)]
    public void Only_new_input_can_interrupt_a_native_wheel_gesture(int message, int flags, bool expected)
        => Assert.Equal(expected, NativePdfWheelMirror.InterruptsWheel(message, new IntPtr(flags)));

    [Theory]
    [InlineData(1)] [InlineData(-1)]
    public void Fractional_relative_wheel_input_is_preserved_until_a_native_unit_accumulates(int sign)
    {
        double residue = 0;
        for (int i = 0; i < 3; i++) Assert.Equal(0, NativePdfWheelMirror.ScaleDelta(sign, .25, ref residue));
        Assert.Equal(sign, NativePdfWheelMirror.ScaleDelta(sign, .25, ref residue));
        Assert.Equal(0, residue);
        Assert.Equal(360 * sign, NativePdfWheelMirror.ScaleDelta(120 * sign, 3, ref residue));
    }

    [Fact]
    public void Very_long_documents_cannot_overflow_or_leave_a_future_wheel_jump()
    {
        double residue = 0;
        Assert.Equal(short.MaxValue, NativePdfWheelMirror.ScaleDelta(120, 1000, ref residue));
        Assert.Equal(0, residue);
        Assert.Equal(-120, NativePdfWheelMirror.ScaleDelta(-120, 1, ref residue));
    }

    [Fact]
    public void Host_input_is_enabled_only_for_opted_in_pdf_controllers_and_replacement_keeps_normal_input()
    {
        using var temp = new TempDir();
        NativePdfCompareTests.Pdf(temp["sample.pdf"], "Selectable native PDF");
        temp.File("first.txt", "First text"); temp.File("second.txt", "Second text");
        Sta.Run(() =>
        {
            var pane = new PreviewPane(nativePdfInput: true);
            var window = new Window { Content = pane, Width = 1000, Height = 800 };
            try
            {
                pane.Show(temp["sample.pdf"], 0); Sta.ShowOffscreen(window);
                Sta.WaitUntil(() => pane.IsReady, "PDF input controller", 30000);
                Assert.True(pane.HasNativePdfInput);
                var pdfCore = pane.Web.CoreWebView2;
                pane.Show(temp["first.txt"], 0);
                Sta.WaitUntil(() => pane.IsReady, "ordinary replacement controller", 30000);
                Assert.False(pane.HasNativePdfInput);
                var textCore = pane.Web.CoreWebView2;
                Assert.NotSame(pdfCore, textCore);
                pane.Show(temp["second.txt"], 0);
                Assert.Same(textCore, pane.Web.CoreWebView2);
                pane.Show(temp["sample.pdf"], 0);
                Sta.WaitUntil(() => pane.IsReady, "replacement native PDF input controller", 30000);
                Assert.True(pane.HasNativePdfInput);
                Assert.NotSame(textCore, pane.Web.CoreWebView2);
            }
            finally { pane.DisposeWeb(); window.Close(); }
        });
    }
}
