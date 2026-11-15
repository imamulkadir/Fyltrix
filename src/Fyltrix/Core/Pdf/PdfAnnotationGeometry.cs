using System.Windows;

namespace Fyltrix.Core.Pdf;

/// <summary>
/// Maps PDF user-space coordinates (points, origin bottom-left) to the page as a viewer displays it: the visible
/// CropBox, turned by /Rotate (clockwise), with the origin at the displayed top-left. Pure arithmetic; the on-screen
/// page rectangle comes from the caller (docs/PDF_ANNOTATION_INTERACTION_INVESTIGATION.md).
/// </summary>
public static class PdfAnnotationGeometry
{
    /// <summary>A PDF point as fractions (0 = left/top, 1 = right/bottom) of the displayed page.</summary>
    public static (double U, double V) ToPageFraction(PdfPageGeometry page, double x, double y)
    {
        var b = page.Box;
        double w = b.Width, h = b.Height;
        return page.Rotate switch
        {
            90 => ((y - b.Bottom) / h, (x - b.Left) / w),
            180 => ((b.Right - x) / w, (y - b.Bottom) / h),
            270 => ((b.Top - y) / h, (b.Right - x) / w),
            _ => ((x - b.Left) / w, (b.Top - y) / h),
        };
    }

    /// <summary>A PDF rectangle as a fraction rectangle (Left, Top, Right, Bottom) of the displayed page. Not clamped:
    /// parts outside the visible box come out below 0 or above 1.</summary>
    public static (double Left, double Top, double Right, double Bottom) ToPageFraction(PdfPageGeometry page, PdfRect rect)
    {
        var a = ToPageFraction(page, rect.Left, rect.Bottom);
        var c = ToPageFraction(page, rect.Right, rect.Top);
        return (Math.Min(a.U, c.U), Math.Min(a.V, c.V), Math.Max(a.U, c.U), Math.Max(a.V, c.V));
    }

    /// <summary>Where <paramref name="rect"/> appears, given where the whole displayed page appears (same units as
    /// <paramref name="displayedPage"/>, e.g. screen pixels).</summary>
    public static Rect ToDisplay(PdfPageGeometry page, PdfRect rect, Rect displayedPage)
    {
        var f = ToPageFraction(page, rect);
        return new Rect(displayedPage.X + f.Left * displayedPage.Width, displayedPage.Y + f.Top * displayedPage.Height,
            (f.Right - f.Left) * displayedPage.Width, (f.Bottom - f.Top) * displayedPage.Height);
    }

    /// <summary>True when a displayed rectangle has this page's proportions (rotation-aware): a check that a viewer's
    /// reported page bounds really belong to this page before anything is drawn from them.</summary>
    public static bool MatchesAspect(PdfPageGeometry page, double width, double height, double tolerance = 0.02)
    {
        if (!(width > 0) || !(height > 0) || page.DisplayWidth <= 0 || page.DisplayHeight <= 0) return false;
        double ratio = width / height / (page.DisplayWidth / page.DisplayHeight);
        return Math.Abs(ratio - 1) <= tolerance;
    }
}
