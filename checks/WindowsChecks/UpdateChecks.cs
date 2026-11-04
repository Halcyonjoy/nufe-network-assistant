using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NufeCampus.Windows;

internal static class UpdateChecks
{
    public static async Task<int> RunAsync()
    {
        byte[] payload = Encoding.UTF8.GetBytes("synthetic installer, never executed");
        string url = "https://github.com/Halcyonjoy/nufe-network-assistant/releases/download/v9.8.7/NUFE-Campus-Setup-9.8.7-x64.exe";
        string json = JsonSerializer.Serialize(new
        {
            draft = false,
            prerelease = false,
            tag_name = "v9.8.7",
            body = "synthetic notes",
            assets = new[] { new { name = "NUFE-Campus-Setup-9.8.7-x64.exe", browser_download_url = url, size = payload.Length, digest = "sha256:" + Convert.ToHexString(SHA256.HashData(payload)) } }
        });
        using var wire = new UpdateWire(json, payload); using var http = new HttpClient(wire);
        var client = new ReleaseUpdates(http);
        var release = await client.CheckAsync(CancellationToken.None);
        if (release.Version != new Version(9, 8, 7) || release.Version <= ReleaseUpdates.CurrentVersion) throw new Exception("Version comparison failed");
        wire.RateLimited = true;
        var fallback = await client.CheckAsync(CancellationToken.None);
        if (fallback.Version != release.Version || fallback.Checksum == null) throw new Exception("Rate limit fallback failed");
        wire.RateLimited = false;
        using (var invalid = JsonDocument.Parse(json.Replace(url, "https://example.invalid/setup.exe", StringComparison.Ordinal)))
        {
            try { ReleaseUpdates.Parse(invalid.RootElement); throw new Exception("Foreign installer accepted"); }
            catch (InvalidDataException) { }
        }
        Console.WriteLine("PASS Release version selection, rate limit fallback and repository download restriction");
        string cacheRoot;
        using (var cache = new UpdateCache())
        {
            cacheRoot = cache.Root;
            await client.DownloadAsync(release, cache.Installer, null, CancellationToken.None);
            if (!File.ReadAllBytes(cache.Installer).SequenceEqual(payload)) throw new Exception("Verified payload mismatch");
            File.Delete(cache.Installer); wire.Corrupt = true;
            try { await client.DownloadAsync(release, cache.Installer, null, CancellationToken.None); throw new Exception("Corrupt installer accepted"); }
            catch (InvalidDataException) { }
            if (Directory.GetFiles(cache.Root).Any(f => Path.GetFileName(f) != "update.lock")) throw new Exception("Partial download remains");
        }
        if (Directory.Exists(cacheRoot)) throw new Exception("Update cache remains after failure");
        Console.WriteLine("PASS Verified download, corruption rejection and temporary file cleanup");
        using (var cache = new UpdateCache()) cacheRoot = cache.Root;
        Directory.CreateDirectory(cacheRoot); File.WriteAllText(Path.Combine(cacheRoot, "setup.exe.partial"), "synthetic interruption");
        Directory.SetLastWriteTimeUtc(cacheRoot, DateTime.UtcNow.AddMinutes(-10));
        UpdateCache.CleanStale();
        if (Directory.Exists(cacheRoot)) throw new Exception("Interrupted update cache remains");
        Console.WriteLine("PASS Stale interrupted update cache cleanup");
        return 0;
    }
    private sealed class UpdateWire(string json, byte[] payload) : HttpMessageHandler
    {
        public bool Corrupt, RateLimited;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (RateLimited && request.RequestUri!.Host == "api.github.com") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (request.RequestUri!.Host == "github.com" && request.RequestUri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new("https://github.com/Halcyonjoy/nufe-network-assistant/releases/tag/v9.8.7");
                return Task.FromResult(redirect);
            }
            byte[] bytes = payload.ToArray(); if (Corrupt) bytes[0] ^= 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = request.RequestUri!.Host == "api.github.com" ? new StringContent(json) : new ByteArrayContent(bytes) });
        }
    }
}
