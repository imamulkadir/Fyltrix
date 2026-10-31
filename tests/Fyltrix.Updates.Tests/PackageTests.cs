using System.IO.Compression;
using System.Text;
using Fyltrix.Updates;

namespace Fyltrix.Updates.Tests;

public sealed class PackageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Fyltrix-UpdateTests-" + Guid.NewGuid().ToString("N"));
    public PackageTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true); // Unique test-created directory, never supplied by a caller.
    [Fact]
    public async Task Signed_setup_checks_exact_bytes_without_modifying_or_running_them()
    {
        using var fixture = new Fixture();
        string path = Path.Combine(root, fixture.Name);
        await File.WriteAllBytesAsync(path, fixture.Package);
        var verified = fixture.Verified();
        Assert.Equal(VerificationFailure.None, await PackageVerifier.VerifyAsync(verified, fixture.Name, path));
        Assert.Equal(fixture.Package, await File.ReadAllBytesAsync(path));
        byte[] changed = fixture.Package.ToArray(); changed[^1] ^= 1;
        await File.WriteAllBytesAsync(path, changed);
        Assert.Equal(VerificationFailure.PackageMismatch, await PackageVerifier.VerifyAsync(verified, fixture.Name, path));
        await File.WriteAllBytesAsync(path, [1]);
        Assert.Equal(VerificationFailure.PackageMismatch, await PackageVerifier.VerifyAsync(verified, fixture.Name, path));
        Assert.Equal(VerificationFailure.AssetMismatch, await PackageVerifier.VerifyAsync(verified, "Fyltrix-Sandbox-Setup-win-x64.exe", path));
    }
    [Fact]
    public async Task A_signed_non_executable_setup_is_rejected()
    {
        using var fixture = new Fixture { Package = Encoding.UTF8.GetBytes("this is not an executable") };
        string path = Path.Combine(root, fixture.Name); await File.WriteAllBytesAsync(path, fixture.Package);
        Assert.Equal(VerificationFailure.InvalidPackage, await PackageVerifier.VerifyAsync(fixture.Verified(), fixture.Name, path));
    }
    [Fact]
    public async Task A_signed_corrupt_zip_is_a_failure_result_instead_of_an_exception()
    {
        using var fixture = new Fixture { Name = ReleasePolicy.ZipName(Fixture.Version), Package = [0x50, 0x4b, 1, 2, 3] };
        string path = Path.Combine(root, fixture.Name); await File.WriteAllBytesAsync(path, fixture.Package);
        Assert.Equal(VerificationFailure.InvalidPackage, await PackageVerifier.VerifyAsync(fixture.Verified(), fixture.Name, path));
    }
    [Fact]
    public async Task Package_hashing_honors_cancellation()
    {
        using var fixture = new Fixture();
        string path = Path.Combine(root, fixture.Name); await File.WriteAllBytesAsync(path, fixture.Package);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PackageVerifier.VerifyAsync(fixture.Verified(), fixture.Name, path, cancel.Token));
    }
    [Theory]
    [InlineData("valid", true)] [InlineData("windows-separators", true)]
    [InlineData("traversal", false)] [InlineData("ads", false)] [InlineData("reserved", false)]
    [InlineData("rooted", false)] [InlineData("duplicate", false)] [InlineData("file-directory-collision", false)]
    [InlineData("trailing-dot", false)] [InlineData("symlink", false)] [InlineData("wrong-version", false)]
    [InlineData("previous-pointer", false)] [InlineData("extra-payload", false)] [InlineData("profile", false)]
    [InlineData("oversized-pointer", false)] [InlineData("missing-launcher", false)]
    public async Task Zip_layout_is_checked_without_extraction(string variant, bool valid)
    {
        using var fixture = new Fixture { Name = ReleasePolicy.ZipName(Fixture.Version) };
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            void Add(string name, string body = "payload")
            {
                if (variant == "windows-separators") name = name.Replace('/', '\\');
                var entry = zip.CreateEntry(name);
                if (variant == "symlink" && name.EndsWith("Fyltrix.exe")) entry.ExternalAttributes = unchecked((int)0xa0000000);
                using var writer = new StreamWriter(entry.Open()); writer.Write(body);
            }
            if (variant != "missing-launcher") Add("Fyltrix.exe");
            Add("app-0.2.0/Fyltrix.exe");
            Add("current.json", variant switch
            {
                "wrong-version" => "{\"current\":\"0.1.0\",\"previous\":null}",
                "previous-pointer" => "{\"current\":\"0.2.0\",\"previous\":\"0.1.0\"}",
                "oversized-pointer" => new string(' ', 4097),
                _ => "{\"current\":\"0.2.0\",\"previous\":null}"
            });
            string? bad = variant switch
            {
                "traversal" => "app-0.2.0/../run.exe", "ads" => "app-0.2.0/file:stream", "reserved" => "app-0.2.0/CON.txt",
                "rooted" => "/app-0.2.0/evil", "duplicate" => "fyltrix.exe", "file-directory-collision" => "app-0.2.0/Fyltrix.exe/evil",
                "trailing-dot" => "app-0.2.0/file.", "extra-payload" => "app-0.1.0/Fyltrix.exe", "profile" => "profile/settings.json", _ => null
            };
            if (bad != null) Add(bad);
        }
        fixture.Package = buffer.ToArray();
        string path = Path.Combine(root, fixture.Name); await File.WriteAllBytesAsync(path, fixture.Package);
        Assert.Equal(valid ? VerificationFailure.None : VerificationFailure.InvalidPackage,
            await PackageVerifier.VerifyAsync(fixture.Verified(), fixture.Name, path));
        Assert.Single(Directory.GetFiles(root));
    }
}
