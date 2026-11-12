using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ludusavo.Commands;
using ludusavo.Models;

namespace ludusavo.Services;

public class GoogleDriveService : IGoogleDriveService
{
    private readonly IConfigService _configService;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, string> _folderCache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, RemoteGameMeta>? _cachedRemoteMetas;

    public bool IsConfigured => _configService.Settings.IsGoogleDriveConfigured;

    public GoogleDriveService(IConfigService configService)
    {
        _configService = configService;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    private async Task<bool> EnsureAuthenticatedAsync()
    {
        var settings = _configService.Settings;
        if (!IsConfigured) return false;

        // Refresh token if expired or nearing expiration
        if (string.IsNullOrEmpty(settings.GoogleAccessToken) ||
            !settings.GoogleTokenExpiry.HasValue ||
            settings.GoogleTokenExpiry.Value <= DateTime.UtcNow.AddMinutes(2))
        {
            var refreshed = await RefreshTokenAsync();
            if (!refreshed) return false;
        }

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.GoogleAccessToken);
        return true;
    }

    private async Task<bool> RefreshTokenAsync()
    {
        var settings = _configService.Settings;
        if (string.IsNullOrWhiteSpace(settings.GoogleRefreshToken))
            return false;

        try
        {
            var formParams = new Dictionary<string, string>
            {
                ["client_id"] = settings.EffectiveClientId,
                ["refresh_token"] = settings.GoogleRefreshToken,
                ["grant_type"] = "refresh_token"
            };

            if (!string.IsNullOrWhiteSpace(settings.EffectiveClientSecret))
            {
                formParams["client_secret"] = settings.EffectiveClientSecret;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            {
                Content = new FormUrlEncodedContent(formParams)
            };

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return false;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var tokenProp))
            {
                settings.GoogleAccessToken = tokenProp.GetString() ?? string.Empty;
                var expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
                settings.GoogleTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);
                _configService.SaveSettings(settings);
                return true;
            }
        }
        catch { }

        return false;
    }

    public async Task<(bool success, string? message)> LoginAsync(Action<string>? statusCallback = null)
    {
        var settings = _configService.Settings;
        if (string.IsNullOrWhiteSpace(settings.EffectiveClientId) || string.IsNullOrWhiteSpace(settings.EffectiveClientSecret))
        {
            return (false, "Google Client ID or Client Secret not set. Please run 'sav config' first.");
        }

        // Generate PKCE code verifier and code challenge (RFC 7636)
        byte[] randomBytes = RandomNumberGenerator.GetBytes(32);
        string codeVerifier = Convert.ToBase64String(randomBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        byte[] challengeHash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        string codeChallenge = Convert.ToBase64String(challengeHash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        // Find an available local port
        var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        int port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        string redirectUri = $"http://127.0.0.1:{port}/";
        string scope = "https://www.googleapis.com/auth/drive.file https://www.googleapis.com/auth/userinfo.email";
        string authUrl = $"https://accounts.google.com/o/oauth2/v2/auth?client_id={Uri.EscapeDataString(settings.EffectiveClientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=code&scope={Uri.EscapeDataString(scope)}&access_type=offline&prompt=consent&code_challenge={Uri.EscapeDataString(codeChallenge)}&code_challenge_method=S256";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        statusCallback?.Invoke("Opening browser for Google login...");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = authUrl,
                UseShellExecute = true
            });
        }
        catch
        {
            statusCallback?.Invoke($"Could not automatically open browser. Please open the following link manually:\n{authUrl}");
        }

        statusCallback?.Invoke("Waiting for browser authentication (click Allow)...");

        var getContextTask = listener.GetContextAsync();
        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(2));
        var completedTask = await Task.WhenAny(getContextTask, timeoutTask);

        if (completedTask == timeoutTask)
        {
            listener.Stop();
            return (false, "Login timed out.");
        }

        var context = await getContextTask;
        var req = context.Request;
        var code = req.QueryString["code"];
        var error = req.QueryString["error"];

        var responseHtml = @"<!DOCTYPE html>
<html>
<head><meta charset='utf-8'><title>sav CLI - Google Drive</title></head>
<body style='font-family:-apple-system,BlinkMacSystemFont,Segoe UI,Roboto,sans-serif;text-align:center;padding-top:60px;background:#0f172a;color:#f8fafc;'>
  <h2 style='color:#4ade80;'>&#x2713; Google Drive login successful!</h2>
  <p style='color:#94a3b8;font-size:16px;'>You can close this browser tab and return to the sav terminal.</p>
</body>
</html>";

        var buffer = Encoding.UTF8.GetBytes(responseHtml);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer);
        context.Response.OutputStream.Close();
        listener.Stop();

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            return (false, $"Login cancelled or denied: {error ?? "Authorization code not received"}");
        }

        statusCallback?.Invoke("Retrieving token from Google...");

        var tokenParams = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = settings.EffectiveClientId,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = codeVerifier
        };

        if (!string.IsNullOrWhiteSpace(settings.EffectiveClientSecret))
        {
            tokenParams["client_secret"] = settings.EffectiveClientSecret;
        }

        using var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
        {
            Content = new FormUrlEncodedContent(tokenParams)
        };

        var tokenRes = await _httpClient.SendAsync(tokenReq);
        if (!tokenRes.IsSuccessStatusCode)
        {
            var errBody = await tokenRes.Content.ReadAsStringAsync();
            return (false, $"Failed to exchange Google token: {errBody}");
        }

        var tokenJson = await tokenRes.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(tokenJson);
        var root = doc.RootElement;

        settings.GoogleAccessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
        if (root.TryGetProperty("refresh_token", out var rfProp))
        {
            settings.GoogleRefreshToken = rfProp.GetString() ?? string.Empty;
        }
        var expiresIn = root.TryGetProperty("expires_in", out var expProp) ? expProp.GetInt32() : 3600;
        settings.GoogleTokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn);

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.GoogleAccessToken);

        // Retrieve user email
        try
        {
            var userRes = await _httpClient.GetAsync("https://www.googleapis.com/oauth2/v2/userinfo");
            if (userRes.IsSuccessStatusCode)
            {
                var userJson = await userRes.Content.ReadAsStringAsync();
                using var userDoc = JsonDocument.Parse(userJson);
                if (userDoc.RootElement.TryGetProperty("email", out var emailProp))
                {
                    settings.UserEmail = emailProp.GetString() ?? string.Empty;
                }
            }
        }
        catch { }

        // Initialize 'sav' root folder on Google Drive
        statusCallback?.Invoke("Checking 'sav' folder on Google Drive...");
        await GetOrCreateRootFolderIdAsync();

        _configService.SaveSettings(settings);
        return (true, $"Successfully logged in to Google account: {settings.UserEmail}!");
    }

    public async Task<(bool success, string? message)> TestConnectionAsync()
    {
        if (!IsConfigured)
            return (false, "Google Drive is not configured! Please run 'sav config' first.");

        var ok = await EnsureAuthenticatedAsync();
        if (!ok)
            return (false, "Could not authenticate with Google Drive (token invalid or expired). Please run 'sav login'.");

        try
        {
            var (used, total, email) = await GetStorageQuotaAsync();
            var usedStr = ConsoleHelper.FormatSize(used);
            var totalStr = total > 0 ? ConsoleHelper.FormatSize(total) : "Unlimited";

            var rootId = await GetOrCreateRootFolderIdAsync();
            return (true, $"Connected to Google Drive successfully! Account: {email ?? _configService.Settings.UserEmail} (Used: {usedStr} / {totalStr})");
        }
        catch (Exception ex)
        {
            return (false, $"Google Drive connection error: {ex.Message}");
        }
    }

    public async Task<(long usedBytes, long totalBytes, string? userEmail)> GetStorageQuotaAsync()
    {
        await EnsureAuthenticatedAsync();
        var res = await _httpClient.GetAsync("https://www.googleapis.com/drive/v3/about?fields=user(emailAddress),storageQuota(limit,usage)");
        if (!res.IsSuccessStatusCode) return (0, 0, null);

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? email = null;
        if (root.TryGetProperty("user", out var u) && u.TryGetProperty("emailAddress", out var ep))
        {
            email = ep.GetString();
        }

        long used = 0, limit = 0;
        if (root.TryGetProperty("storageQuota", out var sq))
        {
            if (sq.TryGetProperty("usage", out var up) && long.TryParse(up.GetString(), out var uVal))
                used = uVal;
            if (sq.TryGetProperty("limit", out var lp) && long.TryParse(lp.GetString(), out var lVal))
                limit = lVal;
        }

        return (used, limit, email);
    }

    private string EscapeQuery(string text) => text.Replace("\\", "\\\\").Replace("'", "\\'");

    private async Task<string> GetOrCreateRootFolderIdAsync()
    {
        var settings = _configService.Settings;
        if (!string.IsNullOrEmpty(settings.GoogleRootFolderId))
        {
            // Check if folder still exists
            var checkRes = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files/{settings.GoogleRootFolderId}?fields=id,trashed");
            if (checkRes.IsSuccessStatusCode)
            {
                var checkJson = await checkRes.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(checkJson);
                if (!doc.RootElement.GetProperty("trashed").GetBoolean())
                {
                    return settings.GoogleRootFolderId;
                }
            }
        }

        // Find 'sav' folder in root
        string q = "name = 'sav' and 'root' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
        var res = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=files(id)");
        if (res.IsSuccessStatusCode)
        {
            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var files = doc.RootElement.GetProperty("files");
            if (files.GetArrayLength() > 0)
            {
                var id = files[0].GetProperty("id").GetString()!;
                settings.GoogleRootFolderId = id;
                _configService.SaveSettings(settings);
                return id;
            }
        }

        // Create 'sav' folder
        var createPayload = new
        {
            name = "sav",
            mimeType = "application/vnd.google-apps.folder",
            parents = new[] { "root" }
        };
        var createJson = JsonSerializer.Serialize(createPayload);
        using var createContent = new StringContent(createJson, Encoding.UTF8, "application/json");
        var createRes = await _httpClient.PostAsync("https://www.googleapis.com/drive/v3/files?fields=id", createContent);
        createRes.EnsureSuccessStatusCode();

        var resJson = await createRes.Content.ReadAsStringAsync();
        using var resDoc = JsonDocument.Parse(resJson);
        var newId = resDoc.RootElement.GetProperty("id").GetString()!;

        settings.GoogleRootFolderId = newId;
        _configService.SaveSettings(settings);
        return newId;
    }

    private async Task<string> GetOrCreateGameFolderIdAsync(string gameId)
    {
        if (_folderCache.TryGetValue(gameId, out var cachedId))
            return cachedId;

        var rootFolderId = await GetOrCreateRootFolderIdAsync();
        string safeName = EscapeQuery(gameId);
        string q = $"name = '{safeName}' and '{rootFolderId}' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false";

        var res = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=files(id)");
        if (res.IsSuccessStatusCode)
        {
            var json = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var files = doc.RootElement.GetProperty("files");
            if (files.GetArrayLength() > 0)
            {
                var id = files[0].GetProperty("id").GetString()!;
                _folderCache[gameId] = id;
                return id;
            }
        }

        var createPayload = new
        {
            name = gameId,
            mimeType = "application/vnd.google-apps.folder",
            parents = new[] { rootFolderId }
        };
        var createJson = JsonSerializer.Serialize(createPayload);
        using var createContent = new StringContent(createJson, Encoding.UTF8, "application/json");
        var createRes = await _httpClient.PostAsync("https://www.googleapis.com/drive/v3/files?fields=id", createContent);
        createRes.EnsureSuccessStatusCode();

        var resJson = await createRes.Content.ReadAsStringAsync();
        using var resDoc = JsonDocument.Parse(resJson);
        var newId = resDoc.RootElement.GetProperty("id").GetString()!;

        _folderCache[gameId] = newId;
        return newId;
    }

    private async Task<string?> FindFileIdInFolderAsync(string folderId, string fileName, bool throwOnError = false)
    {
        string safeName = EscapeQuery(fileName);
        string q = $"name = '{safeName}' and '{folderId}' in parents and trashed = false";
        using var res = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=files(id)");
        if (!res.IsSuccessStatusCode)
        {
            if (throwOnError)
                throw new HttpRequestException($"Failed to look up '{fileName}' on Google Drive ({(int)res.StatusCode} {res.StatusCode}).");
            return null;
        }

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var files = doc.RootElement.GetProperty("files");
        if (files.GetArrayLength() > 0)
        {
            return files[0].GetProperty("id").GetString();
        }
        return null;
    }

    private async Task<(bool success, string? fileId, string? error)> UploadOrUpdateFileAsync(
        string folderId,
        string fileName,
        byte[] content,
        string mimeType = "application/octet-stream")
    {
        string? existingId;
        try
        {
            // A failed lookup must not be mistaken for "file missing", or we would create a duplicate.
            existingId = await FindFileIdInFolderAsync(folderId, fileName, throwOnError: true);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }

        if (!string.IsNullOrEmpty(existingId))
        {
            // Update existing file content
            using var request = new HttpRequestMessage(HttpMethod.Patch, $"https://www.googleapis.com/upload/drive/v3/files/{existingId}?uploadType=media")
            {
                Content = new ByteArrayContent(content)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(mimeType);

            var res = await _httpClient.SendAsync(request);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync();
                return (false, null, $"Failed to update {fileName}: {err}");
            }
            return (true, existingId, null);
        }
        else
        {
            // Upload new file using Multipart
            var boundary = "sav_boundary_" + Guid.NewGuid().ToString("N");
            var multipart = new MultipartContent("related", boundary);

            var metaPayload = new
            {
                name = fileName,
                parents = new[] { folderId }
            };
            var metaContent = new StringContent(JsonSerializer.Serialize(metaPayload), Encoding.UTF8, "application/json");
            multipart.Add(metaContent);

            var mediaContent = new ByteArrayContent(content);
            mediaContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
            multipart.Add(mediaContent);

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id")
            {
                Content = multipart
            };

            var res = await _httpClient.SendAsync(request);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync();
                return (false, null, $"Failed to upload {fileName}: {err}");
            }

            var resJson = await res.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(resJson);
            var newId = doc.RootElement.GetProperty("id").GetString();
            return (true, newId, null);
        }
    }

    public Task<List<string>> ListRemoteGameIdsAsync() => ListRemoteGameIdsCoreAsync(throwOnError: false);

    private async Task<List<string>> ListRemoteGameIdsCoreAsync(bool throwOnError)
    {
        await EnsureAuthenticatedAsync();
        var rootId = await GetOrCreateRootFolderIdAsync();

        string q = $"'{rootId}' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
        using var res = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=files(id,name)&pageSize=1000");
        if (!res.IsSuccessStatusCode)
        {
            if (throwOnError)
                throw new HttpRequestException($"Failed to list game folders on Google Drive ({(int)res.StatusCode} {res.StatusCode}).");
            return new List<string>();
        }

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var files = doc.RootElement.GetProperty("files");
        var list = new List<string>();

        foreach (var f in files.EnumerateArray())
        {
            var name = f.GetProperty("name").GetString();
            if (!string.IsNullOrEmpty(name))
            {
                list.Add(name);
                var id = f.GetProperty("id").GetString();
                if (!string.IsNullOrEmpty(id))
                {
                    _folderCache[name] = id;
                }
            }
        }
        return list;
    }

    public Task<RemoteGameMeta?> GetRemoteMetaAsync(string gameId) => GetRemoteMetaCoreAsync(gameId, throwOnError: false);

    private async Task<RemoteGameMeta?> GetRemoteMetaCoreAsync(string gameId, bool throwOnError)
    {
        await EnsureAuthenticatedAsync();
        var gameFolderId = await GetOrCreateGameFolderIdAsync(gameId);
        var metaFileId = await FindFileIdInFolderAsync(gameFolderId, "meta.json", throwOnError);
        if (string.IsNullOrEmpty(metaFileId)) return null;

        using var res = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files/{metaFileId}?alt=media");
        if (!res.IsSuccessStatusCode)
        {
            if (throwOnError)
                throw new HttpRequestException($"Failed to download meta.json for '{gameId}' ({(int)res.StatusCode} {res.StatusCode}).");
            return null;
        }

        var json = await res.Content.ReadAsStringAsync();
        var meta = JsonSerializer.Deserialize<RemoteGameMeta>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (meta != null && string.IsNullOrEmpty(meta.GameId))
        {
            meta.GameId = gameId;
        }
        return meta;
    }

    public async Task<Dictionary<string, RemoteGameMeta>> GetAllRemoteMetasAsync(bool forceRefresh = false)
    {
        if (_cachedRemoteMetas != null && !forceRefresh)
            return _cachedRemoteMetas;

        if (!IsConfigured) return new Dictionary<string, RemoteGameMeta>(StringComparer.OrdinalIgnoreCase);

        var ok = await EnsureAuthenticatedAsync();
        if (!ok) return new Dictionary<string, RemoteGameMeta>(StringComparer.OrdinalIgnoreCase);

        try
        {
            return await LoadRemoteMetasStrictAsync(forceRefresh: true);
        }
        catch
        {
            // Read-only callers (ls, lsc, pull) can live with an empty list on network errors.
            // The failed result is deliberately NOT cached so writers never reuse it.
            return new Dictionary<string, RemoteGameMeta>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Loads the full remote catalog and throws if any part of it could not be read.
    /// Must be used before rewriting catalog.json, otherwise a transient error would
    /// produce a partial catalog that drops every game not in the current batch.
    /// </summary>
    private async Task<Dictionary<string, RemoteGameMeta>> LoadRemoteMetasStrictAsync(bool forceRefresh = false)
    {
        if (_cachedRemoteMetas != null && !forceRefresh)
            return _cachedRemoteMetas;

        var result = new Dictionary<string, RemoteGameMeta>(StringComparer.OrdinalIgnoreCase);
        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var rootId = await GetOrCreateRootFolderIdAsync();
        var catalogId = await FindFileIdInFolderAsync(rootId, "catalog.json", throwOnError: true);

        if (!string.IsNullOrEmpty(catalogId))
        {
            using var res = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files/{catalogId}?alt=media");
            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException($"Failed to download catalog.json from Google Drive ({(int)res.StatusCode} {res.StatusCode}).");

            var json = await res.Content.ReadAsStringAsync();
            var catalog = JsonSerializer.Deserialize<Dictionary<string, RemoteGameMeta>>(json, jsonOptions)
                ?? throw new InvalidDataException("catalog.json on Google Drive is empty or invalid.");

            foreach (var kvp in catalog)
            {
                kvp.Value.GameId = kvp.Key;
                result[kvp.Key] = kvp.Value;
            }
        }
        else
        {
            // Fallback if catalog.json does not exist: scan each game folder
            var gameIds = await ListRemoteGameIdsCoreAsync(throwOnError: true);
            var metas = await Task.WhenAll(gameIds.Select(async id => (id, meta: await GetRemoteMetaCoreAsync(id, throwOnError: true))));
            foreach (var (id, meta) in metas)
            {
                if (meta != null) result[id] = meta;
            }
        }

        _cachedRemoteMetas = result;
        return result;
    }

    public void InvalidateCatalogCache()
    {
        _cachedRemoteMetas = null;
    }

    public async Task<(bool success, string? error)> UploadGameSaveAsync(string gameId, string zipPath, string metaPath, string mappingPath)
    {
        var payload = new GameBackupPayload
        {
            GameId = gameId,
            GameName = gameId,
            ZipPath = zipPath,
            MetaPath = metaPath,
            MappingPath = mappingPath
        };
        return await UploadGameSavesBatchAsync(new List<GameBackupPayload> { payload });
    }

    public async Task<(bool success, string? error)> UploadGameSavesBatchAsync(
        List<GameBackupPayload> payloads,
        Action<string>? progressCallback = null)
    {
        if (!IsConfigured)
            return (false, "Google Drive is not configured. Please run 'sav config'.");

        if (payloads == null || payloads.Count == 0)
            return (true, null);

        var ok = await EnsureAuthenticatedAsync();
        if (!ok) return (false, "Google Drive authentication error. Please run 'sav login'.");

        try
        {
            progressCallback?.Invoke("Preparing storage folder on Google Drive...");
            var rootFolderId = await GetOrCreateRootFolderIdAsync();
            // Strict load: abort the push rather than rewrite catalog.json from a partial view.
            var allMetas = new Dictionary<string, RemoteGameMeta>(await LoadRemoteMetasStrictAsync(), StringComparer.OrdinalIgnoreCase);

            // Resolve folders sequentially so two games never race to create the same folder.
            var folderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in payloads)
            {
                folderIds[p.GameId] = await GetOrCreateGameFolderIdAsync(p.GameId);
            }

            int total = payloads.Count;
            int done = 0;
            var failures = new ConcurrentBag<string>();
            var uploadedMetas = new ConcurrentDictionary<string, RemoteGameMeta>(StringComparer.OrdinalIgnoreCase);
            using var gate = new SemaphoreSlim(MaxParallelGameUploads);
            progressCallback?.Invoke($"Uploading {total} game(s) to Google Drive (0/{total})...");

            var tasks = payloads.Select(async p =>
            {
                await gate.WaitAsync();
                try
                {
                    var meta = await UploadSingleGameAsync(folderIds[p.GameId], p);
                    uploadedMetas[p.GameId] = meta;
                }
                catch (Exception ex)
                {
                    failures.Add($"{p.GameName}: {ex.Message}");
                }
                finally
                {
                    gate.Release();
                    var count = Interlocked.Increment(ref done);
                    progressCallback?.Invoke($"Uploading {total} game(s) to Google Drive ({count}/{total})...");
                }
            });
            await Task.WhenAll(tasks);

            // Catalog only reflects games whose zip AND meta.json both made it to Drive.
            if (!uploadedMetas.IsEmpty)
            {
                foreach (var (id, meta) in uploadedMetas)
                {
                    allMetas[id] = meta;
                }

                progressCallback?.Invoke("Updating catalog index on Google Drive...");
                var catalogJson = JsonSerializer.Serialize(allMetas, new JsonSerializerOptions { WriteIndented = true });
                var catalogBytes = Encoding.UTF8.GetBytes(catalogJson);
                var catalogUpload = await UploadOrUpdateFileAsync(rootFolderId, "catalog.json", catalogBytes, "application/json");
                if (!catalogUpload.success)
                {
                    return (false, catalogUpload.error ?? "Failed to update the Google Drive backup catalog.");
                }
                _cachedRemoteMetas = allMetas;
            }

            if (!failures.IsEmpty)
            {
                return (false, "Upload failed for " + string.Join("; ", failures));
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private const int MaxParallelGameUploads = 3;

    /// <summary>
    /// Uploads one game's files in a fixed order: latest.zip, mapping.yaml, then meta.json last.
    /// meta.json is the "commit marker": if the zip fails, Drive keeps the old meta that still
    /// describes the old zip, so the remote state never claims a backup it doesn't have.
    /// </summary>
    private async Task<RemoteGameMeta> UploadSingleGameAsync(string gameFolderId, GameBackupPayload p)
    {
        if (!File.Exists(p.ZipPath))
            throw new FileNotFoundException("Backup archive not found.", p.ZipPath);
        if (string.IsNullOrEmpty(p.MetaPath) || !File.Exists(p.MetaPath))
            throw new FileNotFoundException("Backup meta.json not found.", p.MetaPath);

        var metaBytes = await File.ReadAllBytesAsync(p.MetaPath);
        var meta = JsonSerializer.Deserialize<RemoteGameMeta>(metaBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Backup meta.json is invalid.");
        meta.GameId = p.GameId;

        await UploadFileResumableAsync(gameFolderId, "latest.zip", p.ZipPath, "application/zip");

        if (!string.IsNullOrEmpty(p.MappingPath) && File.Exists(p.MappingPath))
        {
            await UploadOrThrowAsync(gameFolderId, "mapping.yaml", await File.ReadAllBytesAsync(p.MappingPath), "application/x-yaml");
        }

        await UploadOrThrowAsync(gameFolderId, "meta.json", metaBytes, "application/json");
        return meta;
    }

    private const int MaxResumeAttempts = 4;

    /// <summary>
    /// Uploads a file with Drive's resumable protocol, streaming from disk instead of loading it
    /// into memory. If the connection drops, asks Drive how many bytes it already has and continues
    /// from there (up to <see cref="MaxResumeAttempts"/> times). Replaces the file if it already exists.
    /// </summary>
    private async Task UploadFileResumableAsync(string folderId, string fileName, string filePath, string mime)
    {
        var totalBytes = new FileInfo(filePath).Length;
        var existingId = await FindFileIdInFolderAsync(folderId, fileName, throwOnError: true);

        // 1. Start a session; Drive returns the session URI in the Location header.
        HttpRequestMessage startRequest;
        if (!string.IsNullOrEmpty(existingId))
        {
            startRequest = new HttpRequestMessage(HttpMethod.Patch,
                $"https://www.googleapis.com/upload/drive/v3/files/{existingId}?uploadType=resumable&fields=id")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
        else
        {
            var metadata = JsonSerializer.Serialize(new { name = fileName, parents = new[] { folderId } });
            startRequest = new HttpRequestMessage(HttpMethod.Post,
                "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id")
            {
                Content = new StringContent(metadata, Encoding.UTF8, "application/json")
            };
        }
        startRequest.Headers.Add("X-Upload-Content-Type", mime);
        startRequest.Headers.Add("X-Upload-Content-Length", totalBytes.ToString());

        Uri sessionUri;
        using (startRequest)
        using (var startRes = await _httpClient.SendAsync(startRequest))
        {
            if (!startRes.IsSuccessStatusCode || startRes.Headers.Location == null)
            {
                var err = await startRes.Content.ReadAsStringAsync();
                throw new IOException($"Failed to start upload of {fileName}: {(int)startRes.StatusCode} {err}");
            }
            sessionUri = startRes.Headers.Location;
        }

        // 2. Send the bytes, resuming from Drive's last confirmed offset after a failure.
        long offset = 0;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                fs.Seek(offset, SeekOrigin.Begin);

                using var put = new HttpRequestMessage(HttpMethod.Put, sessionUri) { Content = new StreamContent(fs, 81920) };
                put.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
                put.Content.Headers.ContentLength = totalBytes - offset;
                if (totalBytes > 0)
                {
                    put.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, totalBytes - 1, totalBytes);
                }

                using var res = await _httpClient.SendAsync(put);
                if (res.IsSuccessStatusCode) return;

                var status = (int)res.StatusCode;
                var body = await res.Content.ReadAsStringAsync();
                // 4xx other than 408/429 means the request itself is wrong; retrying won't help.
                if (status is >= 400 and < 500 and not 408 and not 429)
                    throw new IOException($"Failed to upload {fileName}: {status} {body}");
                if (attempt >= MaxResumeAttempts)
                    throw new IOException($"Failed to upload {fileName} after {attempt} attempts: {status} {body}");
            }
            catch (HttpRequestException) when (attempt < MaxResumeAttempts) { }
            catch (TaskCanceledException) when (attempt < MaxResumeAttempts) { }

            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
            var resumeFrom = await QueryUploadOffsetAsync(sessionUri, totalBytes);
            if (resumeFrom == null) return; // Drive reports the upload as already complete
            offset = resumeFrom.Value;
        }
    }

    /// <summary>Returns the next byte Drive expects, or null if the upload already completed.</summary>
    private async Task<long?> QueryUploadOffsetAsync(Uri sessionUri, long totalBytes)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, sessionUri) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        req.Content.Headers.ContentRange = new ContentRangeHeaderValue(totalBytes);
        using var res = await _httpClient.SendAsync(req);

        if (res.IsSuccessStatusCode) return null;
        if ((int)res.StatusCode == 308)
        {
            // "Range: bytes=0-N" means bytes 0..N were received; no header means nothing was.
            if (res.Headers.TryGetValues("Range", out var values))
            {
                var range = values.FirstOrDefault();
                var dash = range?.LastIndexOf('-') ?? -1;
                if (dash >= 0 && long.TryParse(range![(dash + 1)..], out var lastByte))
                    return lastByte + 1;
            }
            return 0;
        }

        throw new IOException($"Upload session is no longer valid ({(int)res.StatusCode}). Please push again.");
    }

    private async Task UploadOrThrowAsync(string folderId, string fileName, byte[] content, string mime)
    {
        var res = await UploadOrUpdateFileAsync(folderId, fileName, content, mime);
        if (!res.success)
            throw new IOException(res.error ?? $"Failed to upload {fileName}");
    }

    public async Task<(bool success, string? error)> DownloadGameSaveAsync(string gameId, string targetDir)
    {
        if (!IsConfigured)
            return (false, "Google Drive is not configured. Please run 'sav config'.");

        var ok = await EnsureAuthenticatedAsync();
        if (!ok) return (false, "Google Drive authentication error.");

        string? tempPath = null;
        try
        {
            var gameFolderId = await GetOrCreateGameFolderIdAsync(gameId);
            var zipFileId = await FindFileIdInFolderAsync(gameFolderId, "latest.zip", throwOnError: true);
            if (string.IsNullOrEmpty(zipFileId))
            {
                return (false, $"Save file 'latest.zip' for game '{gameId}' not found on Google Drive.");
            }

            // Checksums computed by Drive, used to verify the download end-to-end.
            string? expectedSha256 = null, expectedMd5 = null;
            using (var infoRes = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files/{zipFileId}?fields=sha256Checksum,md5Checksum"))
            {
                if (infoRes.IsSuccessStatusCode)
                {
                    using var infoDoc = JsonDocument.Parse(await infoRes.Content.ReadAsStringAsync());
                    if (infoDoc.RootElement.TryGetProperty("sha256Checksum", out var s)) expectedSha256 = s.GetString();
                    if (infoDoc.RootElement.TryGetProperty("md5Checksum", out var m)) expectedMd5 = m.GetString();
                }
            }

            Directory.CreateDirectory(targetDir);
            var zipPath = Path.Combine(targetDir, "latest.zip");
            tempPath = Path.Combine(targetDir, $".download-{Guid.NewGuid():N}.tmp");

            using var res = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files/{zipFileId}?alt=media", HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode)
            {
                return (false, $"Failed to download file from Google Drive: {res.StatusCode}");
            }

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            await using (var source = await res.Content.ReadAsStreamAsync())
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    md5.AppendData(buffer, 0, read);
                    await fs.WriteAsync(buffer.AsMemory(0, read));
                }
            }

            var actualSha256 = Convert.ToHexString(sha.GetHashAndReset());
            var actualMd5 = Convert.ToHexString(md5.GetHashAndReset());
            if ((!string.IsNullOrEmpty(expectedSha256) && !actualSha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(expectedMd5) && !actualMd5.Equals(expectedMd5, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, "Downloaded archive is corrupted (checksum mismatch). Please try again.");
            }

            // Only replace the cached archive once the new one is complete and verified.
            File.Move(tempPath, zipPath, overwrite: true);
            tempPath = null;
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (tempPath != null)
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    public async Task<(bool success, string? error)> UploadCustomManifestAsync(string localPath)
    {
        if (!IsConfigured || !File.Exists(localPath)) return (false, "File does not exist or Google Drive is not configured");
        var ok = await EnsureAuthenticatedAsync();
        if (!ok) return (false, "Google Drive authentication error");

        var rootId = await GetOrCreateRootFolderIdAsync();
        var bytes = await File.ReadAllBytesAsync(localPath);
        var res = await UploadOrUpdateFileAsync(rootId, "custom-games.yaml", bytes, "application/x-yaml");
        return (res.success, res.error);
    }

    public async Task<(bool success, string? error)> DownloadCustomManifestAsync(string localPath)
    {
        if (!IsConfigured) return (false, "Google Drive not configured");
        var ok = await EnsureAuthenticatedAsync();
        if (!ok) return (false, "Google Drive authentication error");

        var rootId = await GetOrCreateRootFolderIdAsync();
        var fileId = await FindFileIdInFolderAsync(rootId, "custom-games.yaml");
        if (string.IsNullOrEmpty(fileId)) return (false, "File custom-games.yaml not found on Google Drive");

        var res = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media");
        if (!res.IsSuccessStatusCode) return (false, "Failed to download file");

        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        await File.WriteAllBytesAsync(localPath, bytes);
        return (true, null);
    }

    public async Task<(bool success, string? error)> DeleteGameSaveAsync(string gameId)
    {
        if (!IsConfigured) return (false, "Google Drive not configured");
        var ok = await EnsureAuthenticatedAsync();
        if (!ok) return (false, "Google Drive authentication error");

        try
        {
            var rootId = await GetOrCreateRootFolderIdAsync();
            string safeName = EscapeQuery(gameId);
            string q = $"name = '{safeName}' and '{rootId}' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
            var res = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=files(id)");
            if (res.IsSuccessStatusCode)
            {
                var json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var files = doc.RootElement.GetProperty("files");
                if (files.GetArrayLength() > 0)
                {
                    var id = files[0].GetProperty("id").GetString();
                    await _httpClient.DeleteAsync($"https://www.googleapis.com/drive/v3/files/{id}");
                }
            }

            var allMetas = new Dictionary<string, RemoteGameMeta>(await LoadRemoteMetasStrictAsync(forceRefresh: true), StringComparer.OrdinalIgnoreCase);
            if (allMetas.Remove(gameId))
            {
                var catalogJson = JsonSerializer.Serialize(allMetas, new JsonSerializerOptions { WriteIndented = true });
                var catalogUpload = await UploadOrUpdateFileAsync(rootId, "catalog.json", Encoding.UTF8.GetBytes(catalogJson), "application/json");
                if (!catalogUpload.success)
                    return (false, catalogUpload.error ?? "Failed to update the Google Drive backup catalog.");
                _cachedRemoteMetas = allMetas;
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
