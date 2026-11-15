using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using Fyltrix.Core.Pdf;

namespace Fyltrix.UI;

/// <summary>How precisely a comment could be located in the native PDF viewer. Never reports more than was confirmed.</summary>
internal enum RevealLevel
{
    /// <summary>The viewer's page layout couldn't be read (still loading, unsupported runtime, unexpected structure).</summary>
    Unavailable,
    /// <summary>Pages were found but the comment has no usable geometry, or the page couldn't be confirmed.</summary>
    Page,
    /// <summary>Scrolled so the comment is vertically in view and confirmed; its horizontal position isn't trusted.</summary>
    Vertical,
    /// <summary>Scrolled into view and the on-screen rectangle passed every consistency check.</summary>
    Exact,
}

/// <param name="Target">Where the comment is on screen (physical pixels) after scrolling; set for Vertical and Exact.</param>
/// <param name="Viewport">The native document viewport on screen (physical pixels).</param>
internal sealed record RevealResult(RevealLevel Level, Rect? Target, Rect Viewport, string Detail);

/// <summary>
/// Locates and reveals a PDF annotation in the native WebView2 PDF viewer through public UI Automation only
/// (docs/PDF_ANNOTATION_INTERACTION_INVESTIGATION.md): the viewer exposes each page as a region with screen bounds.
/// Pages are identified by structure, not by their localized names, and are accepted only when their count and
/// proportions match the PDF. The annotation's PDF rectangle is mapped onto its page, scrolled to the viewport centre
/// with the document's ScrollPattern, then re-read and checked. Runs on the shared accessibility worker.
/// </summary>
internal static class NativePdfLocator
{
    internal static TimeSpan LayoutWait = TimeSpan.FromSeconds(8);
    private const int SettlePolls = 40, PollMs = 50, MaxScrollAttempts = 4;

    /// <summary>Asks the viewer for its page layout once, in the background, so its accessibility data is being built
    /// before the user picks a comment (the first query switches it on). Result ignored; never scrolls.</summary>
    internal static void Prewarm(IntPtr webView, IReadOnlyList<PdfPageGeometry> pages)
    {
        if (webView == IntPtr.Zero || pages.Count == 0) return;
        _ = NativePdfAccess.OnWorker(() =>
        {
            try { return FindLayout(webView, pages) != null; }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException) { return false; }
        });
    }

    internal static Task<RevealResult> RevealAsync(IntPtr webView, int page, PdfRect? target, IReadOnlyList<PdfPageGeometry> pages, CancellationToken cancellation) =>
        NativePdfAccess.OnWorker(() =>
        {
            try { return Reveal(webView, page, target, pages, cancellation); }
            catch (OperationCanceledException) { return new RevealResult(RevealLevel.Unavailable, null, Rect.Empty, "cancelled"); }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
            { return new RevealResult(RevealLevel.Unavailable, null, Rect.Empty, "accessibility error: " + ex.GetType().Name); }
        });

    private static RevealResult Reveal(IntPtr webView, int page, PdfRect? target, IReadOnlyList<PdfPageGeometry> pages, CancellationToken cancellation)
    {
        if (webView == IntPtr.Zero || page < 1 || page > pages.Count) return new(RevealLevel.Unavailable, null, Rect.Empty, "no viewer or page");
        // The first query switches the browser's accessibility on; the PDF's page layout follows shortly after.
        Layout? layout = null;
        var waited = Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            layout = FindLayout(webView, pages);
            if (layout != null || waited.Elapsed >= LayoutWait) break;
            Thread.Sleep(100);
        }
        if (layout == null) return new(RevealLevel.Unavailable, null, Rect.Empty, "page layout not exposed");

        var geometry = pages[page - 1];
        var viewport = layout.Document.Current.BoundingRectangle;
        if (target is not { } annotation || viewport.IsEmpty) return new(RevealLevel.Page, null, viewport, "no annotation geometry");

        // Scroll so the comment's centre is at the viewport centre (as far as the document allows): measure, scroll,
        // re-measure. A previous (possibly superseded) scroll may still be animating, and right after loading the viewer
        // may still be fitting its layout or ignore a scroll; each attempt therefore starts from a layout at rest and
        // is checked against the result. Bounded: a few attempts, each with a bounded settle.
        var scroll = layout.Scroll;
        Rect pageRect = Rect.Empty, shown = Rect.Empty;
        for (int attempt = 0; attempt < MaxScrollAttempts; attempt++)
        {
            WaitUntilStill(layout, page, cancellation);
            viewport = layout.Document.Current.BoundingRectangle;
            pageRect = layout.PageRect(page);
            shown = PdfAnnotationGeometry.ToDisplay(geometry, annotation, pageRect);
            double viewSize = scroll.Current.VerticalViewSize;
            if (!scroll.Current.VerticallyScrollable || viewSize is <= 0 or >= 100) break; // everything fits: nothing to scroll
            double current = scroll.Current.VerticalScrollPercent;
            double content = viewport.Height * 100 / viewSize;
            double offset = current / 100 * (content - viewport.Height);
            double centreInContent = shown.Top + shown.Height / 2 - viewport.Top + offset;
            double percent = Math.Clamp((centreInContent - viewport.Height / 2) / (content - viewport.Height) * 100, 0, 100);
            double centre = shown.Top + shown.Height / 2;
            bool nearCentre = Math.Abs(centre - (viewport.Top + viewport.Height / 2)) <= viewport.Height * 0.15;
            if (nearCentre || Math.Abs(percent - current) < 0.05) break; // there (or as close as the document allows)
            scroll.SetScrollPercent(ScrollPattern.NoScroll, percent);
        }
        WaitUntilStill(layout, page, cancellation);
        viewport = layout.Document.Current.BoundingRectangle;
        pageRect = layout.PageRect(page);
        shown = PdfAnnotationGeometry.ToDisplay(geometry, annotation, pageRect);

        if (!PdfAnnotationGeometry.MatchesAspect(geometry, pageRect.Width, pageRect.Height, 0.03))
            return new(RevealLevel.Page, null, viewport, "page bounds don't match the page");
        double centreY = shown.Top + shown.Height / 2;
        bool verticallyVisible = centreY >= viewport.Top && centreY <= viewport.Bottom;
        if (!verticallyVisible)
            return new(RevealLevel.Page, null, viewport, $"comment not confirmed in view (centre {centreY:0}, viewport {viewport.Top:0}–{viewport.Bottom:0}, page top {pageRect.Top:0}, scrolled {scroll.Current.VerticalScrollPercent:0.0}% of view {scroll.Current.VerticalViewSize:0.0}%)");

        // Horizontal bounds are trusted only when the page sits where the viewer lays out a page that fits:
        // centred in the viewport (allowing for a vertical scrollbar). Otherwise (zoomed wider than the viewer, or
        // bounds that haven't caught up with a resize) only the vertical position is used.
        double content_width = viewport.Width;
        double offCentre = Math.Abs(pageRect.Left + pageRect.Width / 2 - (viewport.Left + viewport.Width / 2));
        bool centred = pageRect.Width <= content_width && offCentre <= Math.Max(12, viewport.Width * 0.02);
        bool horizontallyVisible = shown.Right > viewport.Left && shown.Left < viewport.Right;
        return centred && horizontallyVisible
            ? new(RevealLevel.Exact, shown, viewport, "exact")
            : new(RevealLevel.Vertical, shown, viewport, centred ? "comment outside the visible width" : "page horizontal position not confirmed");
    }

    /// <summary>Bounded wait until the page's on-screen position is the same on consecutive reads.</summary>
    private static void WaitUntilStill(Layout layout, int page, CancellationToken cancellation)
    {
        double last = layout.PageRect(page).Top; int stable = 0;
        for (int i = 0; i < SettlePolls && stable < 2; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            Thread.Sleep(PollMs);
            double top = layout.PageRect(page).Top;
            stable = top == last ? stable + 1 : 0;
            last = top;
        }
    }

    /// <summary>The native PDF's scrollable document and its page regions, validated against the PDF's pages.</summary>
    private sealed record Layout(AutomationElement Document, ScrollPattern Scroll, AutomationElement[] Pages)
    {
        public Rect PageRect(int page) => Pages[page - 1].Current.BoundingRectangle;
    }

    private static Layout? FindLayout(IntPtr webView, IReadOnlyList<PdfPageGeometry> pages)
    {
        var root = AutomationElement.FromHandle(webView);
        var documents = root.FindAll(TreeScope.Subtree, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        foreach (AutomationElement candidate in documents)
        {
            // The page list: a document whose group children are exactly the PDF's pages, each with the right proportions.
            var groups = candidate.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group));
            if (groups.Count != pages.Count) continue;
            var regions = groups.Cast<AutomationElement>().ToArray();
            bool matches = true;
            for (int i = 0; i < regions.Length && matches; i++)
            {
                var r = regions[i].Current.BoundingRectangle;
                matches = !r.IsEmpty && PdfAnnotationGeometry.MatchesAspect(pages[i], r.Width, r.Height, 0.03);
            }
            if (!matches) continue;
            // Its scroller: the nearest enclosing document that scrolls vertically.
            // (A short document that fits entirely isn't vertically scrollable; its nearest scroll-capable document is used.)
            AutomationElement? fallback = null;
            for (var e = TreeWalker.ControlViewWalker.GetParent(candidate); e != null; e = TreeWalker.ControlViewWalker.GetParent(e))
            {
                if (e.Current.ControlType != ControlType.Document || !e.TryGetCurrentPattern(ScrollPattern.Pattern, out var p)) continue;
                if (((ScrollPattern)p).Current.VerticallyScrollable) return new Layout(e, (ScrollPattern)p, regions);
                fallback ??= e;
            }
            if (fallback != null) return new Layout(fallback, (ScrollPattern)fallback.GetCurrentPattern(ScrollPattern.Pattern), regions);
        }
        return null;
    }
}
