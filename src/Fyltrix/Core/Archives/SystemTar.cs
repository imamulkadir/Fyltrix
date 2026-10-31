using System.Diagnostics;

namespace Fyltrix.Core.Archives;

public enum SystemTarFormat { SevenZip, ZipAes256 }

/// <summary>
/// Windows 10/11 ship libarchive's bsdtar as %SystemRoot%\System32\tar.exe. Recent builds can write 7z and
/// AES-encrypted ZIP, which no managed library here can. Capabilities are probed once, never assumed.
/// </summary>
public static class SystemTar
{
    private static readonly string ExePath = Path.Combine(Environment.SystemDirectory, "tar.exe");
    private static readonly Lazy<(bool sevenZip, bool zipAes)> Caps = new(Probe);

    public static bool IsAvailable => File.Exists(ExePath);
    public static bool CanWrite7z => IsAvailable && Caps.Value.sevenZip;
    public static bool CanWriteEncryptedZip => IsAvailable && Caps.Value.zipAes;

    private static (bool, bool) Probe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Fyltrix-tarprobe-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "p.txt"), "probe");
            bool sz = Run(dir, new[] { "--format", "7zip", "-cf", "p.7z", "p.txt" }, CancellationToken.None, out _) && new FileInfo(Path.Combine(dir, "p.7z")).Length > 0;
            bool aes = Run(dir, new[] { "--format", "zip", "--options", "zip:encryption=aes256", "--passphrase", "x", "-cf", "p.zip", "p.txt" }, CancellationToken.None, out _)
                       && new FileInfo(Path.Combine(dir, "p.zip")).Length > 0;
            return (sz, aes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return (false, false);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool Run(string workDir, IEnumerable<string> args, CancellationToken ct, out string error)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException("tar.exe could not be started.");
        var errTask = p.StandardError.ReadToEndAsync();
        _ = p.StandardOutput.ReadToEndAsync();
        while (!p.WaitForExit(100))
        {
            if (ct.IsCancellationRequested)
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                p.WaitForExit();
                ct.ThrowIfCancellationRequested();
            }
        }
        error = errTask.Result.Trim();
        return p.ExitCode == 0;
    }

    /// <summary>
    /// Extracts with libarchive (which already refuses absolute and ".." paths). Callers treat the output as
    /// untrusted staging data and copy it onward with their own checks.
    /// </summary>
    public static void ExtractAll(string archivePath, string destDir, string? password, CancellationToken ct, IEnumerable<string>? members = null)
    {
        var args = new List<string> { "-xf", Path.GetFullPath(archivePath), "-C", destDir };
        if (password != null) { args.Add("--passphrase"); args.Add(password); }
        if (members != null) args.AddRange(members);
        if (!Run(destDir, args, ct, out var err))
        {
            if (err.Contains("passphrase", StringComparison.OrdinalIgnoreCase) || err.Contains("decrypt", StringComparison.OrdinalIgnoreCase))
                throw new ArchivePasswordException(password != null);
            throw new IOException("tar.exe couldn't extract the archive: " + (err.Length > 0 ? err : "unknown error"));
        }
    }

    /// <summary>Creates a 7z or AES-256 ZIP. Items keep their own names at the archive root.</summary>
    public static void Create(IList<string> sources, string archivePath, SystemTarFormat format, string? password, CancellationToken ct)
    {
        if (format == SystemTarFormat.ZipAes256 && string.IsNullOrEmpty(password)) throw new ArgumentException("A password is required.");
        var outFull = Path.GetFullPath(archivePath);
        if (File.Exists(outFull) || Directory.Exists(outFull)) throw new IOException($"'{Path.GetFileName(outFull)}' already exists.");
        var dir = Path.GetDirectoryName(outFull)!;
        var temp = Path.Combine(dir, $".~{Guid.NewGuid():N}.fl-partial");

        var args = new List<string>();
        if (format == SystemTarFormat.SevenZip) args.AddRange(new[] { "--format", "7zip" });
        else args.AddRange(new[] { "--format", "zip", "--options", "zip:encryption=aes256", "--passphrase", password! });
        args.AddRange(new[] { "-cf", temp });
        foreach (var s in sources)
        {
            var full = Path.GetFullPath(s.TrimEnd('\\'));
            var parent = Path.GetDirectoryName(full) ?? throw new NotSupportedException("A whole drive can't be compressed.");
            args.Add("-C");
            args.Add(parent);
            args.Add(Path.GetFileName(full));
        }
        try
        {
            if (!Run(dir, args, ct, out var err)) throw new IOException("tar.exe failed: " + (err.Length > 0 ? err : "unknown error"));
            File.Move(temp, outFull);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
