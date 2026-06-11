using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Solarpunk.Launcher;

/// <summary>
/// HMAC-signed HTTP client for the SolarpunkServer supervisor's snapshot API. The auth secret
/// is SHA256(RconPassword) — same trust tier as RCON, no second credential
/// to manage. Server side: <see cref="BeaconHttpService"/>.
/// </summary>
public sealed class SolarpunkHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly byte[] _authKey;

    public SolarpunkHttpClient(string host, int port, string rconPassword)
    {
        _base = new UriBuilder("http", host, port, "/api/v1/").Uri;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _authKey = string.IsNullOrEmpty(rconPassword)
            ? Array.Empty<byte>()
            : SHA256.HashData(Encoding.UTF8.GetBytes(rconPassword));
    }

    public sealed record Health(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("instance")] string Instance,
        [property: JsonPropertyName("solarpunk_version")] string SolarpunkVersion,
        [property: JsonPropertyName("game_build")] string GameBuild);

    public sealed record SnapshotSummary(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("taken_unix")] long TakenUnix,
        [property: JsonPropertyName("size_bytes")] long SizeBytes,
        [property: JsonPropertyName("sha256")] string Sha256);

    public sealed record SnapshotList(
        [property: JsonPropertyName("snapshots")] List<SnapshotSummary> Snapshots);

    public sealed record OkResult(
        [property: JsonPropertyName("ok")] bool Ok);

    public sealed record ManifestModEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("sha256")] string? Sha256,
        [property: JsonPropertyName("install_root")] string InstallRoot,
        [property: JsonPropertyName("notes")] string? Description);

    public sealed record ManifestBlockedEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("reason")] string? Reason);

    public sealed record Manifest(
        [property: JsonPropertyName("manifest_version")] int ManifestVersion,
        [property: JsonPropertyName("instance")] string Instance,
        [property: JsonPropertyName("solarpunk_version")] string SolarpunkVersion,
        [property: JsonPropertyName("required")] List<ManifestModEntry> Required,
        [property: JsonPropertyName("recommended")] List<ManifestModEntry> Recommended,
        [property: JsonPropertyName("blocked")] List<ManifestBlockedEntry> Blocked);

    public async Task<Health?> GetHealthAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(new Uri(_base, "health"), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<Health>(ct).ConfigureAwait(false);
    }

    public Task<SnapshotList?> ListSnapshotsAsync(CancellationToken ct = default) =>
        SignedGetJsonAsync<SnapshotList>("snapshots", ct);

    /// <summary>
    /// Fetch the mod manifest. Public endpoint, no HMAC — players need
    /// it before they have the join password. Integrity is enforced
    /// per-mod via the pinned sha256 in each ModEntry. See
    /// <c>protocol/manifest-v1.md</c>.
    /// </summary>
    public async Task<Manifest?> GetManifestAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(new Uri(_base, "manifest"), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<Manifest>(ct).ConfigureAwait(false);
    }

    public sealed record ChatMessage(
        [property: JsonPropertyName("ts")] long Ts,
        [property: JsonPropertyName("channel")] string Channel,
        [property: JsonPropertyName("sender")] string? Sender,
        // Wire field is "msg" per protocol/chat-v1.md (server emits ChatMessage.Msg).
        // Property stays Body so ChatFeedService keeps reading m.Body.
        [property: JsonPropertyName("msg")] string Body,
        [property: JsonPropertyName("color")] string? Color,
        [property: JsonPropertyName("target")] string? Target);

    public sealed record ChatRecent(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("instance")] string Instance,
        [property: JsonPropertyName("now_ms")] long NowMs,
        [property: JsonPropertyName("messages")] List<ChatMessage> Messages);

    public sealed record ChatMotd(
        [property: JsonPropertyName("instance")] string Instance,
        [property: JsonPropertyName("msg")] string Msg);

    /// <summary>
    /// Fetch broadcast chat (RCON say + admin /chat/say + scheduled
    /// restart warnings + MOTD). Public endpoint, no HMAC — see
    /// <c>protocol/chat-v1.md</c>.
    /// </summary>
    public async Task<ChatRecent?> GetChatRecentAsync(long sinceMs = 0, int limit = 100, CancellationToken ct = default)
    {
        var rel = $"chat/recent?since={sinceMs}&limit={limit}";
        using var resp = await _http.GetAsync(new Uri(_base, rel), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ChatRecent>(ct).ConfigureAwait(false);
    }

    public async Task<ChatMotd?> GetChatMotdAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(new Uri(_base, "chat/motd"), ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ChatMotd>(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Relay a player-typed chat message from the local BeaconHud overlay to
    /// the server. Public endpoint, no HMAC — see <c>protocol/chat-v1.md</c>
    /// `POST /api/v1/chat/player`. Returns:
    ///   <c>true</c>  — accepted (HTTP 200)
    ///   <c>false</c> — rate-limited or invalid (HTTP 4xx); caller should
    ///                  drop the message rather than retry
    ///   <c>null</c>  — transport failure; caller should retry next tick
    /// </summary>
    public async Task<bool?> PostChatPlayerAsync(string sender, string msg, CancellationToken ct = default)
    {
        var payload = new { sender, msg };
        using var resp = await _http.PostAsJsonAsync(new Uri(_base, "chat/player"), payload, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode) return true;
        // 4xx = drop, 5xx = retry. The 429/400/413 cases are all "this message
        // isn't going to land no matter how many times we resend it".
        if ((int)resp.StatusCode is >= 400 and < 500) return false;
        return null;
    }

    /// <summary>
    /// Download an arbitrary URL (mod zip on Bunny CDN, GH releases,
    /// etc.) to <paramref name="destPath"/>. Streams to disk in
    /// 64 KB chunks. Returns the byte count written, or null on
    /// transport failure. Caller is responsible for hash verification.
    /// </summary>
    public async Task<long?> DownloadFileAsync(string url, string destPath, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        long total = 0;
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = File.Create(destPath);
        var buf = new byte[64 * 1024];
        while (true)
        {
            int n = await s.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n <= 0) break;
            await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            total += n;
        }
        return total;
    }

    /// <summary>
    /// Stream a snapshot zip down to <paramref name="destPath"/>. Returns
    /// the sha256 reported by the server's X-Solarpunk-Sha256 header on
    /// success, null on failure.
    /// </summary>
    public async Task<string?> DownloadSnapshotAsync(string snapshotId, string destPath, CancellationToken ct = default)
    {
        var path = $"snapshots/{snapshotId}/download";
        using var req = BuildSignedRequest(HttpMethod.Get, path, Array.Empty<byte>());
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fs = File.Create(destPath);
        await s.CopyToAsync(fs, ct).ConfigureAwait(false);
        return resp.Headers.TryGetValues("X-Solarpunk-Sha256", out var v) ? v.FirstOrDefault() : null;
    }

    /// <summary>
    /// Upload a zip as a snapshot. Returns the new snapshot id on success.
    /// Streams the file from disk in 64 KB chunks — never allocates a
    /// buffer the size of the body.
    /// </summary>
    public async Task<string?> UploadSnapshotAsync(string zipPath, CancellationToken ct = default)
    {
        using var req = await BuildSignedFileRequestAsync(HttpMethod.Post, "snapshots", zipPath, ct).ConfigureAwait(false);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("snapshot", out var snap)) return null;
        return snap.GetProperty("id").GetString();
    }

    /// <summary>
    /// Upload a zip and immediately restore it as the active world. The game is
    /// stopped, the SaveGames dir is replaced, and the supervisor restarts
    /// the game with the imported world. Streams the upload — does not buffer
    /// the file in memory.
    /// </summary>
    public async Task<bool> ImportAndRestoreAsync(string zipPath, CancellationToken ct = default)
    {
        using var req = await BuildSignedFileRequestAsync(HttpMethod.Post, "snapshots/import-restore", zipPath, ct).ConfigureAwait(false);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return false;
        var ok = await resp.Content.ReadFromJsonAsync<OkResult>(ct).ConfigureAwait(false);
        return ok?.Ok == true;
    }

    public async Task<bool> RestoreSnapshotAsync(string snapshotId, CancellationToken ct = default)
    {
        using var req = BuildSignedRequest(HttpMethod.Post, $"snapshots/{snapshotId}/restore", Array.Empty<byte>());
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return false;
        var ok = await resp.Content.ReadFromJsonAsync<OkResult>(ct).ConfigureAwait(false);
        return ok?.Ok == true;
    }

    private async Task<T?> SignedGetJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        using var req = BuildSignedRequest(HttpMethod.Get, path, Array.Empty<byte>());
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
    }

    private HttpRequestMessage BuildSignedRequest(HttpMethod method, string relPath, byte[] body, string? contentType = null)
    {
        var uri = new Uri(_base, relPath);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var bodySha = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = $"{method.Method.ToUpperInvariant()}\n{uri.AbsolutePath}\n{ts}\n{bodySha}";
        var sig = Convert.ToHexString(
            HMACSHA256.HashData(_authKey, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        var req = new HttpRequestMessage(method, uri);
        req.Headers.Add("X-Solarpunk-Timestamp", ts.ToString());
        req.Headers.Add("X-Solarpunk-Signature", sig);
        if (body.Length > 0)
        {
            req.Content = new ByteArrayContent(body);
            if (contentType is not null)
                req.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }
        return req;
    }

    /// <summary>
    /// Same as <see cref="BuildSignedRequest"/> but for streaming a file
    /// upload. Hashes the file in 64 KB chunks from disk (one pass) and
    /// attaches the file as a <c>StreamContent</c> so HttpClient sends it
    /// chunked / Content-Length'd without ever holding the full payload
    /// in memory.
    /// </summary>
    private async Task<HttpRequestMessage> BuildSignedFileRequestAsync(
        HttpMethod method, string relPath, string filePath, CancellationToken ct)
    {
        var uri = new Uri(_base, relPath);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        string bodySha;
        long length;
        await using (var hashStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
        {
            length = hashStream.Length;
            using var sha = SHA256.Create();
            var buf = new byte[64 * 1024];
            while (true)
            {
                int n = await hashStream.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n <= 0) break;
                sha.TransformBlock(buf, 0, n, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            bodySha = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }

        var canonical = $"{method.Method.ToUpperInvariant()}\n{uri.AbsolutePath}\n{ts}\n{bodySha}";
        var sig = Convert.ToHexString(
            HMACSHA256.HashData(_authKey, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        var req = new HttpRequestMessage(method, uri);
        req.Headers.Add("X-Solarpunk-Timestamp", ts.ToString());
        req.Headers.Add("X-Solarpunk-Signature", sig);

        // Open a second stream that the framework will dispose when the
        // request is sent. StreamContent reads chunk-by-chunk.
        var sendStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        var content = new StreamContent(sendStream, 64 * 1024);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Headers.ContentLength = length;
        req.Content = content;
        return req;
    }

    public void Dispose() => _http.Dispose();
}
