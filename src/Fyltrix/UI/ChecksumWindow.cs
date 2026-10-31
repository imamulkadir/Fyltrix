using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fyltrix.Core;
using Fyltrix.Core.Tools;

namespace Fyltrix.UI;

/// <summary>Compute SHA-256/512 (and legacy SHA-1/MD5), compare with a published value, verify or write checksum files.</summary>
public sealed class ChecksumWindow : ToolWindow
{
    public sealed class Row
    {
        public required string File { get; init; }
        public required string Algorithm { get; init; }
        public required string Hash { get; init; }
        public string Name => Path.GetFileName(File);
        public string Result { get; set; } = "";
        public Brush ResultBrush { get; set; } = Theme.Brush("Fg2");
    }

    private readonly List<string> _files;
    private readonly Dictionary<HashAlgorithmKind, CheckBox> _algos = new();
    private readonly ListView _list;
    private readonly TextBox _expected = new();
    private readonly TextBlock _verdict = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _bar = new() { Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, Maximum = 1 };
    private readonly Button _compute;
    private List<Row> _rows = new();
    private CancellationTokenSource? _cts;

    public ChecksumWindow(Window owner, IEnumerable<string> files) : base(owner, "Checksums", 1000, 620)
    {
        _files = files.Where(File.Exists).ToList();
        var root = new DockPanel { Margin = new Thickness(16) };
        var top = new StackPanel();
        top.Children.Add(Dialogs.Note(_files.Count == 1 ? _files[0] : $"{_files.Count} files"));
        var algos = new WrapPanel();
        foreach (var k in Enum.GetValues<HashAlgorithmKind>())
        {
            var cb = new CheckBox { Content = FileHasher.Name(k), IsChecked = k == HashAlgorithmKind.Sha256, Margin = new Thickness(0, 0, 18, 0) };
            _algos[k] = cb;
            algos.Children.Add(cb);
        }
        _compute = Dialogs.Button("Compute", primary: true);
        _compute.Click += async (_, _) => await ComputeAsync();
        algos.Children.Add(_compute);
        top.Children.Add(algos);
        top.Children.Add(_bar);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var bottom = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var cmp = new DockPanel();
        var verifyFile = Dialogs.Button("Verify a checksum file…");
        verifyFile.Click += async (_, _) => await VerifyFileAsync();
        var save = Dialogs.Button("Save .sha256 file");
        save.Click += (_, _) => SaveChecksumFile();
        var copy = Dialogs.Button("Copy");
        copy.Click += (_, _) =>
        {
            var lines = (_list!.SelectedItems.Count > 0 ? _list.SelectedItems.Cast<Row>() : _rows).Select(r => _files.Count == 1 ? r.Hash : $"{r.Hash} *{r.Name}");
            try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); } catch (System.Runtime.InteropServices.COMException) { }
        };
        foreach (var b in new[] { verifyFile, save, copy }) { DockPanel.SetDock(b, Dock.Right); cmp.Children.Add(b); }
        cmp.Children.Add(_expected);
        bottom.Children.Add(Dialogs.Field("Compare with an expected checksum (paste it here)", cmp));
        bottom.Children.Add(_verdict);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        _list = List(Column("File", nameof(Row.Name), 200), Column("Algorithm", nameof(Row.Algorithm), 110, brush: "Fg2"),
            Column("Checksum", nameof(Row.Hash), 470), Column("", nameof(Row.Result), 140, brushProperty: nameof(Row.ResultBrush)));
        foreach (var col in ((GridView)_list.View).Columns)
            if ((string)col.Header == "Checksum") ((FrameworkElementFactory)col.CellTemplate.VisualTree).SetValue(TextBlock.FontFamilyProperty, new FontFamily("Cascadia Mono, Consolas"));
        root.Children.Add(_list);
        Content = root;

        _expected.TextChanged += (_, _) => Compare();
        Closed += (_, _) => _cts?.Cancel();
        Loaded += async (_, _) => await ComputeAsync();
    }

    internal IReadOnlyList<Row> Rows => _rows;
    internal string Verdict => _verdict.Text;
    internal void SetExpected(string text) => _expected.Text = text;

    internal async Task ComputeAsync()
    {
        if (_cts != null) { _cts.Cancel(); return; }
        var kinds = _algos.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
        if (kinds.Count == 0) { _verdict.Text = "Choose at least one algorithm."; return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _compute.Content = "Cancel";
        _bar.Visibility = Visibility.Visible;
        long total = Math.Max(1, _files.Sum(f => new FileInfo(f).Length)), done = 0;
        var progress = new Progress<double>(v => _bar.Value = v);
        try
        {
            var files = _files.ToList();
            var rows = await Task.Run(() =>
            {
                var list = new List<Row>();
                foreach (var f in files)
                {
                    var h = FileHasher.Compute(f, kinds, n => { done += n; ((IProgress<double>)progress).Report((double)done / total); }, ct);
                    foreach (var k in kinds) list.Add(new Row { File = f, Algorithm = FileHasher.Name(k), Hash = h[k] });
                }
                return list;
            }, ct);
            _rows = rows;
            _list.ItemsSource = _rows;
            Compare();
        }
        catch (OperationCanceledException) { _verdict.Text = "Stopped."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _verdict.Text = "Couldn't read a file: " + ex.Message; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _compute.Content = "Compute";
            _bar.Visibility = Visibility.Collapsed;
        }
    }

    private void Compare()
    {
        foreach (var r in _rows) { r.Result = ""; r.ResultBrush = Theme.Brush("Fg2"); }
        var text = _expected.Text.Trim();
        if (text.Length == 0) { _verdict.Text = ""; Refresh(); return; }
        var expected = FileHasher.NormalizeExpected(text);
        if (expected == null) { _verdict.Text = "That doesn't look like an MD5, SHA-1, SHA-256 or SHA-512 checksum."; _verdict.Foreground = Theme.Brush("Danger"); Refresh(); return; }
        var kind = FileHasher.KindForLength(expected.Length)!.Value;
        var match = _rows.FirstOrDefault(r => FileHasher.Matches(r.Hash, expected));
        if (match != null)
        {
            match.Result = "✓ matches";
            match.ResultBrush = Brushes.LightGreen;
            _verdict.Text = $"✓ Match: {match.Name} has this {FileHasher.Name(kind)} checksum.";
            _verdict.Foreground = Brushes.LightGreen;
        }
        else if (!_rows.Any(r => r.Algorithm == FileHasher.Name(kind)))
        {
            _verdict.Text = $"This is a {FileHasher.Name(kind)} checksum. Tick {FileHasher.Name(kind)} and compute again.";
            _verdict.Foreground = Theme.Brush("Star");
        }
        else
        {
            _verdict.Text = $"✗ No match. The file differs from the one the checksum was made for (damaged, changed, or a different file).";
            _verdict.Foreground = Theme.Brush("Danger");
        }
        Refresh();
    }

    private void Refresh() => _list.Items.Refresh();

    private async Task VerifyFileAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a checksum file", Filter = "Checksum files|*.sha256;*.sha512;*.sha1;*.md5;*.txt;*SUMS*|All files|*.*",
            InitialDirectory = _files.Count > 0 ? Path.GetDirectoryName(_files[0]) : null,
        };
        if (dlg.ShowDialog(this) != true) return;
        var dir = Path.GetDirectoryName(dlg.FileName)!;
        var entries = FileHasher.ParseChecksumFile(await File.ReadAllTextAsync(dlg.FileName));
        if (entries.Count == 0) { _verdict.Text = "No checksums found in that file."; return; }
        _bar.Visibility = Visibility.Visible;
        _bar.IsIndeterminate = true;
        var rows = await Task.Run(() => entries.Select(e =>
        {
            var path = Path.Combine(dir, e.file.Replace('/', '\\'));
            var kind = FileHasher.KindForLength(e.hash.Length)!.Value;
            if (!File.Exists(path)) return new Row { File = path, Algorithm = FileHasher.Name(kind), Hash = e.hash, Result = "missing", ResultBrush = Theme.Brush("Star") };
            var actual = FileHasher.Compute(path, new[] { kind })[kind];
            bool ok = FileHasher.Matches(actual, e.hash);
            return new Row { File = path, Algorithm = FileHasher.Name(kind), Hash = actual, Result = ok ? "✓ OK" : "✗ MISMATCH", ResultBrush = ok ? Brushes.LightGreen : Theme.Brush("Danger") };
        }).ToList());
        _bar.Visibility = Visibility.Collapsed;
        _bar.IsIndeterminate = false;
        _rows = rows;
        _list.ItemsSource = _rows;
        int bad = rows.Count(r => r.Result != "✓ OK");
        _verdict.Text = bad == 0 ? $"✓ All {rows.Count} files match." : $"{bad} of {rows.Count} files are missing or don't match.";
        _verdict.Foreground = bad == 0 ? Brushes.LightGreen : Theme.Brush("Danger");
    }

    private void SaveChecksumFile()
    {
        var sha = _rows.Where(r => r.Algorithm == FileHasher.Name(HashAlgorithmKind.Sha256)).ToList();
        if (sha.Count == 0) { _verdict.Text = "Compute SHA-256 first."; return; }
        var dir = Path.GetDirectoryName(sha[0].File)!;
        var name = sha.Count == 1 ? sha[0].Name + ".sha256" : "SHA256SUMS";
        var target = FileOps.UniqueName(dir, name);
        File.WriteAllLines(target, sha.Select(r => $"{r.Hash} *{Path.GetRelativePath(dir, r.File).Replace('\\', '/')}"));
        _verdict.Text = $"Saved {Path.GetFileName(target)}.";
        _verdict.Foreground = Theme.Brush("Fg2");
    }
}

/// <summary>Add or remove Fyltrix tags on the selected items.</summary>
public sealed class TagsDialog : DialogWindow
{
    public TagsDialog(Window owner, IReadOnlyList<string> paths) : base(owner, "Tags", 480)
    {
        var store = TagStore.Shared;
        var common = paths.Select(p => store.Get(p)?.Tags ?? new List<string>())
            .Aggregate((IEnumerable<string>?)null, (acc, t) => acc == null ? t : acc.Intersect(t, StringComparer.OrdinalIgnoreCase))?.ToList() ?? new List<string>();
        AddContent(Dialogs.Heading(paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} items"));
        var box = new TextBox { Text = string.Join(", ", common) };
        AddContent(Dialogs.Field("Tags, separated by commas", box));
        var known = store.AllTags();
        if (known.Count > 0)
        {
            AddContent(Dialogs.Label("Click to add an existing tag"));
            var chips = new WrapPanel();
            foreach (var t in known)
            {
                var b = Dialogs.Button(t);
                b.MinWidth = 0;
                b.Margin = new Thickness(0, 0, 6, 6);
                b.Padding = new Thickness(8, 3, 8, 3);
                b.Click += (_, _) =>
                {
                    var list = Split(box.Text);
                    if (!list.Contains(t, StringComparer.OrdinalIgnoreCase)) box.Text = string.Join(", ", list.Append(t));
                };
                chips.Children.Add(b);
            }
            AddContent(chips);
        }
        AddContent(Dialogs.Note($"Tags are stored by {ProductBranding.Name} (not inside the files) and follow moves and renames made in {ProductBranding.Name}.", "Fg3"));
        AddButton("Save", () =>
        {
            var now = Split(box.Text);
            store.SetTags(paths, now, common.Where(c => !now.Contains(c, StringComparer.OrdinalIgnoreCase)));
            DialogResult = true;
        }, primary: true);
        AddButton("Cancel", () => DialogResult = false, cancel: true);
        Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
    }

    private static List<string> Split(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
