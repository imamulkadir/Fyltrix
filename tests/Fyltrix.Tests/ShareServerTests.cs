using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Fyltrix.Core.Sharing;

namespace Fyltrix.Tests;

/// <summary>The share host (loopback only: these tests never listen on the network).</summary>
public class ShareServerTests
{
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };

    private static ShareHost NewHost(TempDir t) => new(t["store/shares.json"], preferredPort: 0);

    /// <summary>Raw request, so traversal tricks reach the server exactly as typed (HttpClient would normalise them).</summary>
    private static async Task<string> Raw(int port, string requestLine)
    {
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, port);
        using var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes(requestLine + "\r\nHost: x\r\n\r\n"));
        using var r = new StreamReader(s);
        return await r.ReadToEndAsync();
    }

    [Fact]
    public async Task Serves_only_shared_items_with_the_token()
    {
        using var t = new TempDir();
        t.File("shared/docs/a.txt", "alpha");
        t.File("shared/b.bin", "0123456789");
        t.File("sharedSibling/secret.txt", "nope");
        t.File("outside.txt", "nope");
        var single = t.File("one.txt", "one file");
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{t["shared/jump"]}\" \"{t["sharedSibling"]}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();

        using var host = NewHost(t);
        var s = host.Start(new[] { t["shared"], single }, new ShareOptions());
        var url = host.Urls(s).Single();
        Assert.Equal($"http://127.0.0.1:{host.Port}/share/{s.Token}/", url);
        Assert.False(host.ListeningOnLan);

        var index = await Http.GetStringAsync(url);
        Assert.Contains("Shared from Fyltrix", index);
        Assert.Contains("shared", index);
        Assert.Contains("one.txt", index);
        Assert.Equal("alpha", await Http.GetStringAsync(url + "0/docs/a.txt"));
        Assert.Equal("one file", await Http.GetStringAsync(url + "1/"));
        var listing = await Http.GetStringAsync(url + "0/");
        Assert.Contains("docs", listing);
        Assert.DoesNotContain("jump", listing); // junctions aren't listed…
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(url + "0/jump/secret.txt")).StatusCode); // …or followed

        // wrong token, other paths, traversal attempts
        var bad = url.Replace(s.Token, s.Token[..^1] + (s.Token[^1] == 'A' ? 'B' : 'A'));
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(bad)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"http://127.0.0.1:{host.Port}/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync($"http://127.0.0.1:{host.Port}/s/{s.Token}/")).StatusCode);
        foreach (var attack in new[] { "0/../outside.txt", "0/..%2Foutside.txt", "0/%2e%2e/outside.txt", "0/..%5Coutside.txt", "0/docs%5C..%5C..%5Coutside.txt", "1/x", "7/", "0/C:%5Coutside.txt" })
        {
            var resp = await Raw(host.Port, $"GET /share/{s.Token}/{attack} HTTP/1.1");
            Assert.StartsWith("HTTP/1.1 404", resp);
            Assert.DoesNotContain("nope", resp);
        }
        Assert.StartsWith("HTTP/1.1 405", await Raw(host.Port, $"POST /share/{s.Token}/0/docs/a.txt HTTP/1.1"));
    }

    [Fact]
    public async Task Range_requests_headers_and_zip_download()
    {
        using var t = new TempDir();
        t.File("f/b.bin", "0123456789");
        t.File("f/sub/c.txt", "see");
        using var host = NewHost(t);
        var url = host.Urls(host.Start(new[] { t["f"] }, new ShareOptions()))[0];

        var req = new HttpRequestMessage(HttpMethod.Get, url + "0/b.bin");
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(2, 5);
        var resp = await Http.SendAsync(req);
        Assert.Equal(HttpStatusCode.PartialContent, resp.StatusCode);
        Assert.Equal("2345", await resp.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", resp.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("attachment", resp.Content.Headers.ContentDisposition!.DispositionType);

        var zipBytes = await Http.GetByteArrayAsync(url + "0/?zip");
        using var zip = new ZipArchive(new MemoryStream(zipBytes));
        Assert.Equal(new[] { "b.bin", "sub/c.txt" }, zip.Entries.Select(e => e.FullName).OrderBy(n => n));
    }

    [Fact]
    public async Task Expiry_revokes_the_link_and_releases_the_port()
    {
        using var t = new TempDir();
        var f = t.File("x.txt", "x");
        using var host = NewHost(t);
        var s = host.Start(new[] { f }, new ShareOptions { Lifetime = TimeSpan.FromMilliseconds(700) });
        int changes = 0;
        host.Changed += (_, _) => Interlocked.Increment(ref changes);
        var url = host.Urls(s)[0];
        Assert.Equal("x", await Http.GetStringAsync(url + "0/"));
        // State is set before the timer publishes Changed and releases the listener.
        await WaitFor(() => s.State == ShareState.Expired && Volatile.Read(ref changes) > 0 && !host.Listening, 5000);
        Assert.True(changes > 0);
        Assert.Equal(TimeSpan.Zero, s.Remaining);
        Assert.False(host.Listening); // nothing shared: nothing listening
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Http.GetStringAsync(url + "0/"));
    }

    [Fact]
    public async Task Stop_invalidates_the_link_at_once_and_cuts_off_a_download_in_progress()
    {
        using var t = new TempDir();
        using (var fs = File.Create(t["big.bin"])) fs.SetLength(512L * 1024 * 1024);
        t.File("small.txt", "still here");
        using var host = NewHost(t);
        var big = host.Start(new[] { t["big.bin"] }, new ShareOptions());
        var other = host.Start(new[] { t["small.txt"] }, new ShareOptions());
        var url = host.Urls(big)[0];

        using var resp = await Http.GetAsync(url + "0/", HttpCompletionOption.ResponseHeadersRead);
        using var body = await resp.Content.ReadAsStreamAsync();
        var buf = new byte[1 << 16];
        Assert.True(await body.ReadAsync(buf) > 0);
        host.Stop(big);
        Assert.Equal(ShareState.Stopped, big.State);
        long read = 0;
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            int n;
            while ((n = await body.ReadAsync(buf)) > 0) read += n;
            if (read < 512L * 1024 * 1024 - buf.Length) throw new IOException("ended early");
        });
        Assert.True(read < 512L * 1024 * 1024);

        // the stopped link is gone; the other share keeps working
        Assert.Equal(HttpStatusCode.Gone, (await Http.GetAsync(url)).StatusCode);
        Assert.Equal("still here", await Http.GetStringAsync(host.Urls(other)[0] + "0/"));
    }

    [Fact]
    public async Task An_existing_share_is_recognised_even_after_a_rename_and_can_be_extended()
    {
        using var t = new TempDir();
        var f = t.File("Movie.mkv", "frames");
        t.File("other/Movie.mkv", "a different file with the same name");
        using var host = NewHost(t);
        var s = host.Start(new[] { f }, new ShareOptions { Lifetime = TimeSpan.FromMinutes(15) });

        Assert.Same(s, host.FindActive(new[] { f }));
        Assert.Null(host.FindActive(new[] { t["other/Movie.mkv"] })); // same name, different file
        Assert.Null(host.FindActive(new[] { f, t["other/Movie.mkv"] }));

        File.Move(f, t["Movie (renamed).mkv"]);
        Assert.Same(s, host.FindActive(new[] { t["Movie (renamed).mkv"] }));
        Assert.Equal("frames", await Http.GetStringAsync(host.Urls(s)[0] + "0/")); // still served from where it is now

        var before = s.ExpiresUtc;
        host.Extend(s, TimeSpan.FromMinutes(15));
        Assert.Equal(before + TimeSpan.FromMinutes(15), s.ExpiresUtc);
        Assert.NotEqual(s.ExpiresUtc, s.OriginalExpiresUtc);
        Assert.InRange(s.Remaining.TotalMinutes, 29, 30);
    }

    [Fact]
    public async Task After_a_restart_shares_are_paused_until_resumed_and_expired_ones_never_return()
    {
        using var t = new TempDir();
        var f = t.File("doc.txt", "hello");
        string token, quickToken;
        using (var first = NewHost(t))
        {
            var s = first.Start(new[] { f }, new ShareOptions { Lifetime = TimeSpan.FromHours(1) });
            var quick = first.Start(new[] { f }, new ShareOptions { Lifetime = TimeSpan.FromMilliseconds(400) });
            token = s.Token;
            quickToken = quick.Token;
        } // Fyltrix exits
        Assert.DoesNotContain(token, File.ReadAllText(t["store/shares.json"])); // the token is stored encrypted
        await Task.Delay(800);

        using var second = NewHost(t);
        var restored = second.Sessions.Single(x => x.Token == token);
        var expired = second.Sessions.Single(x => x.Token == quickToken);
        Assert.Equal(ShareState.Interrupted, restored.State);
        Assert.Equal(ShareState.Expired, expired.State);
        Assert.False(second.Listening); // nothing is served until the user resumes it
        Assert.Null(second.FindActive(new[] { f }));

        Assert.False(await second.ResumeAsync(expired));
        Assert.Equal(ShareState.Expired, expired.State);
        Assert.True(await second.ResumeAsync(restored));
        Assert.Equal(ShareState.Active, restored.State);
        Assert.Equal("hello", await Http.GetStringAsync(second.Urls(restored)[0] + "0/"));
        Assert.Same(restored, second.FindActive(new[] { f }));
    }

    [Fact]
    public void Link_addresses_put_real_adapters_first()
    {
        var wsl = new LanAddress(IPAddress.Parse("172.20.192.1"), "vEthernet (WSL)", "wired", false, true);
        var bluetooth = new LanAddress(IPAddress.Parse("169.254.229.134"), "Bluetooth Network Connection", "wired", false, true);
        var linkLocal = new LanAddress(IPAddress.Parse("169.254.10.10"), "Ethernet 3", "wired", false, false);
        var ethernet = new LanAddress(IPAddress.Parse("192.168.0.226"), "Ethernet 2", "wired", true, false);
        var wifi = new LanAddress(IPAddress.Parse("192.168.0.144"), "Wi-Fi", "Wi-Fi", true, false);
        var noGateway = new LanAddress(IPAddress.Parse("10.0.0.5"), "Ethernet 4", "wired", false, false);
        var ranked = ShareNetwork.Rank(new[] { wsl, bluetooth, linkLocal, noGateway, wifi, ethernet });
        Assert.Equal(new[] { ethernet, wifi, noGateway, linkLocal, wsl, bluetooth }, ranked);

        // on this PC: whatever is found, a virtual adapter is never preferred over a real one
        var here = ShareNetwork.LanAddresses();
        if (here.Any(a => !a.Virtual)) Assert.False(here[0].Virtual);
    }

    [Fact]
    public void Firewall_rule_is_narrow_and_the_check_reads_without_admin()
    {
        var args = ShareFirewall.AddRuleArguments(8765, @"C:\Apps\Fyltrix\Fyltrix.exe");
        Assert.Contains("dir=in", args);
        Assert.Contains("protocol=TCP localport=8765", args);
        Assert.Contains("program=\"C:\\Apps\\Fyltrix\\Fyltrix.exe\"", args);
        Assert.Contains("remoteip=localsubnet", args);
        Assert.Contains("profile=private,domain", args);
        Assert.DoesNotContain("public", args);
        Assert.True(ShareFirewall.PortListContains("80,8000-8999", 8765));
        Assert.False(ShareFirewall.PortListContains("80,9000-9999", 8765));

        var report = ShareFirewall.Check(8765, Environment.ProcessPath);
        Assert.False(string.IsNullOrEmpty(report.Explain()));
    }

    [Fact]
    public void Firewall_upgrade_updates_the_owned_rule_without_adding_a_duplicate_or_widening_access()
    {
        var args = ShareFirewall.UpdateRuleArguments(8765, @"C:\Apps with spaces\Fyltrix\Fyltrix.exe");
        Assert.StartsWith("advfirewall firewall set rule name=\"FileLens file sharing (TCP 8765)\" dir=in new ", args);
        Assert.Contains("program=\"C:\\Apps with spaces\\Fyltrix\\Fyltrix.exe\"", args);
        Assert.Contains("protocol=TCP localport=8765 remoteip=localsubnet profile=private,domain enable=yes", args);
        Assert.DoesNotContain("add rule", args);
        Assert.DoesNotContain("public", args);
        _ = ShareFirewall.HasOwnedRule(8765); // Read-only COM path; no elevation or policy writes.
    }

    /// <summary>
    /// Opt-in (FL_LAN_CHECK=1): listens on the network and requests the generated link through this PC's own LAN
    /// address. Proves the binding and URL; it can't prove another device gets through the firewall.
    /// </summary>
    [Fact]
    public async Task Lan_share_is_reachable_on_the_lan_address_and_lan_off_shares_are_not()
    {
        if (Environment.GetEnvironmentVariable("FL_LAN_CHECK") != "1") return;
        using var t = new TempDir();
        var f = t.File("lan.txt", "over the network");
        var p = t.File("private.txt", "this pc only");
        using var host = new ShareHost(null, ShareHost.DefaultPort);
        var lan = host.Start(new[] { f }, new ShareOptions { AllowLan = true });
        var local = host.Start(new[] { p }, new ShareOptions { AllowLan = false });
        Assert.True(host.ListeningOnLan);
        var urls = host.Urls(lan);
        Assert.StartsWith("http://192.168.", urls[0]); // this PC's real adapter, not WSL's 172.20.x
        Assert.Equal("over the network", await Http.GetStringAsync(urls[0] + "0/"));
        var lanIp = new Uri(urls[0]).Host;
        Assert.Equal(HttpStatusCode.NotFound, (await Http.GetAsync(host.Url(local, lanIp) + "0/")).StatusCode);
        Assert.Equal("this pc only", await Http.GetStringAsync(host.Url(local, "127.0.0.1") + "0/"));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "fyltrix-lan-check.txt"), string.Join("\n", urls) + $"\nport {host.Port}");
        host.Stop(lan);
        Assert.False(host.ListeningOnLan); // back to loopback only while just the local share is left
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.4.5.6", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.70.1.1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("2001:4860::1", false)]
    [InlineData("::ffff:192.168.0.5", true)]
    public void Private_address_ranges(string ip, bool isPrivate) => Assert.Equal(isPrivate, ShareNetwork.IsPrivate(IPAddress.Parse(ip)));

    private static async Task WaitFor(Func<bool> condition, int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(50);
        }
    }
}
