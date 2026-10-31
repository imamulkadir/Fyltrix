using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;

namespace Fyltrix.UI;

/// <summary>"An item with this name already exists" prompt used by paste and drag-and-drop.</summary>
public sealed class ConflictDialog : Window
{
    public ConflictChoice Choice { get; private set; } = ConflictChoice.Cancel;
    public bool ApplyToAll { get; private set; }

    private ConflictDialog(Window owner, string target)
    {
        Owner = owner;
        Title = "Name conflict";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Theme.Brush("Bg2");
        Foreground = Theme.Brush("Fg1");
        SourceInitialized += (_, _) => Shell.UseDarkTitleBar(this);

        var stack = new StackPanel { Margin = new Thickness(20), MaxWidth = 520 };
        stack.Children.Add(new TextBlock
        {
            Text = $"'{Path.GetFileName(target)}' already exists in",
            FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Theme.Brush("Fg1"), TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(new TextBlock
        {
            Text = Path.GetDirectoryName(target) ?? "", Foreground = Theme.Brush("Fg2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12),
        });
        try
        {
            var info = new FileInfo(target);
            if (info.Exists)
                stack.Children.Add(new TextBlock
                {
                    Text = $"Existing: {FileOps.FormatSize(info.Length)}, modified {info.LastWriteTime:g}",
                    Foreground = Theme.Brush("Fg3"), Margin = new Thickness(0, 0, 0, 12),
                });
        }
        catch { }

        var all = new CheckBox { Content = "Do this for all remaining conflicts", Foreground = Theme.Brush("Fg1"), Margin = new Thickness(0, 0, 0, 14) };
        stack.Children.Add(all);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        void Add(string text, ConflictChoice c, bool primary = false)
        {
            var b = new Button
            {
                Content = text, MinWidth = 92, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6),
                Background = primary ? Theme.Brush("Sel") : Theme.Brush("Bg3"), IsDefault = primary, IsCancel = c == ConflictChoice.Cancel,
            };
            b.Click += (_, _) => { Choice = c; ApplyToAll = all.IsChecked == true; DialogResult = true; };
            buttons.Children.Add(b);
        }
        Add("Replace", ConflictChoice.Replace);
        Add("Keep both", ConflictChoice.KeepBoth, primary: true);
        Add("Skip", ConflictChoice.Skip);
        Add("Cancel", ConflictChoice.Cancel);
        stack.Children.Add(buttons);
        Content = stack;
    }

    public static (ConflictChoice, bool) Ask(Window owner, string target)
    {
        var d = new ConflictDialog(owner, target);
        return d.ShowDialog() == true ? (d.Choice, d.ApplyToAll) : (ConflictChoice.Cancel, false);
    }
}
