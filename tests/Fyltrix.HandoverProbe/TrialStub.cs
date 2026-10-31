using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Fyltrix.Launcher;

internal static class TrialStub
{
    internal static async Task<int> Run()
    {
        string nonce = Environment.GetEnvironmentVariable("FYLTRIX_UPDATE_TRIAL")!;
        string mode = Environment.GetEnvironmentVariable("FYLTRIX_TEST_HEALTH_MODE")!;
        if (mode == "crash") return 9;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", "Fyltrix.Update." + nonce, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(deadline.Token);
        await HealthWire.WriteAsync(pipe, new Dictionary<string, string> { ["token"] = nonce, ["version"] = "0.2.0" }, deadline.Token);
        var bootstrap = await HealthWire.ReadAsync(pipe, deadline.Token);
        if (mode == "mutate-trial") File.WriteAllText(Path.Combine(bootstrap["profile"], "settings.json"), "{\"newer-schema\":99}");
        if (mode == "timeout") { await Task.Delay(30000, deadline.Token); return 9; }
        await HealthWire.WriteAsync(pipe, new Dictionary<string, string> { ["token"] = mode == "wrong-token" ? new string('0', 32) : nonce,
            ["version"] = "0.2.0", ["ready"] = "1", ["contract"] = "1" }, deadline.Token);
        await HealthWire.ReadAsync(pipe, deadline.Token);
        return 0;
    }
}
