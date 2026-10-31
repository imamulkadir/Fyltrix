using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Fyltrix.Updates;

namespace Fyltrix.Updates.Tests;

public sealed class ToolTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Fyltrix-UpdateToolTests-" + Guid.NewGuid().ToString("N"));
    public ToolTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);
    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        start.ArgumentList.Add(Assembly.Load("Fyltrix.UpdateTool").Location);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output + await error);
    }
    [Fact]
    public async Task Signing_tool_and_offline_verifier_interoperate_and_preserve_inputs()
    {
        using var fixture = new Fixture();
        string key = Path.Combine(root, "private test.pem"), pub = Path.Combine(root, "public test.txt"),
            setup = Path.Combine(root, fixture.Name), signed = Path.Combine(root, "signed output & test"), feed = Path.Combine(root, "release.json");
        await File.WriteAllTextAsync(key, fixture.Key.ExportPkcs8PrivateKeyPem());
        await File.WriteAllTextAsync(pub, fixture.PublicKey);
        await File.WriteAllBytesAsync(setup, fixture.Package);
        var sign = await Run("sign", Fixture.Version, key, pub, signed, setup);
        Assert.True(sign.ExitCode == 0, sign.Output);
        string manifestPath = Path.Combine(signed, ReleasePolicy.ManifestName), sigPath = Path.Combine(signed, ReleasePolicy.SignatureName);
        byte[] data = await File.ReadAllBytesAsync(manifestPath), sig = await File.ReadAllBytesAsync(sigPath);
        await File.WriteAllBytesAsync(feed, Fixture.Bytes(fixture.Metadata(data, sig)));
        var check = await Run("check", "0.1.0", feed);
        Assert.True(check.ExitCode == 0, check.Output);
        Assert.Equal("Available", JsonNode.Parse(check.Output)!["status"]!.GetValue<string>());
        var verify = await Run("verify", feed, manifestPath, sigPath, setup, pub);
        Assert.True(verify.ExitCode == 0, verify.Output);
        Assert.Equal("None", JsonNode.Parse(verify.Output)!["verification"]!.GetValue<string>());
        Assert.False(JsonNode.Parse(verify.Output)!["installed"]!.GetValue<bool>());
        Assert.Equal(fixture.Package, await File.ReadAllBytesAsync(setup));
        var staged = await Run("stage-local", "0.1.0", feed, manifestPath, sigPath, setup, pub, Path.Combine(root, "offline staging"));
        Assert.True(staged.ExitCode == 0, staged.Output);
        var stageFolder = JsonNode.Parse(staged.Output)!["directory"]!.GetValue<string>();
        Assert.Equal(fixture.Package, await File.ReadAllBytesAsync(Path.Combine(stageFolder, fixture.Name)));
        Assert.False(JsonNode.Parse(staged.Output)!["installed"]!.GetValue<bool>());
        var repeat = await Run("sign", Fixture.Version, key, pub, signed, setup);
        Assert.NotEqual(0, repeat.ExitCode);
        Assert.Equal(data, await File.ReadAllBytesAsync(manifestPath));
        byte[] corrupt = fixture.Package.ToArray(); corrupt[^1] ^= 1;
        await File.WriteAllBytesAsync(setup, corrupt);
        Assert.NotEqual(0, (await Run("verify", feed, manifestPath, sigPath, setup, pub)).ExitCode);
        using var wrong = new Fixture(); await File.WriteAllTextAsync(pub, wrong.PublicKey);
        Assert.NotEqual(0, (await Run("sign", Fixture.Version, key, pub, Path.Combine(root, "wrong-key-output"), setup)).ExitCode);
        Assert.False(Directory.Exists(Path.Combine(root, "wrong-key-output")));
    }
}
