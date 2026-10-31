using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Fyltrix.Launcher;

/// <summary>Bounded, current-user, PID-bound health channel. No file-based health acknowledgements.</summary>
public static class HealthWire
{
    public static async Task WriteAsync(Stream stream, IReadOnlyDictionary<string, string> fields, CancellationToken token)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        { writer.WriteStartObject(); foreach (var pair in fields) writer.WriteString(pair.Key, pair.Value); writer.WriteEndObject(); }
        if (bytes.Length > 16 * 1024) throw new InvalidDataException("Oversized health message.");
        byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, (int)bytes.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes.ToArray(), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    public static async Task<Dictionary<string, string>> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 2 or > 16 * 1024) throw new InvalidDataException("Invalid health frame.");
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid health message.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in json.RootElement.EnumerateObject())
            if (property.Value.ValueKind != JsonValueKind.String || !fields.TryAdd(property.Name, property.Value.GetString()!))
                throw new InvalidDataException("Ambiguous health message.");
        return fields;
    }
    public static bool ClientIs(NamedPipeServerStream pipe, int processId) => GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint actual) && actual == processId;
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}

public sealed class TrialConnection : IDisposable
{
    private readonly NamedPipeClientStream pipe;
    private readonly string token, version;
    public string Profile { get; }
    public string Cache { get; }
    public string Version => version;
    private TrialConnection(NamedPipeClientStream pipe, string token, string version, string profile, string cache)
    { this.pipe = pipe; this.token = token; this.version = version; Profile = profile; Cache = cache; }

    public static TrialConnection? Connect(string executableDirectory)
    {
        string? nonce = Environment.GetEnvironmentVariable("FYLTRIX_UPDATE_TRIAL");
        if (nonce == null) return null;
        if (!Guid.TryParseExact(nonce, "N", out _)) throw new InvalidDataException("Invalid trial token.");
        string? root = UpdateProtocol.PayloadRoot(executableDirectory);
        if (root == null || !UpdateProtocol.Supported(root)) throw new InvalidDataException("Unsupported trial layout.");
        string version = Path.GetFileName(Path.TrimEndingDirectorySeparator(executableDirectory))[4..];
        var current = ReleaseLayout.Resolve(root) ?? throw new InvalidDataException("Missing previous payload.");
        if (!int.TryParse(Environment.GetEnvironmentVariable("FYLTRIX_UPDATE_OWNER"), out int owner)) throw new InvalidDataException("Missing trial owner.");
        var pipe = new NamedPipeClientStream(".", "Fyltrix.Update." + nonce, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            pipe.ConnectAsync(timeout.Token).GetAwaiter().GetResult();
            if (!HealthWire.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actual) || actual != owner) throw new InvalidDataException("Wrong trial owner.");
            using var process = Process.GetProcessById(owner);
            if (!Path.GetFullPath(process.MainModule!.FileName).Equals(current.Executable, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Trial owner is outside the previous payload.");
            HealthWire.WriteAsync(pipe, new Dictionary<string, string> { ["token"] = nonce, ["version"] = version }, timeout.Token).GetAwaiter().GetResult();
            var reply = HealthWire.ReadAsync(pipe, timeout.Token).GetAwaiter().GetResult();
            if (reply.Count != 4 || reply.GetValueOrDefault("token") != nonce || reply.GetValueOrDefault("contract") != "1"
                || !reply.TryGetValue("profile", out var profile) || !Path.IsPathFullyQualified(profile)
                || !reply.TryGetValue("cache", out var cache) || !Path.IsPathFullyQualified(cache)) throw new InvalidDataException("Invalid trial bootstrap.");
            return new(pipe, nonce, version, profile, cache);
        }
        catch { pipe.Dispose(); throw; }
    }
    public async Task ConfirmAndWaitForCloseAsync(CancellationToken cancellationToken)
    {
        await HealthWire.WriteAsync(pipe, new Dictionary<string, string> { ["token"] = token, ["version"] = version, ["ready"] = "1", ["contract"] = "1" }, cancellationToken).ConfigureAwait(false);
        var command = await HealthWire.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
        if (command.Count != 1 || command.GetValueOrDefault("close") != token) throw new InvalidDataException("Invalid trial completion.");
    }
    public void Dispose() => pipe.Dispose();
}
