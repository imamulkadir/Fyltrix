using Fyltrix.Core;
using Fyltrix.Preview;

namespace Fyltrix.Tests;

[Collection("UI")] // changes AppSettings.Folder
public class DiagnosticsTests
{
    [Theory]
    [InlineData("bytes=0-", 1000, 0, 999)]
    [InlineData("bytes=10-19", 1000, 10, 19)]
    [InlineData("bytes=990-5000", 1000, 990, 999)]
    [InlineData("bytes=-100", 1000, 900, 999)]
    [InlineData("bytes=-5000", 1000, 0, 999)]
    [InlineData("bytes=0-0,5-9", 1000, 0, 0)]
    public void Range_header_is_parsed(string header, long total, long from, long to)
    {
        Assert.True(PreviewServer.TryParseRange(header, total, out var a, out var b));
        Assert.Equal((from, to), (a, b));
    }

    [Theory]
    [InlineData("bytes=1000-")]
    [InlineData("bytes=20-10")]
    [InlineData("items=0-1")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=abc")]
    public void Bad_range_headers_are_rejected(string header) => Assert.False(PreviewServer.TryParseRange(header, 1000, out _, out _));

    [Fact]
    public void Errors_are_logged_with_context_and_share_tokens_are_masked()
    {
        using var t = new TempDir();
        var old = AppSettings.Folder;
        AppSettings.Folder = t.Path;
        try
        {
            Exception ex;
            try { throw new InvalidOperationException("outer", new IOException("inner cause")); }
            catch (Exception e) { ex = e; }
            Diagnostics.Log("Media preview", ex, @"C:\Videos\movie.mkv via http://192.168.1.5:8765/s/AbCdEfGhIjKlMnOpQrStUvWxYz0123456789_-/");
            var log = File.ReadAllText(Diagnostics.LogPath);
            Assert.Contains("ERROR Media preview", log);
            Assert.Contains("Fyltrix " + Diagnostics.Version, log);
            Assert.Contains("Operation: C:\\Videos\\movie.mkv", log);
            Assert.Contains("System.InvalidOperationException: outer", log);
            Assert.Contains("inner cause", log);
            Assert.Contains("/s/<token>/", log);
            Assert.DoesNotContain("AbCdEfGhIjKlMnOp", log);

            var report = Diagnostics.Crash("Unhandled (fatal)", ex);
            Assert.NotNull(report);
            Assert.Contains("FATAL", File.ReadAllText(report!));

            // a log that can't be written (folder replaced by a file) is ignored, never thrown
            AppSettings.Folder = Path.Combine(t.Path, "error.log", "nested");
            Diagnostics.Info("Test", "goes nowhere");
        }
        finally { AppSettings.Folder = old; }
    }
}
