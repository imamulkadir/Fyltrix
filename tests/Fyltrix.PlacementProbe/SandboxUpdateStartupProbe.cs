using System.IO;
using Fyltrix.Updates;

internal static class SandboxUpdateStartupProbe
{
    internal static int Run(string testRoot)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length != 4 || args[1] is not (UpdateWorker.ApplyArgument or UpdateWorker.RecoverArgument)) return 2;
        string keyPath = Path.Combine(testRoot, "public-test.txt");
        var options = new ApplyOptions
        {
            Sandbox = true, Profile = Path.Combine(testRoot, "profile"), HealthTimeout = TimeSpan.FromSeconds(15),
            Restart = root => File.WriteAllText(Path.Combine(testRoot, "restart-request.txt"), root),
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
            [File.ReadAllText(keyPath).Trim()], options, CancellationToken.None).GetAwaiter().GetResult();
    }
}
