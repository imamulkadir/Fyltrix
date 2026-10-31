using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using Fyltrix.Core;
using Fyltrix.Core.Archives;

namespace Fyltrix.UI;

public enum ConflictPolicy { Ask, KeepBoth, Skip, Replace }

public sealed record ExtractRequest(string Destination, string? Password, ConflictPolicy Conflicts, bool PreserveTimestamps, bool OpenWhenDone);

/// <summary>"Extract to…": destination, password, what to do with existing files.</summary>
public sealed class ExtractDialog : DialogWindow
{
    public ExtractRequest? Result { get; private set; }

    public ExtractDialog(Window owner, string archiveName, string suggestedDestination, bool encrypted, string? knownPassword, int selectedCount)
        : base(owner, "Extract", 540)
    {
        AddContent(Dialogs.Heading(selectedCount > 0 ? $"Extract {selectedCount} selected item{(selectedCount == 1 ? "" : "s")} from '{archiveName}'" : $"Extract '{archiveName}'"));
        var dest = new TextBox { Text = suggestedDestination };
        AddContent(Dialogs.Field("Destination folder (created if needed)", Dialogs.FolderPicker(dest, this, "Choose where to extract")));

        var pw = new PasswordBox { Password = knownPassword ?? "" };
        var pwField = Dialogs.Field("Password", pw);
        pwField.Visibility = encrypted ? Visibility.Visible : Visibility.Collapsed;
        AddContent(pwField);

        var conflicts = Dialogs.Combo(new[]
        {
            ("Ask me for each file", ConflictPolicy.Ask),
            ("Keep both (rename the extracted file)", ConflictPolicy.KeepBoth),
            ("Skip existing files", ConflictPolicy.Skip),
            ("Replace (old files go to the Recycle Bin)", ConflictPolicy.Replace),
        }, ConflictPolicy.Ask);
        AddContent(Dialogs.Field("If a file already exists", conflicts));

        var times = new CheckBox { Content = "Keep the original modified dates", IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
        var open = new CheckBox { Content = "Open the destination folder when done", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
        AddContent(times);
        AddContent(open);
        var err = Dialogs.Note("", "Danger");
        AddContent(err);

        AddButton("Extract", () =>
        {
            var path = dest.Text.Trim().Trim('"');
            if (path.Length == 0 || !Path.IsPathFullyQualified(path)) { err.Text = "Enter a full folder path, e.g. C:\\Users\\me\\Downloads\\out"; return; }
            if (File.Exists(path)) { err.Text = "That path is a file, not a folder."; return; }
            Result = new ExtractRequest(path, encrypted && pw.Password.Length > 0 ? pw.Password : knownPassword, Dialogs.Selected<ConflictPolicy>(conflicts),
                times.IsChecked == true, open.IsChecked == true);
            DialogResult = true;
        }, primary: true);
        AddButton("Cancel", () => DialogResult = false, cancel: true);
        Loaded += (_, _) => { dest.Focus(); dest.SelectAll(); };
    }
}

public sealed record CompressRequest(string ArchivePath, ArchiveCreateFormat? Managed, SystemTarFormat? System, CompressionLevel Level, string? Password);

/// <summary>"Compress…": archive name, folder, format, compression level and (for ZIP, where Windows supports it) a password.</summary>
public sealed class CompressDialog : DialogWindow
{
    public CompressRequest? Result { get; private set; }

    private sealed record FormatChoice(string Label, string Extension, ArchiveCreateFormat? Managed, SystemTarFormat? System, bool Level);

    public CompressDialog(Window owner, string suggestedName, string folder, bool singleFile)
        : base(owner, "Compress", 560)
    {
        var formats = new List<FormatChoice>
        {
            new("ZIP (.zip) — opens everywhere", ".zip", ArchiveCreateFormat.Zip, null, true),
        };
        if (SystemTar.CanWrite7z) formats.Add(new("7-Zip (.7z) — smaller, via Windows tar", ".7z", null, SystemTarFormat.SevenZip, false));
        formats.Add(new("TAR + gzip (.tar.gz)", ".tar.gz", ArchiveCreateFormat.TarGz, null, true));
        formats.Add(new("TAR + bzip2 (.tar.bz2)", ".tar.bz2", ArchiveCreateFormat.TarBz2, null, false));
        formats.Add(new("TAR, uncompressed (.tar)", ".tar", ArchiveCreateFormat.Tar, null, false));
        if (singleFile) formats.Add(new("gzip, this one file (.gz)", ".gz", ArchiveCreateFormat.GZip, null, true));

        var name = new TextBox { Text = suggestedName };
        var dir = new TextBox { Text = folder };
        var format = new ComboBox();
        foreach (var f in formats) format.Items.Add(new ComboBoxItem { Content = f.Label, Tag = f });
        format.SelectedIndex = 0;
        var level = Dialogs.Combo(new[]
        {
            ("Normal", CompressionLevel.Optimal),
            ("Smallest (slower)", CompressionLevel.SmallestSize),
            ("Fastest (bigger)", CompressionLevel.Fastest),
            ("Store only (no compression)", CompressionLevel.NoCompression),
        }, CompressionLevel.Optimal);
        var levelField = Dialogs.Field("Compression", level);

        var encrypt = new CheckBox { Content = "Protect with a password (AES-256)", Margin = new Thickness(0, 10, 0, 0) };
        var pw1 = new PasswordBox();
        var pw2 = new PasswordBox();
        var pwPanel = new StackPanel { Visibility = Visibility.Collapsed };
        pwPanel.Children.Add(Dialogs.Field("Password", pw1));
        pwPanel.Children.Add(Dialogs.Field("Confirm password", pw2));
        pwPanel.Children.Add(Dialogs.Note($"Created with Windows' built-in tar.exe. 7-Zip, WinRAR and {ProductBranding.Name} open AES-encrypted ZIPs; File Explorer can't. " +
                                          "File names inside the ZIP are not encrypted.", "Fg3"));
        encrypt.Checked += (_, _) => pwPanel.Visibility = Visibility.Visible;
        encrypt.Unchecked += (_, _) => pwPanel.Visibility = Visibility.Collapsed;

        AddContent(Dialogs.Field("Archive name", name));
        AddContent(Dialogs.Field("Save in", Dialogs.FolderPicker(dir, this, "Choose where to save the archive")));
        AddContent(Dialogs.Field("Format", format));
        AddContent(levelField);
        AddContent(encrypt);
        AddContent(pwPanel);
        var err = Dialogs.Note("", "Danger");
        AddContent(err);

        FormatChoice Current() => (FormatChoice)((ComboBoxItem)format.SelectedItem).Tag;
        void Sync()
        {
            var f = Current();
            levelField.Visibility = f.Level ? Visibility.Visible : Visibility.Collapsed;
            bool canEncrypt = f.Managed == ArchiveCreateFormat.Zip && SystemTar.CanWriteEncryptedZip;
            encrypt.Visibility = canEncrypt ? Visibility.Visible : Visibility.Collapsed;
            if (!canEncrypt) encrypt.IsChecked = false;
            // keep the name's extension in step with the format
            var stem = ArchiveFormats.StripArchiveExtension(name.Text.Trim());
            if (stem.Length > 0) name.Text = stem + f.Extension;
        }
        format.SelectionChanged += (_, _) => Sync();
        Sync();

        AddButton("Create", () =>
        {
            var f = Current();
            var n = name.Text.Trim();
            var nameError = FileOps.ValidateName(n);
            if (nameError != null) { err.Text = nameError; return; }
            var d = dir.Text.Trim().Trim('"');
            if (!Directory.Exists(d)) { err.Text = "That folder doesn't exist."; return; }
            var full = Path.Combine(d, n);
            if (FileOps.Exists(full)) { err.Text = $"'{n}' already exists there. Choose another name."; return; }
            string? password = null;
            if (encrypt.IsChecked == true)
            {
                if (pw1.Password.Length < 1) { err.Text = "Enter a password."; return; }
                if (pw1.Password != pw2.Password) { err.Text = "The passwords don't match."; return; }
                password = pw1.Password;
            }
            Result = password != null
                ? new CompressRequest(full, null, SystemTarFormat.ZipAes256, CompressionLevel.Optimal, password)
                : new CompressRequest(full, f.Managed, f.System, Dialogs.Selected<CompressionLevel>(level), null);
            DialogResult = true;
        }, primary: true);
        AddButton("Cancel", () => DialogResult = false, cancel: true);
        Loaded += (_, _) =>
        {
            name.Focus();
            name.Select(0, ArchiveFormats.StripArchiveExtension(name.Text).Length);
        };
    }
}
