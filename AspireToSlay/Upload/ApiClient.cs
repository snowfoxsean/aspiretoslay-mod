using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AspireToSlay.Upload;

// ── Request / Response DTOs ────────────────────────────────────────────────

/// <summary>
/// Body sent to POST /upload-grants.
/// Identifies the native .run file so the backend can deduplicate and assign
/// a presigned S3 URL.
/// </summary>
internal sealed class UploadGrantRequest
{
    [JsonPropertyName("steam_id")]       public string SteamId       { get; set; } = "";
    [JsonPropertyName("profile_id")]     public string ProfileId     { get; set; } = "";
    [JsonPropertyName("modded")]         public bool   Modded        { get; set; }
    [JsonPropertyName("filename")]       public string Filename      { get; set; } = "";
    [JsonPropertyName("content_hash")]   public string ContentHash   { get; set; } = "";
    [JsonPropertyName("content_length")] public long   ContentLength { get; set; }
    [JsonPropertyName("mod_version")]    public string ModVersion    { get; set; } = "";
}

internal sealed class UploadGrantResponse
{
    [JsonPropertyName("run_id")]      public string  RunId     { get; set; } = "";
    [JsonPropertyName("upload_url")]  public string  UploadUrl { get; set; } = "";
    [JsonPropertyName("expires_at")]  public string? ExpiresAt { get; set; }
    [JsonPropertyName("skipped")]     public bool    Skipped   { get; set; }
    [JsonPropertyName("required_headers")] public Dictionary<string, string>? RequiredHeaders { get; set; }
}

internal sealed class TokenRefreshResponse
{
    [JsonPropertyName("token")] public string Token { get; set; } = "";
}

internal sealed class TokenValidationResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("token")]  public string? Token  { get; set; }
}

internal sealed class ModVersionRequest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

internal sealed class ModVersionResponse
{
    [JsonPropertyName("status")]  public string  Status  { get; set; } = "";
    [JsonPropertyName("message")] public string? Message { get; set; }
}

// ── Client ─────────────────────────────────────────────────────────────────

/// <summary>
/// Thin HTTP client for the AspireToSlay backend API.
/// Stateless — callers pass the token explicitly.
///
/// Supports main/backup URL cycling:
///   main → fail → backup → fail → main → …
/// The backup URL is derived from the main URL by changing the first path
/// segment from "/ingest" to "/ingest-backup", or set via ASPIRE_BACKUP_API_URL.
/// </summary>
internal sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions _json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string MainBaseUrl =
        (Environment.GetEnvironmentVariable("ASPIRE_API_URL")
         ?? "https://ingest.aspiretoslay.com").TrimEnd('/');

    private static readonly string BackupBaseUrl =
        (Environment.GetEnvironmentVariable("ASPIRE_BACKUP_API_URL")
         ?? MainBaseUrl.Replace("ingest.aspiretoslay.com", "ingest-backup.aspiretoslay.com")).TrimEnd('/');

    /// <summary>
    /// Tracks which URL to try next: false = main, true = backup.
    /// Flipped after each upload failure so we cycle between them.
    /// </summary>
    private bool _useBackup;

    /// <summary>Returns the base URL to use for the current attempt.</summary>
    private string CurrentBaseUrl => _useBackup ? BackupBaseUrl : MainBaseUrl;

    /// <summary>Flips to the other URL (main ↔ backup).</summary>
    internal void FlipUrl() => _useBackup = !_useBackup;

    public ApiClient()
    {
        _http = new HttpClient
        {
            // BaseAddress is not used — we build full URIs per request
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── Upload grants ──────────────────────────────────────────────────────

    /// <summary>
    /// Requests a presigned S3 PUT URL for the given .run file.
    /// Returns null on failure (caller logs the error).
    /// </summary>
    public async Task<UploadGrantResponse?> RequestUploadGrantAsync(
        string modToken,
        UploadGrantRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(request, _json);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{CurrentBaseUrl}/upload-grants")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", modToken);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                MainFile.Logger.Error(
                    $"[ApiClient] upload-grants {(int)resp.StatusCode}: {err[..Math.Min(200, err.Length)]}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<UploadGrantResponse>(json, _json);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[ApiClient] RequestUploadGrant error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Like <see cref="RequestUploadGrantAsync"/> but also returns the HTTP status code
    /// so the caller can detect 401 (token expired) and attempt a refresh.
    /// </summary>
    public async Task<(UploadGrantResponse?, int)> RequestUploadGrantWithStatusAsync(
        string modToken,
        UploadGrantRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(request, _json);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{CurrentBaseUrl}/upload-grants")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", modToken);

            using var resp = await _http.SendAsync(req, ct);
            var statusCode = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                MainFile.Logger.Error(
                    $"[ApiClient] upload-grants {statusCode}: {err[..Math.Min(200, err.Length)]}");
                return (null, statusCode);
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<UploadGrantResponse>(json, _json);
            return (result, statusCode);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[ApiClient] RequestUploadGrant error: {ex.Message}");
            return (null, 0);
        }
    }

    // ── S3 upload ──────────────────────────────────────────────────────────

    /// <summary>
    /// PUTs the gzipped .run file to S3 using the presigned URL.
    /// </summary>
    public async Task<bool> UploadToS3Async(
        string presignedUrl,
        Dictionary<string, string>? requiredHeaders,
        string filePath,
        CancellationToken ct = default)
    {
        try
        {
            await using var fileStream = File.OpenRead(filePath);
            var length = new FileInfo(filePath).Length;

            using var req = new HttpRequestMessage(HttpMethod.Put, presignedUrl)
            {
                Content = new StreamContent(fileStream),
            };
            req.Content.Headers.ContentType   = new MediaTypeHeaderValue("application/gzip");
            req.Content.Headers.ContentLength = length;

            if (requiredHeaders is not null)
            {
                foreach (var (k, v) in requiredHeaders)
                    req.Headers.TryAddWithoutValidation(k, v);
            }

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                MainFile.Logger.Error(
                    $"[ApiClient] S3 PUT {(int)resp.StatusCode}: {err[..Math.Min(200, err.Length)]}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[ApiClient] UploadToS3 error: {ex.Message}");
            return false;
        }
    }

    // ── Token refresh ──────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to refresh an expired mod token via POST /tokens/refresh.
    /// Returns the new token on success, or null on failure.
    /// </summary>
    public async Task<string?> RefreshTokenAsync(
        string expiredToken,
        CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{CurrentBaseUrl}/tokens/refresh");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", expiredToken);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                MainFile.Logger.Error(
                    $"[ApiClient] token refresh {(int)resp.StatusCode}: {err[..Math.Min(200, err.Length)]}");
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<TokenRefreshResponse>(json, _json);
            return result?.Token;
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[ApiClient] RefreshToken error: {ex.Message}");
            return null;
        }
    }

    // ── Token validation ───────────────────────────────────────────────────

    /// <summary>
    /// Validates the mod token against the backend.
    /// Returns a <see cref="TokenValidationResponse"/> with status:
    ///   "valid"     — token is active and current
    ///   "refreshed" — token was expired; new token in <c>Token</c> property
    ///   "invalid"   — token is revoked/bogus; user must re-auth
    /// Returns null on network/transport error.
    /// </summary>
    public async Task<TokenValidationResponse?> ValidateTokenAsync(
        string modToken,
        CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{MainBaseUrl}/token-validation");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", modToken);

            using var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                MainFile.Logger.Error(
                    $"[ApiClient] token-validation {(int)resp.StatusCode}: {json[..Math.Min(200, json.Length)]}");
                return null;
            }

            return JsonSerializer.Deserialize<TokenValidationResponse>(json, _json);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[ApiClient] ValidateToken error: {ex.Message}");
            return null;
        }
    }

    // ── Mod version check ──────────────────────────────────────────────────

    /// <summary>
    /// Checks whether the current mod version is up-to-date.
    /// Returns a <see cref="ModVersionResponse"/> with status:
    ///   "ok"              — version is current
    ///   "update_available" — newer version exists (non-blocking)
    ///   "requires_update"  — version is below minimum (uploads blocked)
    /// Returns null on network/transport error.
    /// </summary>
    public async Task<ModVersionResponse?> CheckModVersionAsync(
        CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(
                new ModVersionRequest { Version = ModConstants.ModVersion }, _json);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{MainBaseUrl}/mod-version")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };

            using var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                MainFile.Logger.Error(
                    $"[ApiClient] mod-version {(int)resp.StatusCode}: {json[..Math.Min(200, json.Length)]}");
                return null;
            }

            return JsonSerializer.Deserialize<ModVersionResponse>(json, _json);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[ApiClient] CheckModVersion error: {ex.Message}");
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
