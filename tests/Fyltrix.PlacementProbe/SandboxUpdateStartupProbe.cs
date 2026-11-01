using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Fyltrix.Updates;

internal static class SandboxUpdateStartupProbe
{
    internal static int Run(string testRoot)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length is not (4 or 5) || args[1] is not (UpdateWorker.ApplyArgument or UpdateWorker.RecoverArgument) || args.Length == 5 && args[4] != "--notify") return 2;
        string keyPath = Path.Combine(testRoot, "public-test.txt");
        var options = new ApplyOptions
        {
            Sandbox = true, Profile = Path.Combine(testRoot, "profile"), HealthTimeout = TimeSpan.FromSeconds(15),
            Restart = root =>
            {
                File.WriteAllText(Path.Combine(testRoot, "restart-request.txt"), root);
                if (Environment.GetEnvironmentVariable("FYLTRIX_MANUAL_UPDATE_CASE") != "commit") return;
                var start = new ProcessStartInfo(Path.Combine(root, "Fyltrix.exe")) { UseShellExecute = false, WorkingDirectory = root };
                // Keep the Sandbox-only profile hook, but do not re-enter the worker or update controls.
                foreach (var key in new[] { "FYLTRIX_SANDBOX_UPDATE_WORKER", "FYLTRIX_TEST_PRODUCTION_TRUST", "FYLTRIX_SANDBOX_FAULT", "FYLTRIX_MANUAL_UPDATE_DATA", "FYLTRIX_MANUAL_UPDATE_CASE" }) start.Environment.Remove(key);
                using var restarted = Process.Start(start) ?? throw new IOException("Sandbox restart failed.");
                File.WriteAllText(Path.Combine(testRoot, "restart-pid.json"), JsonSerializer.Serialize(new { pid = restarted.Id }));
            },
            Checkpoint = point =>
            {
                string? mode = Environment.GetEnvironmentVariable("FYLTRIX_SANDBOX_FAULT");
                if (mode == "interrupt-switch" && point == ApplyPoint.PointerSwitched) Environment.Exit(97);
                if (mode == "interrupt-commit" && point == ApplyPoint.Committed) Environment.Exit(98);
                if (mode == "invalid-trial-settings" && point == ApplyPoint.PayloadExtracted)
                {
                    string stage = Path.GetDirectoryName(args[2])!;
                    File.WriteAllText(Path.Combine(stage, "transaction", "trial-profile", "settings.json"), "{\"UnsupportedSchema\":99}");
                }
            }
        };
        return UpdateWorker.RunCoreAsync(args[2], args[3], args[1] == UpdateWorker.RecoverArgument,
            Environment.GetEnvironmentVariable("FYLTRIX_TEST_PRODUCTION_TRUST") == "1" ? null : [File.ReadAllText(keyPath).Trim()], options, CancellationToken.None).GetAwaiter().GetResult();
    }
}
