using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Fyltrix.UI;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; init; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true → the "Danger" brush, false → "Accent" (a drive's usage bar turns red when space runs low).</summary>
public sealed class LowSpaceBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Theme.Brush(value is true ? "Danger" : "Accent");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public static class VisualTree
{
    public static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    public static T? FindDescendant<T>(DependencyObject d, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t && (predicate == null || predicate(t))) return t;
            var r = FindDescendant(c, predicate);
            if (r != null) return r;
        }
        return null;
    }

    public static IEnumerable<T> FindDescendants<T>(DependencyObject d) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) yield return t;
            foreach (var x in FindDescendants<T>(c)) yield return x;
        }
    }
}
