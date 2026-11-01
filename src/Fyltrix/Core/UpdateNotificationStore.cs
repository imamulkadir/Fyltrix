using System.Text.Json;
using Fyltrix.Updates;

namespace Fyltrix.Core;

internal sealed record UpdateNotificationHistory(int Schema = 1, DateTimeOffset? LastChecked = null,
    string? LastNotifiedVersion = null, string? LastAvailableVersion = null, string Product = UpdateNotificationStore.ProductTag,
    DateTimeOffset? RetryAfter = null);

/// <summary>Updater-only metadata. Background checks never save the user's application settings.</summary>
internal sealed class UpdateNotificationStore(string? root = null)
{
    internal const string ProductTag = "Fyltrix.UpdateNotifications";
    private readonly string folder = Path.GetFullPath(root ?? StagingArea.DefaultRoot);
    private string FileName => Path.Combine(folder, "notification-state.json");
    private UpdateNotificationHistory ReadOwned()
    {
        if (new FileInfo(FileName).Length > 4096) throw new IOException("Notification metadata too large.");
        using var json = JsonDocument.Parse(File.ReadAllText(FileName));
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("Product", out var product) || product.ValueKind != JsonValueKind.String || product.GetString() != ProductTag ||
            !json.RootElement.TryGetProperty("Schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int number) || number != 1)
            throw new IOException("Notification metadata is not owned by this version.");
        return json.RootElement.Deserialize<UpdateNotificationHistory>() ?? throw new IOException("Invalid notification state.");
    }
    internal UpdateNotificationHistory Load()
    {
        try
        {
            CheckLinks();
            if (!File.Exists(FileName)) return new();
            return ReadOwned();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    internal void Save(UpdateNotificationHistory value)
    {
        CheckLinks();
        // Refuse to replace an unrelated/corrupt pre-existing file; no application config is ever reset.
        if (File.Exists(FileName)) _ = ReadOwned();
        Directory.CreateDirectory(folder);
        string pending = Path.Combine(folder, ".notification-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, value);
            File.Move(pending, FileName, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    private void CheckLinks()
    {
        for (var directory = new DirectoryInfo(folder); directory != null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected notification storage.");
        if (File.Exists(FileName) && (File.GetAttributes(FileName) & FileAttributes.ReparsePoint) != 0) throw new IOException("Redirected notification state.");
    }
}
