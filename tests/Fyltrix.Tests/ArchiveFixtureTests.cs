using System.Diagnostics;
using Fyltrix.Core.Archives;

namespace Fyltrix.Tests;

/// <summary>Fixtures produced with Windows' own tar.exe (libarchive): 7z and encrypted ZIP, which .NET can't write.</summary>
public class ArchiveFixtureTests
{
    private static void Tar(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe")) { WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
    }

    [Theory]
    [InlineData("aes256")]
    [InlineData("zipcrypt")]
    public void Encrypted_zip_requires_correct_password(string method)
    {
        using var t = new TempDir();
        t.File("in/secret.txt", "top secret");
        Tar(t["in"], "--format", "zip", "--options", "zip:encryption=" + method, "--passphrase", "pa55", "-cf", t["enc.zip"], "secret.txt");

        var list = ArchiveService.List(t["enc.zip"]);
        Assert.True(ArchiveService.NeedsPassword(list));

        var none = Assert.Throws<ArchivePasswordException>(() => ArchiveService.Extract(t["enc.zip"], t.Dir("a"), new ExtractOptions()));
        Assert.False(none.WasWrong);
        var wrong = Assert.Throws<ArchivePasswordException>(() => ArchiveService.Extract(t["enc.zip"], t.Dir("b"), new ExtractOptions { Password = "nope" }));
        Assert.True(wrong.WasWrong);
        Assert.Empty(Directory.GetFileSystemEntries(t["b"]));

        var ok = ArchiveService.Extract(t["enc.zip"], t.Dir("c"), new ExtractOptions { Password = "pa55" });
        Assert.Empty(ok.Errors);
        Assert.Equal("top secret", File.ReadAllText(t["c/secret.txt"]));
        Assert.Equal(new[] { t["c/secret.txt"] }, ok.CreatedRoots);
        Assert.Single(Directory.GetFileSystemEntries(t["c"]));

        ArchiveService.ExtractEntryToFile(t["enc.zip"], "secret.txt", t["one/s.txt"], "pa55", new ArchiveLimits());
        Assert.Equal("top secret", File.ReadAllText(t["one/s.txt"]));
    }

    [Fact]
    public void SevenZip_list_and_extract()
    {
        using var t = new TempDir();
        t.File("in/dir/a.txt", "alpha");
        t.File("in/b.txt", "bravo");
        Tar(t["in"], "--format", "7zip", "-cf", t["x.7z"], "dir", "b.txt");

        var list = ArchiveService.List(t["x.7z"]);
        Assert.Contains(list, e => e.Path == "dir/a.txt");
        var r = ArchiveService.Extract(t["x.7z"], t.Dir("out"), new ExtractOptions());
        Assert.Empty(r.Errors);
        Assert.Equal("alpha", File.ReadAllText(t["out/dir/a.txt"]));
        Assert.Equal("bravo", File.ReadAllText(t["out/b.txt"]));

        ArchiveService.ExtractEntryToFile(t["x.7z"], "b.txt", t["one/b.txt"], null, new ArchiveLimits());
        Assert.Equal("bravo", File.ReadAllText(t["one/b.txt"]));
    }

    [Fact]
    public void Tar_xz_from_libarchive_is_readable()
    {
        using var t = new TempDir();
        t.File("in/x/y.txt", "why");
        Tar(t["in"], "-cJf", t["p.tar.xz"], "x");
        var r = ArchiveService.Extract(t["p.tar.xz"], t.Dir("o"), new ExtractOptions());
        Assert.Empty(r.Errors);
        Assert.Equal("why", File.ReadAllText(t["o/x/y.txt"]));
    }

    [Fact]
    public void SystemTar_creates_7z_and_encrypted_zip()
    {
        Assert.True(SystemTar.IsAvailable);
        using var t = new TempDir();
        t.File("src/d/file.txt", "content");
        t.File("src/top.txt", "top");
        SystemTar.Create(new[] { t["src/d"], t["src/top.txt"] }, t["out.7z"], SystemTarFormat.SevenZip, null, CancellationToken.None);
        var r = ArchiveService.Extract(t["out.7z"], t.Dir("x"), new ExtractOptions());
        Assert.Empty(r.Errors);
        Assert.Equal("content", File.ReadAllText(t["x/d/file.txt"]));

        SystemTar.Create(new[] { t["src/top.txt"] }, t["enc.zip"], SystemTarFormat.ZipAes256, "s3cret", CancellationToken.None);
        Assert.Throws<ArchivePasswordException>(() => ArchiveService.Extract(t["enc.zip"], t.Dir("y"), new ExtractOptions()));
        var ok = ArchiveService.Extract(t["enc.zip"], t.Dir("z"), new ExtractOptions { Password = "s3cret" });
        Assert.Equal("top", File.ReadAllText(t["z/top.txt"]));
    }
}
