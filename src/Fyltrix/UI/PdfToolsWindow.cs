using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Fyltrix.Core;
using Fyltrix.Core.Pdf;

namespace Fyltrix.UI;

/// <summary>
/// Page-level PDF editing: reorder, rotate, delete, extract, split, export pages as images; or merge several PDFs.
/// Results are always new files next to the original.
/// </summary>
public sealed class PdfToolsWindow : ToolWindow
{
    public sealed class PageItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private ImageSource? _thumb;
        private int _rotate;
        public required int Source { get; init; }
        public ImageSource? Thumb { get => _thumb; set { _thumb = value; Raise(nameof(Thumb)); } }
        public int Rotate { get => _rotate; set { _rotate = (value % 360 + 360) % 360; Raise(nameof(Rotate)); Raise(nameof(Label)); } }
        public string Label => $"Page {Source + 1}" + (Rotate != 0 ? $" ↻{Rotate}°" : "");
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private readonly string _path;
    private string? _password;
    private readonly ObservableCollection<PageItem> _pages = new();
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Extended, BorderThickness = new Thickness(0) };
    private readonly StackPanel _details = new();
    private readonly TextBlock _status = new() { Foreground = Theme.Brush("Fg2"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly CancellationTokenSource _cts = new();
    private PdfInfo? _info;

    /// <summary>Raised for every file written (so the host can refresh and offer Undo).</summary>
    public event EventHandler<IReadOnlyList<string>>? FilesCreated;

    public PdfToolsWindow(Window owner, string path) : base(owner, "PDF tools – " + Path.GetFileName(path), 1200, 800)
    {
        _path = path;
        var root = new DockPanel { Margin = new Thickness(16) };

        var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        void Tool(string glyph, string text, Action a, string tip)
        {
            var b = Theme.SmallButton(glyph, text, tip);
            b.Background = Theme.Brush("Bg3");
            b.Margin = new Thickness(0, 0, 6, 6);
            b.Click += (_, _) => a();
            bar.Children.Add(b);
        }
        Tool("", "Move earlier", () => MoveSelected(-1), "Move the selected pages one place earlier");
        Tool("", "Move later", () => MoveSelected(+1), "Move the selected pages one place later");
        Tool("", "Rotate left", () => RotateSelected(-90), "Rotate the selected pages 90° anticlockwise");
        Tool("", "Rotate right", () => RotateSelected(90), "Rotate the selected pages 90° clockwise");
        Tool("", "Remove", RemoveSelected, "Leave the selected pages out of the new file");
        Tool("", "Extract selected…", ExtractSelected, "Save only the selected pages as a new PDF");
        Tool("", "Split…", Split, "Split into several PDFs of N pages each");
        Tool("", "Export as PNG", ExportPng, "Save the selected pages as PNG images");
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var save = Dialogs.Button("Save as new PDF…", primary: true);
        save.Click += (_, _) => SaveComposition();
        var reset = Dialogs.Button("Reset");
        reset.Click += (_, _) => LoadPages();
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(reset, Dock.Right);
        bottom.Children.Add(save);
        bottom.Children.Add(reset);
        bottom.Children.Add(_status);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var side = new Border
        {
            Width = 260, Margin = new Thickness(12, 0, 0, 0), Background = Theme.Brush("Bg2"), CornerRadius = new CornerRadius(6), Padding = new Thickness(14),
            Child = new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);

        // thumbnails in a wrapping grid
        var panel = new FrameworkElementFactory(typeof(WrapPanel));
        _list.ItemsPanel = new ItemsPanelTemplate(panel);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var cell = new FrameworkElementFactory(typeof(StackPanel));
        cell.SetValue(WidthProperty, 150.0);
        cell.SetValue(MarginProperty, new Thickness(6));
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetValue(Border.BackgroundProperty, Brushes.White);
        frame.SetValue(HeightProperty, 190.0);
        var img = new FrameworkElementFactory(typeof(Image));
        img.SetBinding(Image.SourceProperty, new Binding(nameof(PageItem.Thumb)));
        img.SetValue(Image.StretchProperty, Stretch.Uniform);
        img.SetBinding(LayoutTransformProperty, new Binding(nameof(PageItem.Rotate)) { Converter = new RotateConverter() });
        frame.AppendChild(img);
        cell.AppendChild(frame);
        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(PageItem.Label)));
        label.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        label.SetValue(MarginProperty, new Thickness(0, 4, 0, 0));
        cell.AppendChild(label);
        _list.ItemTemplate = new DataTemplate { VisualTree = cell };
        _list.ItemsSource = _pages;
        _list.SelectionChanged += (_, _) => UpdateStatus();
        root.Children.Add(_list);
        Content = root;

        Closed += (_, _) => _cts.Cancel();
        Loaded += (_, _) => Open();
    }

    internal IList<PageItem> Pages => _pages;

    private void Open()
    {
        while (true)
        {
            try
            {
                _info = PdfService.Info(_path, _password);
                break;
            }
            catch (PdfPasswordException ex)
            {
                _password = Dialogs.AskPassword(this, Path.GetFileName(_path), ex.WasWrong);
                if (_password == null) { Close(); return; }
            }
            catch (Exception ex)
            {
                Dialogs.Error(this, "PDF tools", "This PDF can't be opened: " + ex.Message);
                Close();
                return;
            }
        }
        _details.Children.Clear();
        _details.Children.Add(Dialogs.Heading("Document"));
        void Row(string k, string v)
        {
            _details.Children.Add(new TextBlock { Text = k, Foreground = Theme.Brush("Fg2"), FontSize = 11.5 });
            _details.Children.Add(new TextBlock { Text = v, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        }
        Row("Pages", _info.Pages.ToString());
        Row("PDF version", $"{_info.Version / 10}.{_info.Version % 10}");
        Row("File size", FileOps.FormatSize(new FileInfo(_path).Length));
        foreach (var (k, v) in _info.Metadata) Row(k, v);
        if (_info.Signed)
            _details.Children.Add(Dialogs.Note("⚠ This PDF is digitally signed. Any new file made here is no longer signed (the original keeps its signature).", "Star"));
        if (_info.Encrypted)
            _details.Children.Add(Dialogs.Note("🔒 This PDF is encrypted. New files made here are saved without encryption.", "Star"));
        LoadPages();
    }

    private void LoadPages()
    {
        if (_info == null) return;
        _pages.Clear();
        for (int i = 0; i < _info.Pages; i++) _pages.Add(new PageItem { Source = i });
        UpdateStatus();
        var items = _pages.ToList();
        var path = _path;
        var pw = _password;
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            foreach (var p in items)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var bmp = PdfService.RenderPage(path, p.Source, 300, pw);
                    await Dispatcher.InvokeAsync(() => p.Thumb = bmp);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* leave the thumbnail blank */ }
            }
        }, ct);
    }

    private List<PageItem> Selected() => _list.SelectedItems.Cast<PageItem>().OrderBy(p => _pages.IndexOf(p)).ToList();

    private void UpdateStatus()
    {
        int sel = _list.SelectedItems.Count;
        _status.Text = $"{_pages.Count} page(s) in the new document" + (_info != null && _pages.Count != _info.Pages ? $" (original has {_info.Pages})" : "")
                       + (sel > 0 ? $" · {sel} selected" : " · select pages to move, rotate, remove or extract them");
    }

    internal void Select(params int[] positions)
    {
        _list.SelectedItems.Clear();
        foreach (var p in positions) _list.SelectedItems.Add(_pages[p]);
    }

    internal void MoveSelected(int delta)
    {
        var sel = Selected();
        if (sel.Count == 0) return;
        var ordered = delta < 0 ? sel : Enumerable.Reverse(sel).ToList();
        foreach (var p in ordered)
        {
            int i = _pages.IndexOf(p), j = i + delta;
            if (j < 0 || j >= _pages.Count || sel.Contains(_pages[j])) continue;
            _pages.Move(i, j);
        }
        foreach (var p in sel) _list.SelectedItems.Add(p);
        UpdateStatus();
    }

    internal void RotateSelected(int degrees) { foreach (var p in Selected()) p.Rotate += degrees; }

    internal void RemoveSelected()
    {
        var sel = Selected();
        if (sel.Count >= _pages.Count) { Dialogs.Error(this, "PDF tools", "A PDF needs at least one page."); return; }
        foreach (var p in sel) _pages.Remove(p);
        UpdateStatus();
    }

    private string? AskOutput(string suggested)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the new PDF", Filter = "PDF|*.pdf", InitialDirectory = Path.GetDirectoryName(_path),
            FileName = Path.GetFileName(FileOps.UniqueName(Path.GetDirectoryName(_path)!, suggested)), OverwritePrompt = false,
        };
        if (dlg.ShowDialog(this) != true) return null;
        if (string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase) || File.Exists(dlg.FileName))
        {
            Dialogs.Error(this, "PDF tools", "Choose a new file name; existing files (including the original) are never overwritten.");
            return null;
        }
        return dlg.FileName;
    }

    private string Stem => Path.GetFileNameWithoutExtension(_path);

    internal string? SaveComposition(string? output = null)
    {
        output ??= AskOutput(Stem + " (edited).pdf");
        if (output == null) return null;
        return Run(() => PdfService.Compose(_path, _pages.Select(p => (p.Source, p.Rotate)).ToList(), output, _password), new[] { output });
    }

    private void ExtractSelected()
    {
        var sel = Selected();
        if (sel.Count == 0) { _status.Text = "Select the pages to extract first."; return; }
        var output = AskOutput($"{Stem} (pages {string.Join(",", sel.Select(p => p.Source + 1))}).pdf");
        if (output == null) return;
        Run(() => PdfService.Compose(_path, sel.Select(p => (p.Source, p.Rotate)).ToList(), output, _password), new[] { output });
    }

    private void Split()
    {
        var n = Dialogs.Prompt(this, "Split PDF", "Pages per file", "1", s => int.TryParse(s, out var v) && v >= 1 ? null : "Enter a whole number of 1 or more.");
        if (n == null) return;
        try
        {
            var files = PdfService.Split(_path, int.Parse(n), Path.GetDirectoryName(_path)!, _password, _cts.Token);
            FilesCreated?.Invoke(this, files);
            _status.Text = $"Split into {files.Count} file(s) next to the original.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PdfSharp.Pdf.IO.PdfReaderException)
        {
            Dialogs.Error(this, "Split", ex.Message);
        }
    }

    private async void ExportPng()
    {
        var sel = Selected();
        if (sel.Count == 0) sel = _pages.ToList();
        var dir = Path.GetDirectoryName(_path)!;
        var path = _path;
        var pw = _password;
        _status.Text = $"Exporting {sel.Count} page(s)…";
        try
        {
            var files = await Task.Run(() => sel.Select(p =>
            {
                var output = FileOps.UniqueName(dir, $"{Stem} - page {p.Source + 1}.png");
                var bmp = PdfService.RenderPage(path, p.Source, 1600, pw);
                if (p.Rotate != 0) bmp = Core.Imaging.ImageService.Rotate(bmp, p.Rotate);
                Core.Imaging.ImageService.Save(bmp, output, Core.Imaging.ImageFormat.Png);
                return output;
            }).ToList());
            FilesCreated?.Invoke(this, files);
            _status.Text = $"Saved {files.Count} PNG file(s) (1600 px wide) next to the PDF.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Dialogs.Error(this, "Export", ex.Message);
        }
    }

    private string? Run(Action a, IReadOnlyList<string> outputs)
    {
        try
        {
            a();
            FilesCreated?.Invoke(this, outputs);
            _status.Text = $"Saved {Path.GetFileName(outputs[0])}.";
            return outputs[0];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or PdfSharp.Pdf.IO.PdfReaderException)
        {
            Dialogs.Error(this, "PDF tools", "The PDF couldn't be saved: " + ex.Message);
            return null;
        }
    }

    private sealed class RotateConverter : IValueConverter
    {
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => new RotateTransform(value is int d ? d : 0);
        public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }
}

/// <summary>Merge several PDFs (in a chosen order) into one new file.</summary>
public sealed class PdfMergeDialog : DialogWindow
{
    public string? Output { get; private set; }

    public PdfMergeDialog(Window owner, IReadOnlyList<string> files) : base(owner, "Merge PDFs", 560)
    {
        var list = new ListBox { Height = 220 };
        foreach (var f in files) list.Items.Add(new ListBoxItem { Content = Path.GetFileName(f), Tag = f, ToolTip = f });
        list.SelectedIndex = 0;
        AddContent(Dialogs.Label("Order of the documents (top first)"));
        var row = new DockPanel();
        var buttons = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        void Move(int d)
        {
            int i = list.SelectedIndex, j = i + d;
            if (i < 0 || j < 0 || j >= list.Items.Count) return;
            var it = list.Items[i];
            list.Items.RemoveAt(i);
            list.Items.Insert(j, it);
            list.SelectedIndex = j;
        }
        var up = Dialogs.Button("Up"); up.Margin = new Thickness(0, 0, 0, 6); up.Click += (_, _) => Move(-1);
        var down = Dialogs.Button("Down"); down.Margin = new Thickness(0); down.Click += (_, _) => Move(1);
        buttons.Children.Add(up);
        buttons.Children.Add(down);
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(list);
        AddContent(row);
        var name = new TextBox { Text = Path.GetFileName(FileOps.UniqueName(Path.GetDirectoryName(files[0])!, "Merged.pdf")) };
        AddContent(Dialogs.Field("New file name (saved in the same folder)", name));
        var err = Dialogs.Note("", "Danger");
        AddContent(err);
        AddButton("Merge", () =>
        {
            var n = name.Text.Trim();
            if (!n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) n += ".pdf";
            var problem = FileOps.ValidateName(n);
            var target = Path.Combine(Path.GetDirectoryName(files[0])!, n);
            if (problem == null && FileOps.Exists(target)) problem = $"'{n}' already exists.";
            if (problem != null) { err.Text = problem; return; }
            var ordered = list.Items.Cast<ListBoxItem>().Select(i => (string)i.Tag).ToList();
            try
            {
                PdfService.Merge(ordered, target);
                Output = target;
                DialogResult = true;
            }
            catch (PdfPasswordException) { err.Text = "One of the PDFs is password protected; open it in PDF tools first and extract its pages."; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PdfSharp.Pdf.IO.PdfReaderException)
            {
                err.Text = ex.Message;
            }
        }, primary: true);
        AddButton("Cancel", () => DialogResult = false, cancel: true);
    }
}
