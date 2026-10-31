using System.Diagnostics;
using System.Text.Json;

namespace Fyltrix.Updates;

/// <summary>Runs in the old payload's existing self-contained host. No extra runtime, service, or scheduler.</summary>
public static class UpdateWorker
{
    public const string ApplyArgument = "--fyltrix-update-worker";
    public const string RecoverArgument = "--fyltrix-update-recover";
    public static ProcessStartInfo StartInfo(HandoverPreparation prepared)
    {
        var start = new ProcessStartInfo(Path.Combine(prepared.Request.LayoutRoot, "app-" + prepared.Request.CurrentVersion, "Fyltrix.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = prepared.Request.LayoutRoot };
        start.ArgumentList.Add(ApplyArgument); start.ArgumentList.Add(prepared.RequestPath); start.ArgumentList.Add(prepared.Request.Token);
        return start;
    }
    public static Task<int> RunAsync(string request, string nonce, bool recover = false, CancellationToken token = default) =>
        RunCoreAsync(request, nonce, recover, null, new ApplyOptions(), token);

    internal static async Task<int> RunCoreAsync(string request, string nonce, bool recover, IReadOnlyList<string>? testKeys, ApplyOptions options, CancellationToken token)
    {
        if (!Guid.TryParseExact(nonce, "N", out _)) return 2;
        string folder;
        try
        {
            request = Path.GetFullPath(request); folder = Path.GetDirectoryName(request)!;
            StagingArea.ValidateRoot(Path.GetDirectoryName(folder)!);
            if (Path.GetFileName(folder) != "stage-" + nonce) return 2;
            ApplyResult result;
            if (recover) result = await UpdateRecovery.RecoverAsync(request, nonce, testKeys, options, token).ConfigureAwait(false);
            else
            {
                var handover = await UpdateHandover.WaitForWorkerReadyAsync(request, token, testKeys).ConfigureAwait(false);
                using var ready = handover.Ready;
                result = ready == null ? new(ApplyStatus.Refused, "handover-" + handover.Failure) :
                    await UpdateApplier.ApplyCoreAsync(ready, request, options, token).ConfigureAwait(false);
            }
            await UpdateTransaction.AtomicWriteAsync(Path.Combine(folder, "update-result.json"), JsonSerializer.SerializeToUtf8Bytes(result), CancellationToken.None).ConfigureAwait(false);
            return result.Status == ApplyStatus.Committed ? 0 : result.Status == ApplyStatus.RolledBack ? 3 : 2;
        }
        catch (OperationCanceledException) { return 2; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
            or JsonException or FormatException or System.Security.SecurityException)
        { return 2; }
    }
}
