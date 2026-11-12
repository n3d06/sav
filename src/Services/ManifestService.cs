using System.IO;
using System.Net.Http;
using System.Text.Json;
using YamlDotNet.Serialization;
using ludusavo.Models;

namespace ludusavo.Services;

public class ManifestService : IManifestService
{
    private static readonly HttpClient ManifestHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders = { { "User-Agent", "ludusavo-CLI" } }
    };

    private readonly IConfigService _configService;
    private Dictionary<string, GameEntry> _gamesById = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, GameEntry> _gamesBySteamId = new();
    private List<GameEntry> _gamesList = new();

    public bool IsLoaded { get; private set; }
    public int GameCount => _gamesList.Count;

    public ManifestService(IConfigService configService)
    {
        _configService = configService;
    }

    public async Task<bool> LoadManifestAsync(bool forceDownload = false)
    {
        var cachePath = _configService.ManifestCachePath;

        // 1. If not forcing download, check if present in dev mode local data/cache
        if (!forceDownload && !File.Exists(cachePath))
        {
            var fallback = Path.Combine(_configService.RootDir, "data", "cache", "manifest_processed.json");
            if (File.Exists(fallback))
            {
                try
                {
                    var dir = Path.GetDirectoryName(cachePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.Copy(fallback, cachePath, overwrite: true);
                }
                catch { }
            }
        }

        // 2. If forceDownload or still not present, automatically download YAML from Ludusavi and process
        if (forceDownload || !File.Exists(cachePath) || new FileInfo(cachePath).Length < 1000)
        {
            var downloaded = await DownloadAndProcessManifestAsync(cachePath);
            if (!downloaded && !File.Exists(cachePath))
            {
                Console.WriteLine($"[ManifestService] Failed to download manifest database from Ludusavi to {cachePath}");
                return false;
            }
        }

        if (!File.Exists(cachePath))
        {
            Console.WriteLine($"[ManifestService] Manifest cache not found at {cachePath}");
            return false;
        }

        try
        {
            await using var stream = File.OpenRead(cachePath);
            var loaded = await JsonSerializer.DeserializeAsync<List<GameEntry>>(stream);

            if (loaded != null && loaded.Count > 0)
            {
                _gamesList = loaded;
                _gamesById = new Dictionary<string, GameEntry>(loaded.Count, StringComparer.OrdinalIgnoreCase);
                _gamesBySteamId = new Dictionary<int, GameEntry>(loaded.Count);

                foreach (var g in _gamesList)
                {
                    _gamesById[g.Id] = g;
                    var sid = g.GetSteamAppId();
                    if (sid.HasValue)
                    {
                        _gamesBySteamId[sid.Value] = g;
                    }
                }

                IsLoaded = true;
                
                // Load custom manifest
                await LoadCustomManifestAsync();

                return true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ManifestService] Error loading manifest: {ex.Message}");
        }

        return false;
    }

    public IReadOnlyList<GameEntry> GetAllGames() => _gamesList;

    public GameEntry? GetGameById(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        _gamesById.TryGetValue(id, out var game);
        return game;
    }

    public GameEntry? GetGameBySteamId(int steamId)
    {
        _gamesBySteamId.TryGetValue(steamId, out var game);
        return game;
    }

    public IEnumerable<GameEntry> SearchGames(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _gamesList;

        var q = query.Trim();
        return _gamesList.Where(g =>
            g.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            g.Id.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private async Task LoadCustomManifestAsync()
    {
        var customPath = Path.Combine(_configService.CacheDir, "custom_manifest.json");
        if (!File.Exists(customPath)) return;

        try
        {
            await using var stream = File.OpenRead(customPath);
            var loaded = await JsonSerializer.DeserializeAsync<List<GameEntry>>(stream);
            if (loaded != null)
            {
                foreach (var g in loaded)
                {
                    // Update or Add
                    var existing = _gamesList.FirstOrDefault(x => x.Id == g.Id);
                    if (existing != null)
                    {
                        _gamesList.Remove(existing);
                    }
                    _gamesList.Add(g);
                    
                    _gamesById[g.Id] = g;
                    var sid = g.GetSteamAppId();
                    if (sid.HasValue)
                    {
                        _gamesBySteamId[sid.Value] = g;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ManifestService] Error loading custom manifest: {ex.Message}");
        }
    }

    public async Task AddCustomGameAsync(GameEntry game)
    {
        var customPath = Path.Combine(_configService.CacheDir, "custom_manifest.json");
        List<GameEntry> customGames = new();

        if (File.Exists(customPath))
        {
            try
            {
                await using var stream = File.OpenRead(customPath);
                var loaded = await JsonSerializer.DeserializeAsync<List<GameEntry>>(stream);
                if (loaded != null)
                {
                    customGames = loaded;
                }
            }
            catch { }
        }

        var existingIdx = customGames.FindIndex(g => g.Id == game.Id);
        if (existingIdx >= 0)
        {
            customGames[existingIdx] = game;
        }
        else
        {
            customGames.Add(game);
        }

        // Save back
        var options = new JsonSerializerOptions 
        { 
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        var json = JsonSerializer.Serialize(customGames, options);
        await File.WriteAllTextAsync(customPath, json);

        // Update in-memory state
        var existingListIdx = _gamesList.FindIndex(g => g.Id == game.Id);
        if (existingListIdx >= 0)
        {
            _gamesList.RemoveAt(existingListIdx);
        }
        _gamesList.Add(game);
        _gamesById[game.Id] = game;
    }

    public async Task RemoveCustomGameAsync(string gameId)
    {
        var customPath = Path.Combine(_configService.CacheDir, "custom_manifest.json");
        List<GameEntry> customGames = new();

        if (File.Exists(customPath))
        {
            try
            {
                await using var stream = File.OpenRead(customPath);
                var loaded = await JsonSerializer.DeserializeAsync<List<GameEntry>>(stream);
                if (loaded != null)
                {
                    customGames = loaded;
                }
            }
            catch { }
        }

        var existingIdx = customGames.FindIndex(g => g.Id == gameId);
        if (existingIdx >= 0)
        {
            customGames.RemoveAt(existingIdx);
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var json = JsonSerializer.Serialize(customGames, options);
            await File.WriteAllTextAsync(customPath, json);
        }

        // Update in-memory state
        var existingListIdx = _gamesList.FindIndex(g => g.Id == gameId);
        if (existingListIdx >= 0)
        {
            _gamesList.RemoveAt(existingListIdx);
        }
        _gamesById.Remove(gameId);
    }

    private static async Task<bool> DownloadAndProcessManifestAsync(string targetJsonPath)
    {
        var urls = new[]
        {
            "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml",
            "https://cdn.jsdelivr.net/gh/mtkennerly/ludusavi-manifest@master/data/manifest.yaml"
        };

        var dir = Path.GetDirectoryName(targetJsonPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempYamlPath = Path.Combine(dir ?? Path.GetTempPath(), $"manifest_{Guid.NewGuid():N}.yaml.tmp");
        var tempJsonPath = targetJsonPath + $".{Guid.NewGuid():N}.tmp";

        foreach (var url in urls)
        {
            try
            {
                using var response = await ManifestHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode) continue;

                await using (var remoteStream = await response.Content.ReadAsStreamAsync())
                await using (var fileStream = new FileStream(tempYamlPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await remoteStream.CopyToAsync(fileStream);
                }

                if (!File.Exists(tempYamlPath) || new FileInfo(tempYamlPath).Length < 1000)
                    continue;

                // Deserialize YAML into memory using YamlDotNet
                using (var yamlReader = File.OpenText(tempYamlPath))
                {
                    var deserializer = new DeserializerBuilder()
                        .IgnoreUnmatchedProperties()
                        .Build();

                    var rawDict = deserializer.Deserialize<Dictionary<string, LudusaviRawGame>>(yamlReader);
                    if (rawDict == null || rawDict.Count == 0)
                        continue;

                    var gameEntries = new List<GameEntry>(rawDict.Count);
                    foreach (var (gameName, rawGame) in rawDict)
                    {
                        if (rawGame == null) continue;

                        var savePaths = new List<SavePathEntry>();
                        if (rawGame.files != null)
                        {
                            foreach (var (filePath, fileEntry) in rawGame.files)
                            {
                                if (string.IsNullOrWhiteSpace(filePath)) continue;

                                var whenList = fileEntry?.when?.Select(w => new WhenCondition
                                {
                                    Os = w.os,
                                    Store = w.store
                                }).ToList();

                                savePaths.Add(new SavePathEntry
                                {
                                    Path = filePath,
                                    Tags = fileEntry?.tags,
                                    When = whenList
                                });
                            }
                        }

                        gameEntries.Add(new GameEntry
                        {
                            Id = gameName,
                            Name = gameName,
                            SteamId = rawGame.steam?.id,
                            SavePaths = savePaths
                        });
                    }

                    // Save processed list as JSON cache
                    await using (var jsonStream = new FileStream(tempJsonPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await JsonSerializer.SerializeAsync(jsonStream, gameEntries);
                    }

                    if (File.Exists(tempJsonPath) && new FileInfo(tempJsonPath).Length > 1000)
                    {
                        File.Move(tempJsonPath, targetJsonPath, overwrite: true);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ManifestService] Error downloading or processing manifest from {url}: {ex.Message}");
            }
            finally
            {
                if (File.Exists(tempYamlPath))
                {
                    try { File.Delete(tempYamlPath); } catch { }
                }
                if (File.Exists(tempJsonPath))
                {
                    try { File.Delete(tempJsonPath); } catch { }
                }
            }
        }

        return false;
    }

    private class LudusaviRawGame
    {
        public Dictionary<string, LudusaviRawFileEntry>? files { get; set; }
        public LudusaviRawSteam? steam { get; set; }
    }

    private class LudusaviRawSteam
    {
        public int? id { get; set; }
    }

    private class LudusaviRawFileEntry
    {
        public List<string>? tags { get; set; }
        public List<LudusaviRawWhen>? when { get; set; }
    }

    private class LudusaviRawWhen
    {
        public string? os { get; set; }
        public string? store { get; set; }
    }
}
