using System.Text;
using Fyltrix.Launcher;

namespace Fyltrix.Packaging.Tests;

public class ReleaseLayoutTests
{
    [Theory]
    [InlineData("0.1.0")]
    [InlineData("1.2.3-rc.1")]
    [InlineData("10.0.0-alpha-beta")]
    public void Supported_versions_are_path_safe(string value) => Assert.True(ReleaseLayout.IsVersion(value));

    [Theory]
    [InlineData("../1.0.0")]
    [InlineData("C:\\outside")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0+metadata")]
    [InlineData("1.0.0 ")]
    [InlineData("1.0.0\n")]
    [InlineData("1.0.0;evil")]
    [InlineData("1.0.0:stream")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-a..b")]
    public void Unsafe_or_unsupported_versions_are_rejected(string value) => Assert.False(ReleaseLayout.IsVersion(value));

    [Theory]
    [InlineData("{\"current\":\"1.0.0\",\"current\":\"2.0.0\"}")]
    [InlineData("{\"current\":\"1.0.0\",\"previous\":\"../outside\"}")]
    [InlineData("{\"current\":\"1.0.0\",\"previous\":5}")]
    [InlineData("{\"current\":\"1.0.0\",\"executable\":\"outside.exe\"}")]
    [InlineData("{\"current\":null}")]
    [InlineData("{\"Current\":\"1.0.0\"}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void Corrupt_ambiguous_or_redirecting_pointers_are_rejected(string value) =>
        Assert.Null(ReleaseLayout.ParsePointer(Encoding.UTF8.GetBytes(value)));

    [Fact]
    public void Oversized_pointer_is_rejected_before_parsing() =>
        Assert.Null(ReleaseLayout.ParsePointer(new byte[ReleaseLayout.MaxPointerBytes + 1]));

    [Fact]
    public void Current_payload_is_preferred_and_previous_is_only_a_missing_file_fallback()
    {
        using var tree = new LayoutTree();
        tree.Payload("1.0.0"); tree.Payload("0.9.0"); tree.Pointer("1.0.0", "0.9.0");
        Assert.Equal("1.0.0", ReleaseLayout.Resolve(tree.Root)!.Version);
        File.Delete(Path.Combine(tree.Root, "app-1.0.0", "Fyltrix.exe"));
        var previous = ReleaseLayout.Resolve(tree.Root)!;
        Assert.Equal("0.9.0", previous.Version); Assert.True(previous.UsedPrevious);
    }

    [Fact]
    public void Missing_or_invalid_pointer_never_launches_a_sibling_or_the_launcher_itself()
    {
        using var tree = new LayoutTree();
        File.WriteAllText(Path.Combine(tree.Root, "Fyltrix.exe"), "root launcher");
        tree.Payload("1.0.0");
        Assert.Null(ReleaseLayout.Resolve(tree.Root));
        File.WriteAllText(Path.Combine(tree.Root, "current.json"), "broken");
        Assert.Null(ReleaseLayout.Resolve(tree.Root));
        File.WriteAllText(Path.Combine(tree.Root, "current.json"), new string(' ', ReleaseLayout.MaxPointerBytes + 1));
        Assert.Null(ReleaseLayout.Resolve(tree.Root));
    }

    [Fact]
    public void Argument_boundaries_unicode_and_caller_working_directory_are_preserved()
    {
        var args = new[] { "C:\\Projects with spaces\\café & notes", "quoted\"name", "$()", "", "trailing\\" };
        var start = ReleaseLayout.StartInfo(new Payload("C:\\app\\app-1.0.0\\Fyltrix.exe", "1.0.0", false), args, "C:\\caller");
        Assert.Equal(args, start.ArgumentList);
        Assert.Equal("C:\\caller", start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public void Junction_payloads_are_rejected_without_following_the_redirect()
    {
        using var tree = new LayoutTree();
        tree.Payload("0.9.0"); tree.Pointer("1.0.0", "0.9.0");
        var target = Path.Combine(tree.Root, "redirect-target"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Fyltrix.exe"), "redirected executable");
        var link = Path.Combine(tree.Root, "app-1.0.0");
        var script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + link.Replace("'", "''")
            + "' -Target '" + target.Replace("'", "''") + "' | Out-Null";
        var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
        try
        {
            using var process = System.Diagnostics.Process.Start(start)!;
            if (!process.WaitForExit(20000)) { process.Kill(); throw new TimeoutException("Test junction creation timed out."); }
            Assert.Equal(0, process.ExitCode);
            var resolved = ReleaseLayout.Resolve(tree.Root)!;
            Assert.True(resolved.UsedPrevious); Assert.Equal("0.9.0", resolved.Version);
            Assert.Equal("redirected executable", File.ReadAllText(Path.Combine(target, "Fyltrix.exe")));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link, recursive: false); }
    }

    private sealed class LayoutTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "FyltrixPackagingTests", Guid.NewGuid().ToString("N"));
        public LayoutTree() => Directory.CreateDirectory(Root);
        public void Payload(string version)
        {
            var folder = Path.Combine(Root, "app-" + version); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Fyltrix.exe"), "fixture");
        }
        public void Pointer(string current, string? previous) => File.WriteAllText(Path.Combine(Root, "current.json"), $"{{\"current\":\"{current}\",\"previous\":{(previous == null ? "null" : "\"" + previous + "\"")}}}");
        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "FyltrixPackagingTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unexpected test cleanup path.");
            Directory.Delete(Root, recursive: true);
        }
    }
}
