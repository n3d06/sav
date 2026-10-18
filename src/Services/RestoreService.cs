using System.IO;
using System.IO.Compression;
using System.Text.Json;
using YamlDotNet.Serialization;

namespace ludusavo.Services;

public class RestoreService : IRestoreService
{
    private readonly IConfigService _configService;

    public RestoreService(IConfigService configService)
    {
        _configService = configService;
    }

    public async Task<RestoreResult> RestoreGameAsync(string gameId, string? specificZipPath = null)
    {
        var gameCacheDir = Path.Combine(_configService.CacheDir, BackupService.SanitizeFolderName(gameId));
        var zipPath = specificZipPath ?? Path.Combine(gameCacheDir, "latest.zip");
        var mappingPath = Path.Combine(gameCacheDir, "mapping.yaml");

        if (!File.Exists(zipPath))
        {
            return new RestoreResult { Success = false, ErrorMessage = $"Backup archive file not found at {zipPath}" };
        }

        try
        {
            return await Task.Run(() =>
            {
                // Read drives mapping
                var driveMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(mappingPath))
                {
                    try
                    {
                        var yaml = File.ReadAllText(mappingPath);
                        var deserializer = new DeserializerBuilder().Build();
                        var root = deserializer.Deserialize<Dictionary<string, object>>(yaml);
                        if (root != null && root.TryGetValue("drives", out var drivesObj) && drivesObj is Dictionary<object, object> dDict)
                        {
                            foreach (var kv in dDict)
                            {
                                driveMap[kv.Key.ToString()!] = kv.Value.ToString()!;
                            }
                        }
                    }
                    catch { }
                }

                int count = 0;
                using var zip = ZipFile.OpenRead(zipPath);
                var portablePaths = ReadPortablePaths(zip);

                // Also check if mapping.yaml is embedded in the zip itself
                var embeddedMapping = zip.GetEntry("mapping.yaml");
                if (embeddedMapping != null && driveMap.Count == 0)
                {
                    try
                    {
                        using var s = embeddedMapping.Open();
                        using var reader = new StreamReader(s);
                        var yaml = reader.ReadToEnd();
                        var deserializer = new DeserializerBuilder().Build();
                        var root = deserializer.Deserialize<Dictionary<string, object>>(yaml);
                        if (root != null && root.TryGetValue("drives", out var drivesObj) && drivesObj is Dictionary<object, object> dDict)
                        {
                            foreach (var kv in dDict)
                            {
                                driveMap[kv.Key.ToString()!] = kv.Value.ToString()!;
                            }
                        }
                    }
                    catch { }
                }

                var filesToRestore = new List<(ZipArchiveEntry Entry, string Destination, DateTime? ModifiedUtc)>();
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // Directory entry
                    if (entry.FullName.Equals("mapping.yaml", StringComparison.OrdinalIgnoreCase) ||
                        entry.FullName.Equals("restore-paths.json", StringComparison.OrdinalIgnoreCase)) continue;

                    if (portablePaths.TryGetValue(entry.FullName, out var portable))
                    {
                        if (!TryResolvePortablePath(portable.PlaceholderPath, out var portableDestination))
                        {
                            throw new InvalidDataException($"Unsafe portable path in backup archive: {entry.FullName}");
                        }
                        filesToRestore.Add((entry, portableDestination, portable.ModifiedUtc));
                        continue;
                    }

                    // Form: drive-C/path/to/file.ext
                    var parts = entry.FullName.Split('/', 2);
                    if (parts.Length < 2) continue;

                    var driveKey = parts[0];
                    var archiveRelativePath = parts[1];
                    if (Path.IsPathRooted(archiveRelativePath) ||
                        archiveRelativePath.Contains(':') ||
                        archiveRelativePath.Split('/', '\\').Any(part => part is ".." or "." || part.Length == 0))
                    {
                        throw new InvalidDataException($"Unsafe path in backup archive: {entry.FullName}");
                    }
                    var relativePath = archiveRelativePath.Replace('/', Path.DirectorySeparatorChar);

                    string targetDrive;
                    if (driveMap.TryGetValue(driveKey, out var mappedDrive))
                    {
                        if (mappedDrive.Length < 2 || mappedDrive.Length > 3 ||
                            !char.IsAsciiLetter(mappedDrive[0]) || mappedDrive[1] != ':' ||
                            (mappedDrive.Length == 3 && mappedDrive[2] != '\\'))
                        {
                            throw new InvalidDataException($"Unsafe drive mapping for '{driveKey}'.");
                        }
                        targetDrive = mappedDrive.TrimEnd('\\', '/');
                    }
                    else if (driveKey.StartsWith("drive-", StringComparison.OrdinalIgnoreCase))
                    {
                        var letter = driveKey.Substring("drive-".Length);
                        if (letter.Length != 1 || !char.IsAsciiLetter(letter[0]))
                        {
                            throw new InvalidDataException($"Invalid drive entry in backup archive: {entry.FullName}");
                        }
                        targetDrive = $"{letter}:";
                    }
                    else
                    {
                        throw new InvalidDataException($"Invalid drive entry in backup archive: {entry.FullName}");
                    }

                    var destinationRoot = Path.GetFullPath(targetDrive + Path.DirectorySeparatorChar);
                    var destPath = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
                    var rootWithSeparator = Path.EndsInDirectorySeparator(destinationRoot)
                        ? destinationRoot
                        : destinationRoot + Path.DirectorySeparatorChar;
                    if (!destPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"Unsafe destination path in backup archive: {entry.FullName}");
                    }
                    filesToRestore.Add((entry, destPath, null));
                }

                foreach (var (entry, destPath, modifiedUtc) in filesToRestore)
                {
                    var destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    if (File.Exists(destPath))
                    {
                        var attrs = File.GetAttributes(destPath);
                        if ((attrs & FileAttributes.ReadOnly) != 0)
                        {
                            File.SetAttributes(destPath, attrs & ~FileAttributes.ReadOnly);
                        }
                    }

                    entry.ExtractToFile(destPath, overwrite: true);
                    if (modifiedUtc.HasValue)
                    {
                        File.SetLastWriteTimeUtc(destPath, modifiedUtc.Value);
                    }
                    count++;
                }

                return new RestoreResult { Success = true, RestoredFilesCount = count };
            });
        }
        catch (Exception ex)
        {
            return new RestoreResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private sealed class PortablePathInfo
    {
        public string ArchivePath { get; set; } = string.Empty;
        public string PlaceholderPath { get; set; } = string.Empty;
        public DateTime? ModifiedUtc { get; set; }
    }

    private static Dictionary<string, PortablePathInfo> ReadPortablePaths(ZipArchive zip)
    {
        var result = new Dictionary<string, PortablePathInfo>(StringComparer.OrdinalIgnoreCase);
        var entry = zip.GetEntry("restore-paths.json");
        if (entry == null) return result;

        try
        {
            using var reader = new StreamReader(entry.Open());
            var paths = JsonSerializer.Deserialize<List<PortablePathInfo>>(reader.ReadToEnd(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            foreach (var path in paths ?? Enumerable.Empty<PortablePathInfo>())
            {
                if (!string.IsNullOrWhiteSpace(path.ArchivePath) && !string.IsNullOrWhiteSpace(path.PlaceholderPath))
                {
                    result[path.ArchivePath] = path;
                }
            }
        }
        catch { }

        return result;
    }

    private static bool TryResolvePortablePath(string placeholderPath, out string destination)
    {
        destination = string.Empty;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["<home>"] = home,
            ["<documents>"] = Path.Combine(home, "Documents"),
            ["<winDocuments>"] = Path.Combine(home, "Documents"),
            ["<savedGames>"] = Path.Combine(home, "Saved Games"),
            ["<winSavedGames>"] = Path.Combine(home, "Saved Games"),
            ["<appdata>"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["<winAppData>"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["<localappdata>"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["<winLocalAppData>"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ["<localappdataLow>"] = Path.Combine(home, "AppData", "LocalLow"),
            ["<winLocalAppDataLow>"] = Path.Combine(home, "AppData", "LocalLow"),
            ["<winProgramData>"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ["<winDir>"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        };

        var normalized = placeholderPath.Replace('/', '\\');
        foreach (var (token, root) in roots)
        {
            if (!normalized.Equals(token, StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith(token + "\\", StringComparison.OrdinalIgnoreCase)) continue;

            var relative = normalized[token.Length..].TrimStart('\\');
            if (relative.Split('\\').Any(part => part is "" or "." or "..")) return false;

            var rootPath = Path.GetFullPath(root);
            var resolved = Path.GetFullPath(Path.Combine(rootPath, relative));
            var rootWithSeparator = Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)) return false;

            destination = resolved;
            return true;
        }

        return false;
    }
}
