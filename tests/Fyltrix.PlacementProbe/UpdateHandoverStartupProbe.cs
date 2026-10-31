using System.IO;
using System.Text.Json;
using Fyltrix.UI;
using Fyltrix.Updates;

// Used only by the explicitly gated startup hook on a private desktop, never shipped with the application.
internal static class UpdateHandoverStartupProbe
{
    internal static Action<MainWindow> Create(string root, string stageDirectory, string publicKeyFile)
    {
        HandoverPreparation? prepared = null;
        bool preparing = false, closing = false;
        async Task Prepare()
        {
            try
            {
                string publicKey = (await File.ReadAllTextAsync(publicKeyFile)).Trim();
                var stage = await UpdateHandover.LoadStageAsync(stageDirectory, [publicKey]) ?? throw new InvalidDataException("Invalid test stage.");
                var result = await UpdateHandover.PrepareAsync(stage, Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName);
                prepared = result.Preparation ?? throw new InvalidDataException(result.Failure.ToString());
                File.WriteAllText(Path.Combine(root, "handover-prepared.json"), JsonSerializer.Serialize(new
                    { request = prepared.RequestPath, processes = prepared.Request.Processes.Length }));
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(root, "update-probe-error.txt"), ex.GetType().Name + ": " + ex.Message); }
        }
        async Task Close(MainWindow window)
        {
            try
            {
                var result = await UpdateHandover.RequestCloseAsync(prepared!, () =>
                {
                    window.Dispatcher.VerifyAccess();
                    var method = typeof(MainWindow).GetMethod("TryCloseForUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    return (bool)method.Invoke(window, null)!;
                });
                File.WriteAllText(Path.Combine(root, "update-close.json"), JsonSerializer.Serialize(new { accepted = result == HandoverFailure.None }));
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(root, "update-probe-error.txt"), ex.GetType().Name + ": " + ex.Message); }
        }
        return window =>
        {
            if (!preparing && File.Exists(Path.Combine(root, "prepare-for-update"))) { preparing = true; _ = Prepare(); }
            if (prepared != null && !closing && File.Exists(Path.Combine(root, "release-for-update"))) { closing = true; _ = Close(window); }
        };
    }
}
