using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NufeCampus.Windows;

public sealed record ReleaseUpdate(Version Version, string Tag, string Notes, Uri Installer, long Size, string? Sha256, Uri? Checksum)
{
    public string Page => "https://github.com/Halcyonjoy/nufe-network-assistant/releases/tag/" + Uri.EscapeDataString(Tag);
}

// Public GitHub requests never use campus credentials or the campus HTTP client.
public sealed class ReleaseUpdates(HttpClient client)
{
    public const string LatestApi = "https://api.github.com/repos/Halcyonjoy/nufe-network-assistant/releases/latest";
    public const long MaxInstallerSize = 250L * 1024 * 1024;
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public static Version CurrentVersion => typeof(ReleaseUpdates).Assembly.GetName().Version is { } v ? new(v.Major, v.Minor, Math.Max(0, v.Build)) : new(0, 0, 0);

    public async Task<ReleaseUpdate> CheckAsync(CancellationToken token)
    {
        using var response = await GetAsync(new(LatestApi), token);
        if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429) return await CheckFromReleasePageAsync(token);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new IOException("仓库暂未发布可用的正式版本。");
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await ReadLimited(response, 1024 * 1024, token));
        return Parse(data.RootElement);
    }
    private async Task<ReleaseUpdate> CheckFromReleasePageAsync(CancellationToken token)
    {
        // The public latest permalink avoids requiring a GitHub token on shared/rate-limited networks.
        using var page = await GetAsync(new("https://github.com/Halcyonjoy/nufe-network-assistant/releases/latest"), token);
        page.EnsureSuccessStatusCode();
        const string prefix = "https://github.com/Halcyonjoy/nufe-network-assistant/releases/tag/";
        string address = page.RequestMessage?.RequestUri?.AbsoluteUri ?? "";
        if (!address.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("无法确认最新 Release 版本，请稍后重试。");
        string tag = Uri.UnescapeDataString(address[prefix.Length..]);
        string number = tag.StartsWith('v') ? tag[1..] : tag;
        if (!System.Text.RegularExpressions.Regex.IsMatch(number, @"^\d+\.\d+\.\d+$") || !Version.TryParse(number, out var version)) throw new InvalidDataException("版本号格式不受支持。");
        var installer = new Uri("https://github.com/Halcyonjoy/nufe-network-assistant/releases/download/" + Uri.EscapeDataString(tag) + $"/NUFE-Campus-Setup-{number}-x64.exe");
        using var asset = await GetAsync(installer, token, HttpMethod.Head); asset.EnsureSuccessStatusCode();
        long size = asset.Content.Headers.ContentLength ?? 0;
        if (size is <= 0 or > MaxInstallerSize) throw new InvalidDataException("无法确认安装包大小，请打开 Release 页面手动下载。");
        var checksum = new Uri(installer.AbsoluteUri + ".sha256");
        using var sum = await GetAsync(checksum, token, HttpMethod.Head); sum.EnsureSuccessStatusCode();
        return new(version, tag, "完整版本说明请打开 Release 页面查看。", installer, size, null, checksum);
    }
    public static ReleaseUpdate Parse(JsonElement root)
    {
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("更新不是正式版本。");
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        string number = tag.StartsWith('v') ? tag[1..] : tag;
        if (!System.Text.RegularExpressions.Regex.IsMatch(number, @"^\d+\.\d+\.\d+$") || !Version.TryParse(number, out var version)) throw new InvalidDataException("版本号格式不受支持。");
        string name = $"NUFE-Campus-Setup-{number}-x64.exe";
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        var matches = assets.Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("此版本缺少唯一的 Windows x64 安装包。");
        var asset = matches[0];
        long size = asset.GetProperty("size").GetInt64();
        if (size is <= 0 or > MaxInstallerSize) throw new InvalidDataException("安装包大小不受支持。");
        Uri AssetUrl(JsonElement item, string filename)
        {
            string expected = "https://github.com/Halcyonjoy/nufe-network-assistant/releases/download/" + Uri.EscapeDataString(tag) + "/" + filename;
            if (item.GetProperty("browser_download_url").GetString() != expected) throw new InvalidDataException("更新下载地址不属于本仓库。");
            return new(expected);
        }
        var installer = AssetUrl(asset, name);
        string? digest = asset.TryGetProperty("digest", out var field) ? field.GetString() : null;
        string? hash = digest?.StartsWith("sha256:", StringComparison.Ordinal) == true ? digest[7..] : null;
        if (hash != null && !ValidHash(hash)) throw new InvalidDataException("安装包校验值无效。");
        Uri? checksum = null;
        var sums = assets.Where(a => a.GetProperty("name").GetString() == name + ".sha256").ToArray();
        if (sums.Length == 1) checksum = AssetUrl(sums[0], name + ".sha256");
        if (hash == null && checksum == null) throw new InvalidDataException("此版本缺少 SHA256 校验信息，无法自动安装。");
        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new(version, tag, notes.Length > 6000 ? notes[..6000] : notes, installer, size, hash, checksum);
    }
    public async Task DownloadAsync(ReleaseUpdate update, string destination, IProgress<int>? progress, CancellationToken token)
    {
        string? expected = update.Sha256;
        if (expected == null)
        {
            using var checksum = await GetAsync(update.Checksum ?? throw new InvalidDataException("缺少校验文件。"), token);
            checksum.EnsureSuccessStatusCode();
            string[] parts = Encoding.UTF8.GetString(await ReadLimited(checksum, 1024, token)).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !ValidHash(parts[0]) || parts[1].TrimStart('*') != Path.GetFileName(update.Installer.AbsolutePath)) throw new InvalidDataException("校验文件格式不正确。");
            expected = parts[0];
        }
        using var response = await GetAsync(update.Installer, token); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != update.Size) throw new InvalidDataException("安装包大小与发布信息不一致。");
        string partial = destination + ".partial";
        try
        {
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                {
                    total += read;
                    if (total > update.Size || total > MaxInstallerSize) throw new InvalidDataException("安装包超出预期大小。");
                    hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token);
                    progress?.Report((int)(total * 100 / update.Size));
                }
                if (total != update.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("安装包校验失败，未启动安装。");
            }
            File.Move(partial, destination);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    private static bool ValidHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken token, HttpMethod? method = null)
    {
        for (int redirects = 0; redirects < 6; redirects++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new InvalidDataException("更新服务器地址不受信任。");
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("NUFE-Campus/" + CurrentVersion);
            request.Headers.Accept.ParseAdd(uri.Host == "api.github.com" ? "application/vnd.github+json" : "application/octet-stream");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                Uri? location = response.Headers.Location; response.Dispose();
                uri = location == null ? throw new InvalidDataException("更新重定向缺少地址。") : new Uri(uri, location);
                continue;
            }
            return response;
        }
        throw new InvalidDataException("更新服务器重定向次数过多。");
    }
    private static async Task<byte[]> ReadLimited(HttpResponseMessage response, int limit, CancellationToken token)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(token); using var result = new MemoryStream();
        byte[] buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) != 0)
        {
            if (result.Length + read > limit) throw new InvalidDataException("更新信息过大。");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
}
