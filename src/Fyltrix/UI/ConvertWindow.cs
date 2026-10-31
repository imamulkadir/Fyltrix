using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fyltrix.Core;
using Fyltrix.Core.Conversion;
using Fyltrix.Core.Imaging;

namespace Fyltrix.UI;

/// <summary>Batch conversion: images with the Windows codecs (+ WebP), audio/video with a user-installed FFmpeg.</summary>
public sealed class ConvertWindow : ToolWindow
{
    public sealed class Row
    {
        public required string Source { get; init; }
        public string Name => Path.GetFileName(Source);
        public string Result { get; set; } = "waiting";
        public Brush ResultBrush { get; set; } = Theme.Brush("Fg3");
    }

    private enum Kind { Image, Audio, Video }

    private readonly AppSettings _settings;
    private readonly List<Row> _rows;
    private readonly Kind _kind;
    private readonly ListView _list;
    private readonly ComboBox _format, _quality, _size;
    private readonly TextBox _folder = new();
    private readonly StackPanel _ffmpegPanel = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox _ffmpegPath = new();
    private readonly TextBlock _status = new() { Foreground = Theme.Brush("Fg2"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressBar _bar = new() { Maximum = 1, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _go;
    private CancellationTokenSource? _cts;

    public event EventHandler<IReadOnlyList<string>>? FilesCreated;

    public ConvertWindow(Window owner, AppSettings settings, IReadOnlyList<string> files) : base(owner, "Convert", 1000, 700)
    {
        _settings = settings;
        _rows = files.Select(f => new Row { Source = f }).ToList();
        _kind = files.All(ImageService.CanDecode) ? Kind.Image : files.All(FileKinds.IsAudio) ? Kind.Audio : Kind.Video;

        var form = new StackPanel { Width = 330, Margin = new Thickness(0, 0, 16, 0) };
        if (_kind == Kind.Image)
        {
            _format = Dialogs.Combo(new[]
            {
                ("PNG (lossless)", (object)ImageFormat.Png), ("JPEG", ImageFormat.Jpeg), ("WebP", ImageFormat.WebP), ("TIFF", ImageFormat.Tiff),
                ("BMP", ImageFormat.Bmp), ("GIF", ImageFormat.Gif), ("ICO (Windows icon)", ImageFormat.Ico),
            }, ImageFormat.Png);
            _quality = Dialogs.Combo(new[] { ("Best (95)", (object)95), ("High (90)", 90), ("Medium (80)", 80), ("Small (65)", 65), ("Lossless WebP", 100) }, 90);
            _size = Dialogs.Combo(new[] { ("Original size", (object)0), ("Longest side 3840 px", 3840), ("Longest side 1920 px", 1920), ("Longest side 1280 px", 1280), ("Longest side 640 px", 640) }, 0);
        }
        else
        {
            var presets = _kind == Kind.Audio ? Ffmpeg.AudioPresets : Ffmpeg.VideoPresets.Concat(Ffmpeg.AudioPresets.Select(p => p with { Label = "Audio only: " + p.Label }));
            _format = Dialogs.Combo(presets.Select(p => (p.Label, (object)p)), presets.First());
            _quality = Dialogs.Combo(new[] { ("High quality", (object)MediaQuality.High), ("Balanced", MediaQuality.Balanced), ("Smaller file", MediaQuality.Small) }, MediaQuality.Balanced);
            _size = Dialogs.Combo(new[] { ("Keep resolution", (object)0), ("Up to 2160p", 2160), ("Up to 1080p", 1080), ("Up to 720p", 720), ("Up to 480p", 480) }, 0);
        }
        form.Children.Add(Dialogs.Field("Convert to", _format));
        form.Children.Add(Dialogs.Field("Quality", _quality));
        var sizeField = Dialogs.Field(_kind == Kind.Image ? "Size" : "Resolution", _size);
        if (_kind == Kind.Audio) sizeField.Visibility = Visibility.Collapsed;
        form.Children.Add(sizeField);
        _folder.Text = Path.GetDirectoryName(files[0]) ?? "";
        form.Children.Add(Dialogs.Field("Save converted files in", Dialogs.FolderPicker(_folder, this, "Choose the output folder")));
        form.Children.Add(Dialogs.Note("Originals are never changed. New files get the new extension; existing names get a number.", "Fg3"));

        _ffmpegPanel.Children.Add(Dialogs.Heading("FFmpeg is needed for audio and video"));
        _ffmpegPanel.Children.Add(Dialogs.Note($"FFmpeg is free and does the actual conversion. {ProductBranding.Name} doesn't download it for you. Install it once, for example with\n" +
                                              $"    winget install Gyan.FFmpeg\nor from ffmpeg.org, then press Check again, or point {ProductBranding.Name} at ffmpeg.exe:"));
        _ffmpegPanel.Children.Add(Dialogs.FilePicker(_ffmpegPath, this, "Choose ffmpeg.exe", "ffmpeg.exe|ffmpeg.exe|Programs|*.exe"));
        var check = Dialogs.Button("Check again");
        check.HorizontalAlignment = HorizontalAlignment.Left;
        check.Margin = new Thickness(0, 8, 0, 0);
        check.Click += (_, _) => { _settings.FfmpegPath = _ffmpegPath.Text; _settings.Save(); UpdateFfmpeg(); };
        _ffmpegPanel.Children.Add(check);
        form.Children.Add(_ffmpegPanel);

        _list = List(Column("File", nameof(Row.Name), 340), Column("Result", nameof(Row.Result), 260, brushProperty: nameof(Row.ResultBrush)));
        _list.ItemsSource = _rows;

        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        _go = Dialogs.Button($"Convert {_rows.Count} file{(_rows.Count == 1 ? "" : "s")}", primary: true);
        _go.Click += async (_, _) => await RunAsync();
        DockPanel.SetDock(_go, Dock.Right);
        bottom.Children.Add(_go);
        bottom.Children.Add(_status);

        var right = new DockPanel();
        DockPanel.SetDock(_bar, Dock.Bottom);
        right.Children.Add(_bar);
        right.Children.Add(_list);
        var root = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        DockPanel.SetDock(form, Dock.Left);
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 350 });
        DockPanel.SetDock(root.Children[^1], Dock.Left);
        root.Children.Add(right);
        Content = root;
        _ffmpegPath.Text = settings.FfmpegPath;
        UpdateFfmpeg();
        Closed += (_, _) => _cts?.Cancel();
    }

    internal IReadOnlyList<Row> Rows => _rows;

    private string? FfmpegExe => _kind == Kind.Image ? null : Ffmpeg.Locate(_settings.FfmpegPath);

    private void UpdateFfmpeg()
    {
        if (_kind == Kind.Image) return;
        var exe = FfmpegExe;
        _ffmpegPanel.Visibility = exe == null ? Visibility.Visible : Visibility.Collapsed;
        _go.IsEnabled = exe != null;
        _status.Text = exe == null ? "FFmpeg wasn't found." : $"Using {exe}";
    }

    internal void SelectFormat(object value)
    {
        foreach (ComboBoxItem i in _format.Items) if (Equals(i.Tag, value)) _format.SelectedItem = i;
    }

    internal async Task RunAsync()
    {
        using var updateWork = UpdateWork.Track();
        if (_cts != null) { _cts.Cancel(); return; }
        var folder = _folder.Text.Trim();
        if (!Directory.Exists(folder)) { _status.Text = "The output folder doesn't exist."; return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _go.Content = "Cancel";
        var created = new List<string>();
        var format = Dialogs.Selected<object>(_format);
        var quality = Dialogs.Selected<object>(_quality);
        int size = Dialogs.Selected<object>(_size) is int s ? s : 0;
        var ffmpeg = FfmpegExe;
        try
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                row.Result = "converting…";
                row.ResultBrush = Theme.Brush("Accent");
                _list.Items.Refresh();
                int index = i;
                var progress = new Progress<double>(v => _bar.Value = (index + v) / _rows.Count);
                try
                {
                    string output;
                    if (format is ImageFormat f)
                    {
                        output = FileOps.UniqueName(folder, Path.GetFileNameWithoutExtension(row.Source) + ImageService.Extension(f));
                        if (string.Equals(output, row.Source, StringComparison.OrdinalIgnoreCase)) output = FileOps.UniqueName(folder, Path.GetFileName(output));
                        int q = (int)quality;
                        await Task.Run(() =>
                        {
                            ct.ThrowIfCancellationRequested();
                            var img = ImageService.Load(row.Source, size > 0 ? size : null);
                            ImageService.Save(img, output, f, q, metadataFrom: row.Source);
                        }, ct);
                    }
                    else
                    {
                        var preset = (MediaPreset)format;
                        output = FileOps.UniqueName(folder, Path.GetFileNameWithoutExtension(row.Source) + preset.Extension);
                        await Ffmpeg.ConvertAsync(ffmpeg!, row.Source, output, preset, (MediaQuality)quality, size > 0 ? size : null, progress, ct);
                    }
                    created.Add(output);
                    row.Result = $"✓ {Path.GetFileName(output)} ({FileOps.FormatSize(new FileInfo(output).Length)})";
                    row.ResultBrush = Brushes.LightGreen;
                }
                catch (OperationCanceledException) { row.Result = "cancelled"; row.ResultBrush = Theme.Brush("Fg3"); throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException
                                               or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException or System.ComponentModel.Win32Exception)
                {
                    row.Result = "✗ " + ex.Message.Split('\n').Last(l => l.Trim().Length > 0);
                    row.ResultBrush = Theme.Brush("Danger");
                }
                _list.Items.Refresh();
                _bar.Value = (i + 1.0) / _rows.Count;
            }
            _status.Text = $"Done: {created.Count} of {_rows.Count} converted.";
        }
        catch (OperationCanceledException) { _status.Text = $"Cancelled after {created.Count} file(s)."; }
        finally
        {
            _list.Items.Refresh();
            _cts.Dispose();
            _cts = null;
            _go.Content = "Convert again";
            if (created.Count > 0) FilesCreated?.Invoke(this, created);
        }
    }
}
