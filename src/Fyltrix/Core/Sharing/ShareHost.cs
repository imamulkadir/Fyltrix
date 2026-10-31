using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Fyltrix.Core.Sharing;

public sealed class ShareOptions
{
    /// <summary>False (default): only this PC can connect. True: devices on the local network can, using the link.</summary>
    public bool AllowLan { get; init; }
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromHours(1);
}

public sealed record ShareTransfer(string SessionId, string Client, string Item, long Bytes, bool Completed);

/// <summary>Active: being served. Expired/Stopped: the link is dead for good. Interrupted: Fyltrix closed while it was active; it can be resumed (with consent) until it would have expired.</summary>
public enum ShareState { Active, Expired, Stopped, Interrupted }

/// <summary>A shared file or folder, remembered by its on-disk identity so a rename or move (same drive) doesn't lose it.</summary>
public sealed class SharedItem
{
    public string Path { get; set; } = "";
    public string? ItemId { get; set; }
    public bool IsFolder { get; set; }

    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;

    public static SharedItem? From(string path)
    {
        var full = System.IO.Path.GetFullPath(path.TrimEnd('\\') is { Length: > 2 } p ? p : path);
        bool dir = Directory.Exists(full);
        if (!dir && !File.Exists(full)) return null;
        return new SharedItem { Path = full, IsFolder = dir, ItemId = Native.ItemId(full) };
    }

    /// <summary>Where the item is now: its last known path if that is still the same item, else wherever it was renamed or moved to; null if it's gone.</summary>
    public string? Locate()
    {
        bool exists = IsFolder ? Directory.Exists(Path) : File.Exists(Path);
        if (ItemId == null) return exists ? Path : null;
        if (exists && Native.ItemId(Path) == ItemId) return Path;
        var moved = Native.PathOfItemId(ItemId, Path);
        if (moved != null && (IsFolder ? Directory.Exists(moved) : File.Exists(moved))) { Path = moved; return moved; }
        return null;
    }

    public bool SameAs(SharedItem other) =>
        ItemId != null && other.ItemId != null ? ItemId == other.ItemId : string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
}

public sealed class ShareSession
{
    internal CancellationTokenSource Cancel { get; private set; } = new();

    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>The secret part of the link (32 random bytes). Never logged.</summary>
    public string Token { get; init; } = NewToken();
    public List<SharedItem> Items { get; init; } = new();
    public bool AllowLan { get; init; }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime OriginalExpiresUtc { get; init; }
    public DateTime ExpiresUtc { get; internal set; }
    public ShareState State { get; internal set; } = ShareState.Active;
    public DateTime? EndedUtc { get; internal set; }
    public int Downloads { get; internal set; }

    public string Title => Items.Count == 1 ? Items[0].Name : $"{Items.Count} items";
    public TimeSpan Remaining => State == ShareState.Active ? Max(TimeSpan.Zero, ExpiresUtc - DateTime.UtcNow) : TimeSpan.Zero;

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    internal static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    internal void ResetCancel()
    {
        Cancel.Dispose();
        Cancel = new CancellationTokenSource();
    }

    public bool Covers(IReadOnlyList<SharedItem> wanted) =>
        wanted.Count == Items.Count && wanted.All(w => Items.Any(i => i.SameAs(w)));
}

/// <summary>
/// The one local HTTP server behind every share. It listens only while something is shared: on 127.0.0.1 when
/// every active share is for this PC only, on all IPv4 interfaces when a share allows the local network (and
/// even then it refuses connections from outside private address ranges and serves LAN-off shares to this PC
/// only). It prefers a fixed port (8765 by default) so a firewall rule and the links stay the same.
///
/// Every URL is /share/&lt;token&gt;/…; only the shared items (and what is inside shared folders) can be reached;
/// links and junctions are never followed. Shares end when they expire or are stopped: the token stops working at
/// once and downloads still in progress are cut off. Active shares are written to shares.json (token encrypted
/// for this Windows user) so that after a restart they can be resumed, but never automatically, and never once
/// they would have expired.
/// </summary>
public sealed class ShareHost : IDisposable
{
    public const int DefaultPort = 8765;
    private static ShareHost? _current;

    private readonly object _gate = new();
    private readonly List<ShareSession> _sessions = new();
    private readonly SemaphoreSlim _connections = new(32);
    private readonly Timer _tick;
    private readonly string? _storePath;
    private readonly int _preferredPort;
    private TcpListener? _listener;
    private CancellationTokenSource? _listenCts;
    private bool _listeningLan, _disposed;

    /// <summary>The app-wide host (store in %AppData%\FileLens\shares.json).</summary>
    public static ShareHost Current => _current ??= new ShareHost(Path.Combine(AppSettings.Folder, "shares.json"), DefaultPort);

    public ShareHost(string? storePath, int preferredPort)
    {
        _storePath = storePath;
        _preferredPort = preferredPort;
        Load();
        _tick = new Timer(_ => ExpireDue(), null, Timeout.Infinite, Timeout.Infinite);
        UpdateTick();
    }

    /// <summary>The expiry check runs once a second only while a share is active (every change of state passes through
    /// RaiseChanged), instead of ticking for the whole session with nothing shared.</summary>
    private void UpdateTick()
    {
        bool any;
        lock (_gate) any = _sessions.Any(s => s.State == ShareState.Active);
        try { _tick.Change(any ? TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan, any ? TimeSpan.FromSeconds(1) : Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Something about the shares changed (started, stopped, expired, extended, downloaded). Raised on a worker thread.</summary>
    public event EventHandler? Changed;
    public event EventHandler<ShareTransfer>? Transfer;

    public int Port { get; private set; }
    public bool Listening => _listener != null;
    public bool ListeningOnLan => _listener != null && _listeningLan;

    public IReadOnlyList<ShareSession> Sessions { get { lock (_gate) return _sessions.ToList(); } }
    public IReadOnlyList<ShareSession> ActiveSessions { get { lock (_gate) return _sessions.Where(s => s.State == ShareState.Active).ToList(); } }

    /// <summary>The active share of exactly these items (found even if they were renamed or moved since), or null.</summary>
    public ShareSession? FindActive(IEnumerable<string> paths)
    {
        var wanted = paths.Select(SharedItem.From).OfType<SharedItem>().ToList();
        if (wanted.Count == 0) return null;
        lock (_gate) return _sessions.FirstOrDefault(s => s.State == ShareState.Active && s.Covers(wanted));
    }

    public ShareSession Start(IEnumerable<string> paths, ShareOptions options)
    {
        var items = paths.Select(SharedItem.From).OfType<SharedItem>().ToList();
        items = items.Where((it, i) => !items.Take(i).Any(prev => prev.SameAs(it))).ToList();
        if (items.Count == 0) throw new ArgumentException("Nothing to share: the items no longer exist.");
        if (options.Lifetime <= TimeSpan.Zero) throw new ArgumentException("The share needs a duration.");
        var now = DateTime.UtcNow;
        var s = new ShareSession { Items = items, AllowLan = options.AllowLan, CreatedUtc = now, OriginalExpiresUtc = now + options.Lifetime, ExpiresUtc = now + options.Lifetime };
        lock (_gate)
        {
            _sessions.Add(s);
            try { EnsureListener(); }
            catch { _sessions.Remove(s); throw; }
        }
        Save();
        RaiseChanged();
        return s;
    }

    public void Extend(ShareSession s, TimeSpan by)
    {
        lock (_gate)
        {
            if (s.State != ShareState.Active) throw new InvalidOperationException("Only an active share can be extended.");
            s.ExpiresUtc = (s.ExpiresUtc > DateTime.UtcNow ? s.ExpiresUtc : DateTime.UtcNow) + by;
        }
        Save();
        RaiseChanged();
    }

    public void Stop(ShareSession s) => End(s, ShareState.Stopped);

    public void StopAll()
    {
        foreach (var s in ActiveSessions) End(s, ShareState.Stopped, save: false);
        Save();
    }

    /// <summary>Forget ended shares (the list in the Shares window).</summary>
    public void ClearEnded()
    {
        lock (_gate) _sessions.RemoveAll(s => s.State is ShareState.Expired or ShareState.Stopped);
        Save();
        RaiseChanged();
    }

    /// <summary>
    /// Restarts a share that was active when Fyltrix last closed. It only counts as active again once the server
    /// answers its link; otherwise it stays interrupted. Shares past their expiry never come back.
    /// </summary>
    public async Task<bool> ResumeAsync(ShareSession s)
    {
        lock (_gate)
        {
            if (s.State != ShareState.Interrupted) return s.State == ShareState.Active;
            if (DateTime.UtcNow >= s.ExpiresUtc) { s.State = ShareState.Expired; s.EndedUtc = s.ExpiresUtc; }
            else if (s.Items.All(i => i.Locate() == null)) { s.State = ShareState.Stopped; s.EndedUtc = DateTime.UtcNow; }
            else
            {
                s.ResetCancel();
                s.State = ShareState.Active;
                try { EnsureListener(); }
                catch (SocketException) { s.State = ShareState.Interrupted; return false; }
            }
        }
        if (s.State != ShareState.Active) { Save(); RaiseChanged(); return false; }
        bool ok = await ValidateAsync(s);
        if (!ok)
        {
            lock (_gate) s.State = ShareState.Interrupted;
            EnsureListenerSafe();
        }
        Save();
        RaiseChanged();
        return ok;
    }

    private async Task<bool> ValidateAsync(ShareSession s)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var r = await http.GetAsync($"http://127.0.0.1:{Port}/share/{s.Token}/");
            return r.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    /// <summary>Links for a share: the given (or best) LAN address first when the share allows the network, then this PC's own.</summary>
    public List<string> Urls(ShareSession s, IEnumerable<LanAddress>? lan = null)
    {
        var list = new List<string>();
        if (s.AllowLan) list.AddRange((lan ?? ShareNetwork.LanAddresses()).Select(a => Url(s, a.Address.ToString())));
        list.Add(Url(s, "127.0.0.1"));
        return list;
    }

    public string Url(ShareSession s, string host) => $"http://{host}:{Port}/share/{s.Token}/";

    private void End(ShareSession s, ShareState state, bool save = true)
    {
        lock (_gate)
        {
            if (s.State is not (ShareState.Active or ShareState.Interrupted)) return;
            s.State = state;
            s.EndedUtc = DateTime.UtcNow;
            s.Cancel.Cancel(); // cuts off downloads in progress
            EnsureListenerSafe();
        }
        if (save) Save();
        RaiseChanged();
    }

    private void ExpireDue()
    {
        List<ShareSession> due;
        lock (_gate) due = _sessions.Where(s => s.State == ShareState.Active && DateTime.UtcNow >= s.ExpiresUtc).ToList();
        foreach (var s in due) End(s, ShareState.Expired);
    }

    // ---------------------------------------------------------------- listener lifecycle

    private void EnsureListenerSafe()
    {
        try { EnsureListener(); }
        catch (SocketException ex) { Diagnostics.Log("Sharing", ex, "rebinding the share server"); }
    }

    /// <summary>Listens only while needed, and on the network only while a LAN share is active. Call under _gate.</summary>
    private void EnsureListener()
    {
        var active = _sessions.Where(s => s.State == ShareState.Active).ToList();
        if (active.Count == 0 || _disposed) { StopListener(); return; }
        bool lan = active.Any(s => s.AllowLan);
        if (_listener != null && _listeningLan == lan) return;
        int keepPort = Port;
        StopListener();
        var address = lan ? IPAddress.Any : IPAddress.Loopback;
        var ports = new List<int>();
        if (keepPort > 0) ports.Add(keepPort);
        if (_preferredPort > 0) ports.AddRange(Enumerable.Range(_preferredPort, 10));
        ports.Add(0);
        SocketException? last = null;
        foreach (var port in ports.Distinct())
        {
            var l = new TcpListener(address, port);
            l.Server.ExclusiveAddressUse = true; // nobody else can bind the same port and intercept requests
            try { l.Start(); }
            catch (SocketException ex) { last = ex; continue; }
            _listener = l;
            _listeningLan = lan;
            Port = ((IPEndPoint)l.LocalEndpoint).Port;
            _listenCts = new CancellationTokenSource();
            _ = AcceptLoop(l, _listenCts.Token);
            return;
        }
        throw last ?? new SocketException((int)SocketError.AddressAlreadyInUse);
    }

    private void StopListener()
    {
        _listenCts?.Cancel();
        _listenCts = null;
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
    }

    public void Dispose()
    {
        _tick.Dispose();
        lock (_gate)
        {
            _disposed = true;
            // shares that were running end with the process; they are remembered as interrupted, not stopped
            foreach (var s in _sessions.Where(s => s.State == ShareState.Active)) { s.State = ShareState.Interrupted; s.Cancel.Cancel(); }
            StopListener();
        }
        Save();
        if (ReferenceEquals(_current, this)) _current = null;
    }

    // ---------------------------------------------------------------- persistence

    private sealed class StoredItem { public string Path { get; set; } = ""; public string? ItemId { get; set; } public bool IsFolder { get; set; } }
    private sealed class StoredSession
    {
        public string Id { get; set; } = "";
        public string ProtectedToken { get; set; } = "";
        public List<StoredItem> Items { get; set; } = new();
        public bool AllowLan { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime OriginalExpiresUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public ShareState State { get; set; }
        public DateTime? EndedUtc { get; set; }
        public int Downloads { get; set; }
    }

    private void Save()
    {
        if (_storePath == null) return;
        try
        {
            List<StoredSession> data;
            lock (_gate)
                data = _sessions.Select(s => new StoredSession
                {
                    Id = s.Id, ProtectedToken = Protect(s.Token), AllowLan = s.AllowLan, CreatedUtc = s.CreatedUtc, OriginalExpiresUtc = s.OriginalExpiresUtc,
                    ExpiresUtc = s.ExpiresUtc, State = s.State, EndedUtc = s.EndedUtc, Downloads = s.Downloads,
                    Items = s.Items.Select(i => new StoredItem { Path = i.Path, ItemId = i.ItemId, IsFolder = i.IsFolder }).ToList(),
                }).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
            var tmp = _storePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _storePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { Diagnostics.Log("Sharing", ex, "saving shares.json"); }
    }

    private void Load()
    {
        if (_storePath == null || !File.Exists(_storePath)) return;
        try
        {
            var data = JsonSerializer.Deserialize<List<StoredSession>>(File.ReadAllText(_storePath)) ?? new();
            var now = DateTime.UtcNow;
            foreach (var d in data)
            {
                string token;
                try { token = Unprotect(d.ProtectedToken); }
                catch (CryptographicException) { continue; } // written by another Windows user/PC: unusable
                var s = new ShareSession
                {
                    Id = d.Id, Token = token, AllowLan = d.AllowLan, CreatedUtc = d.CreatedUtc, OriginalExpiresUtc = d.OriginalExpiresUtc,
                    Items = d.Items.Select(i => new SharedItem { Path = i.Path, ItemId = i.ItemId, IsFolder = i.IsFolder }).ToList(),
                };
                s.ExpiresUtc = d.ExpiresUtc;
                s.Downloads = d.Downloads;
                s.EndedUtc = d.EndedUtc;
                // nothing is served after a restart until the user resumes it; expired shares never come back
                s.State = d.State switch
                {
                    ShareState.Active or ShareState.Interrupted when now < d.ExpiresUtc => ShareState.Interrupted,
                    ShareState.Active or ShareState.Interrupted => ShareState.Expired,
                    _ => d.State,
                };
                if (s.State == ShareState.Expired) s.EndedUtc ??= d.ExpiresUtc;
                // keep the list short: ended shares are kept for a day
                if (s.State is ShareState.Expired or ShareState.Stopped && (s.EndedUtc ?? d.ExpiresUtc) < now.AddDays(-1)) continue;
                _sessions.Add(s);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Diagnostics.Log("Sharing", ex, "reading shares.json"); }
    }

    private static string Protect(string token) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));

    private static string Unprotect(string data)
    {
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data), null, DataProtectionScope.CurrentUser)); }
        catch (FormatException ex) { throw new CryptographicException("bad token", ex); }
    }

    private void RaiseChanged()
    {
        UpdateTick();
        try { Changed?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Diagnostics.Log("Sharing", ex, "share change handler"); }
    }

    // ---------------------------------------------------------------- HTTP

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException or InvalidOperationException) { return; }
            if (!await _connections.WaitAsync(0)) { client.Dispose(); continue; } // too many at once
            _ = Task.Run(async () =>
            {
                try { await Handle(client); }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
                catch (Exception ex) { Diagnostics.Log("Sharing", ex, "serving a request"); }
                finally { client.Dispose(); _connections.Release(); }
            });
        }
    }

    private ShareSession? SessionFor(string token)
    {
        if (token.Length == 0) return null;
        var bytes = Encoding.ASCII.GetBytes(token);
        lock (_gate)
            return _sessions.FirstOrDefault(s => s.Token.Length == token.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s.Token), bytes));
    }

    private async Task Handle(TcpClient client)
    {
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        using var stream = client.GetStream();
        stream.ReadTimeout = 30_000;
        stream.WriteTimeout = 120_000;
        if (!ShareNetwork.IsPrivate(remote.Address)) { await Status(stream, 403, "Forbidden", "Only devices on your local network can use this link."); return; }

        var (method, target, headers) = await ReadRequest(stream);
        if (method == null) return;
        if (method is not ("GET" or "HEAD")) { await Status(stream, 405, "Method Not Allowed", "Read-only share."); return; }

        // /share/<token>/[<item index>/<relative path>][?zip]
        var pathPart = target!.Split('?')[0];
        bool zip = target.Contains("?zip", StringComparison.Ordinal);
        var segments = pathPart.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToList();
        if (segments.Count < 2 || segments[0] != "share") { await Status(stream, 404, "Not Found", "Not found."); return; }
        var session = SessionFor(segments[1]);
        // a share for this PC only doesn't exist for anyone else
        if (session == null || (!session.AllowLan && !IPAddress.IsLoopback(remote.Address))) { await Status(stream, 404, "Not Found", "Not found."); return; }
        if (session.State != ShareState.Active || DateTime.UtcNow >= session.ExpiresUtc)
        {
            await Status(stream, 410, "Gone", session.State == ShareState.Expired || DateTime.UtcNow >= session.ExpiresUtc ? "This share has expired." : "This share has ended.");
            return;
        }
        var ct = session.Cancel.Token;
        if (segments.Count == 2) { await Html(stream, method, IndexPage(session)); return; }
        if (!int.TryParse(segments[2], out var index) || index < 0 || index >= session.Items.Count) { await Status(stream, 404, "Not Found", "Not found."); return; }
        var resolved = Resolve(session, index, segments.Skip(3).ToList());
        if (resolved == null) { await Status(stream, 404, "Not Found", "Not found."); return; }

        var who = remote.Address.ToString();
        if (Directory.Exists(resolved))
        {
            if (zip) await SendZip(stream, method, resolved, session, who, ct);
            else await Html(stream, method, FolderPage(session, index, segments.Skip(3).ToList(), resolved));
        }
        else await SendFile(stream, method, resolved, headers.TryGetValue("range", out var range) ? range : null, session, who, ct);
    }

    /// <summary>Maps URL segments to a path inside a shared item, or null for anything outside it or through a link.</summary>
    internal static string? Resolve(ShareSession session, int index, IReadOnlyList<string> rel)
    {
        var root = session.Items[index].Locate();
        if (root == null) return null;
        if (rel.Count == 0) return root;
        if (File.Exists(root)) return null; // a shared file has nothing below it
        foreach (var s in rel)
            if (s is "." or ".." || s.IndexOfAny(new[] { '\\', '/', ':', '\0' }) >= 0 || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        var full = Path.GetFullPath(Path.Combine(new[] { root }.Concat(rel).ToArray()));
        if (!full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        // every step below the root must be a real folder/file, not a junction or symbolic link
        for (var p = full; p.Length > root.Length; p = Path.GetDirectoryName(p)!)
        {
            if (!File.Exists(p) && !Directory.Exists(p)) return null;
            var attrs = File.GetAttributes(p);
            if ((attrs & FileAttributes.ReparsePoint) != 0 || (attrs & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System)) return null;
        }
        return full;
    }

    private static async Task<(string? method, string? target, Dictionary<string, string> headers)> ReadRequest(NetworkStream stream)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[16 * 1024];
        int len = 0;
        while (true)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(len, buffer.Length - len));
            if (n == 0) return (null, null, headers);
            len += n;
            int end = buffer.AsSpan(0, len).IndexOf("\r\n\r\n"u8);
            if (end >= 0)
            {
                var lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length < 3) return (null, null, headers);
                foreach (var l in lines.Skip(1))
                {
                    var i = l.IndexOf(':');
                    if (i > 0) headers[l[..i].Trim()] = l[(i + 1)..].Trim();
                }
                return (first[0], first[1], headers);
            }
            if (len == buffer.Length) return (null, null, headers); // header too large
        }
    }

    private const string SecurityHeaders =
        "X-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nCache-Control: no-store\r\nX-Frame-Options: DENY\r\n" +
        "Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'\r\nConnection: close\r\n";

    private static async Task Write(NetworkStream s, string text) => await s.WriteAsync(Encoding.UTF8.GetBytes(text));

    private static async Task Status(NetworkStream s, int code, string reason, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        await Write(s, $"HTTP/1.1 {code} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\n{SecurityHeaders}\r\n");
        await s.WriteAsync(body);
    }

    private static async Task Html(NetworkStream s, string method, string html)
    {
        var body = Encoding.UTF8.GetBytes(html);
        await Write(s, $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n{SecurityHeaders}\r\n");
        if (method == "GET") await s.WriteAsync(body);
    }

    private static string Esc(string s) => WebUtility.HtmlEncode(s);
    private static string Seg(string s) => Uri.EscapeDataString(s);

    private static string Page(ShareSession session, string title, string body) =>
        "<!doctype html><html><head><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>" + Esc(title) + "</title>" +
        "<style>body{font:15px system-ui,sans-serif;margin:0;background:#1b1b1f;color:#e6e6ea}main{max-width:760px;margin:0 auto;padding:20px}" +
        "a{color:#5aa9ff;text-decoration:none}li{padding:9px 4px;border-bottom:1px solid #34343e;display:flex;gap:12px}li span{margin-left:auto;color:#a0a0aa}" +
        "ul{list-style:none;padding:0}small{color:#a0a0aa}</style></head><body><main><h2>" + Esc(title) + "</h2>" + body +
        $"<p><small>Shared from {ProductBranding.Name} · read-only · ends {session.ExpiresUtc.ToLocalTime():t}</small></p></main></body></html>";

    private static string IndexPage(ShareSession session)
    {
        var sb = new StringBuilder("<ul>");
        for (int i = 0; i < session.Items.Count; i++)
        {
            var path = session.Items[i].Locate();
            if (path == null) continue;
            bool dir = Directory.Exists(path);
            var name = Path.GetFileName(path) is { Length: > 0 } n ? n : path;
            sb.Append($"<li><a href='{i}/'>{(dir ? "📁 " : "📄 ")}{Esc(name)}</a>");
            sb.Append(dir ? $"<span><a href='{i}/?zip'>download as ZIP</a></span>" : $"<span>{FileOps.FormatSize(new FileInfo(path).Length)}</span>");
            sb.Append("</li>");
        }
        return Page(session, "Shared files", sb.Append("</ul>").ToString());
    }

    private static string FolderPage(ShareSession session, int root, List<string> rel, string dir)
    {
        var baseUrl = $"/share/{session.Token}/{root}/" + string.Concat(rel.Select(r => Seg(r) + "/"));
        var sb = new StringBuilder($"<p><a href='/share/{session.Token}/'>All shared items</a> · <a href='{baseUrl}?zip'>download this folder as ZIP</a></p><ul>");
        var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint })
            .OrderBy(e => (e.Attributes & FileAttributes.Directory) == 0).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).Take(5000);
        foreach (var e in entries)
        {
            bool isDir = (e.Attributes & FileAttributes.Directory) != 0;
            sb.Append($"<li><a href='{baseUrl}{Seg(e.Name)}{(isDir ? "/" : "")}'>{(isDir ? "📁 " : "📄 ")}{Esc(e.Name)}</a>");
            if (!isDir) sb.Append($"<span>{FileOps.FormatSize(((FileInfo)e).Length)}</span>");
            sb.Append("</li>");
        }
        return Page(session, Path.GetFileName(dir), sb.Append("</ul>").ToString());
    }

    private async Task SendFile(NetworkStream s, string method, string path, string? range, ShareSession session, string who, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
        long length = fs.Length, start = 0, end = length - 1;
        bool partial = false;
        if (range != null && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) && !range.Contains(','))
        {
            var parts = range[6..].Split('-');
            if (parts.Length == 2 && length > 0)
            {
                bool okStart = long.TryParse(parts[0], out var a), okEnd = long.TryParse(parts[1], out var b);
                if (okStart) { start = a; end = okEnd ? Math.Min(b, length - 1) : length - 1; }
                else if (okEnd) { start = Math.Max(0, length - b); end = length - 1; }
                if (start > end || start >= length)
                {
                    await Write(s, $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{length}\r\nContent-Length: 0\r\n{SecurityHeaders}\r\n");
                    return;
                }
                partial = true;
            }
        }
        long count = length == 0 ? 0 : end - start + 1;
        var name = Path.GetFileName(path);
        var disposition = $"attachment; filename=\"{new string(name.Select(c => c < 32 || c > 126 || c == '"' ? '_' : c).ToArray())}\"; filename*=UTF-8''{Seg(name)}";
        await Write(s, $"HTTP/1.1 {(partial ? "206 Partial Content" : "200 OK")}\r\nContent-Type: {FileKinds.MimeFor(path).Replace("text/html", "text/plain")}\r\n" +
                       $"Content-Length: {count}\r\nAccept-Ranges: bytes\r\nContent-Disposition: {disposition}\r\n" +
                       (partial ? $"Content-Range: bytes {start}-{end}/{length}\r\n" : "") + SecurityHeaders + "\r\n");
        if (method == "HEAD") return;
        fs.Position = start;
        var buf = new byte[1 << 16];
        long sent = 0;
        while (sent < count)
        {
            ct.ThrowIfCancellationRequested(); // the share was stopped or expired: the download is cut off
            int n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, count - sent)), ct);
            if (n <= 0) break;
            await s.WriteAsync(buf.AsMemory(0, n), ct);
            sent += n;
            if (sent % (4 << 20) < n) Transfer?.Invoke(this, new ShareTransfer(session.Id, who, name, sent, false));
        }
        if (sent == count) { lock (_gate) session.Downloads++; }
        Transfer?.Invoke(this, new ShareTransfer(session.Id, who, name, sent, sent == count));
    }

    /// <summary>Streams a folder as a ZIP built on the fly (no temporary file).</summary>
    private async Task SendZip(NetworkStream s, string method, string dir, ShareSession session, string who, CancellationToken ct)
    {
        var name = (Path.GetFileName(dir) is { Length: > 0 } n ? n : "folder") + ".zip";
        await Write(s, $"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Disposition: attachment; filename*=UTF-8''{Seg(name)}\r\n{SecurityHeaders}\r\n");
        if (method == "HEAD") return;
        long total = 0;
        await Task.Run(() =>
        {
            using var zip = new ZipArchive(s, ZipArchiveMode.Create, leaveOpen: true);
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint };
            foreach (var f in Directory.EnumerateFiles(dir, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                // AttributesToSkip keeps junctions and symlinked folders out, so nothing outside the share is reached
                var entry = zip.CreateEntry(Path.GetRelativePath(dir, f).Replace('\\', '/'), CompressionLevel.Fastest);
                using var input = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = entry.Open();
                input.CopyTo(output);
                total += input.Length;
            }
        }, ct);
        lock (_gate) session.Downloads++;
        Transfer?.Invoke(this, new ShareTransfer(session.Id, who, name, total, true));
    }
}
