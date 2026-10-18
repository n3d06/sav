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

    private async Task<string?> FindFileIdInFolderAsync(string folderId, string fileName)
    {
        string safeName = EscapeQuery(fileName);
        string q = $"name = '{safeName}' and '{folderId}' in parents and trashed = false";
        var res = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=files(id)");
        if (!res.IsSuccessStatusCode) return null;

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
        var existingId = await FindFileIdInFolderAsync(folderId, fileName);

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

    public async Task<List<string>> ListRemoteGameIdsAsync()
    {
        await EnsureAuthenticatedAsync();
        var rootId = await GetOrCreateRootFolderIdAsync();

        string q = $"'{rootId}' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
        var res = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(q)}&fields=files(id,name)&pageSize=1000");
        if (!res.IsSuccessStatusCode) return new List<string>();

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

    public async Task<RemoteGameMeta?> GetRemoteMetaAsync(string gameId)
    {
        await EnsureAuthenticatedAsync();
        var gameFolderId = await GetOrCreateGameFolderIdAsync(gameId);
        var metaFileId = await FindFileIdInFolderAsync(gameFolderId, "meta.json");
        if (string.IsNullOrEmpty(metaFileId)) return null;

        var res = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files/{metaFileId}?alt=media");
        if (!res.IsSuccessStatusCode) return null;

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

        var result = new Dictionary<string, RemoteGameMeta>(StringComparer.OrdinalIgnoreCase);
        if (!IsConfigured) return result;

        var ok = await EnsureAuthenticatedAsync();
        if (!ok) return result;

        try
        {
            var rootId = await GetOrCreateRootFolderIdAsync();
            var catalogId = await FindFileIdInFolderAsync(rootId, "catalog.json");

            if (!string.IsNullOrEmpty(catalogId))
            {
                var res = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files/{catalogId}?alt=media");
                if (res.IsSuccessStatusCode)
                {
                    var json = await res.Content.ReadAsStringAsync();
                    var catalog = JsonSerializer.Deserialize<Dictionary<string, RemoteGameMeta>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (catalog != null)
                    {
                        foreach (var kvp in catalog)
                        {
                            kvp.Value.GameId = kvp.Key;
                            result[kvp.Key] = kvp.Value;
                        }
                        _cachedRemoteMetas = result;
                        return result;
                    }
                }
            }

            // Fallback if catalog.json does not exist: Scan each game folder
            var gameIds = await ListRemoteGameIdsAsync();
            if (gameIds.Count > 0)
            {
                var tasks = gameIds.Select(async id =>
                {
                    var meta = await GetRemoteMetaAsync(id);
                    return (id, meta);
                });
                var metas = await Task.WhenAll(tasks);
                foreach (var m in metas)
                {
                    if (m.meta != null)
                    {
                        result[m.id] = m.meta;
                    }
                }
            }
        }
        catch { }

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
            var allMetas = await GetAllRemoteMetasAsync();

            var uploadItems = new List<(string folderId, string fileName, byte[] content, string mime)>();

            foreach (var p in payloads)
            {
                var gameFolderId = await GetOrCreateGameFolderIdAsync(p.GameId);

                if (File.Exists(p.ZipPath))
                {
                    var zipBytes = await File.ReadAllBytesAsync(p.ZipPath);
                    uploadItems.Add((gameFolderId, "latest.zip", zipBytes, "application/zip"));
                }

                if (!string.IsNullOrEmpty(p.MetaPath) && File.Exists(p.MetaPath))
                {
                    var metaBytes = await File.ReadAllBytesAsync(p.MetaPath);
                    uploadItems.Add((gameFolderId, "meta.json", metaBytes, "application/json"));

                    var metaText = Encoding.UTF8.GetString(metaBytes);
                    var metaObj = JsonSerializer.Deserialize<RemoteGameMeta>(metaText, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (metaObj != null)
                    {
                        allMetas[p.GameId] = metaObj;
                    }
                }

                if (!string.IsNullOrEmpty(p.MappingPath) && File.Exists(p.MappingPath))
                {
                    var mapBytes = await File.ReadAllBytesAsync(p.MappingPath);
                    uploadItems.Add((gameFolderId, "mapping.yaml", mapBytes, "application/x-yaml"));
                }
            }

            int totalFiles = uploadItems.Count;
            int uploadedFiles = 0;
            progressCallback?.Invoke($"Uploading {totalFiles} file(s) in parallel to Google Drive (0/{totalFiles})...");

            var tasks = uploadItems.Select(async item =>
            {
                var res = await UploadOrUpdateFileAsync(item.folderId, item.fileName, item.content, item.mime);
                if (!res.success)
                {
                    throw new Exception(res.error ?? $"Failed to upload {item.fileName}");
                }
                var count = Interlocked.Increment(ref uploadedFiles);
                progressCallback?.Invoke($"Uploading {totalFiles} file(s) in parallel to Google Drive ({count}/{totalFiles})...");
            });

            await Task.WhenAll(tasks);

            // Update catalog.json on root 'sav'
            progressCallback?.Invoke("Updating catalog index on Google Drive...");
            var catalogJson = JsonSerializer.Serialize(allMetas, new JsonSerializerOptions { WriteIndented = true });
            var catalogBytes = Encoding.UTF8.GetBytes(catalogJson);
            var catalogUpload = await UploadOrUpdateFileAsync(rootFolderId, "catalog.json", catalogBytes, "application/json");
            if (!catalogUpload.success)
            {
                return (false, catalogUpload.error ?? "Failed to update the Google Drive backup catalog.");
            }

            _cachedRemoteMetas = allMetas;
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool success, string? error)> DownloadGameSaveAsync(string gameId, string targetDir)
    {
        if (!IsConfigured)
            return (false, "Google Drive is not configured. Please run 'sav config'.");

        var ok = await EnsureAuthenticatedAsync();
        if (!ok) return (false, "Google Drive authentication error.");

        try
        {
            var gameFolderId = await GetOrCreateGameFolderIdAsync(gameId);
            var zipFileId = await FindFileIdInFolderAsync(gameFolderId, "latest.zip");
            if (string.IsNullOrEmpty(zipFileId))
            {
                return (false, $"Save file 'latest.zip' for game '{gameId}' not found on Google Drive.");
            }

            Directory.CreateDirectory(targetDir);
            var zipPath = Path.Combine(targetDir, "latest.zip");

            var res = await _httpClient.GetAsync($"https://www.googleapis.com/drive/v3/files/{zipFileId}?alt=media", HttpCompletionOption.ResponseHeadersRead);
            if (!res.IsSuccessStatusCode)
            {
                return (false, $"Failed to download file from Google Drive: {res.StatusCode}");
            }

            await using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await res.Content.CopyToAsync(fs);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
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

            var allMetas = await GetAllRemoteMetasAsync();
            if (allMetas.Remove(gameId))
            {
                var catalogJson = JsonSerializer.Serialize(allMetas, new JsonSerializerOptions { WriteIndented = true });
                await UploadOrUpdateFileAsync(rootId, "catalog.json", Encoding.UTF8.GetBytes(catalogJson), "application/json");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
