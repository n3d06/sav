using System.IO;
using System.IO.Compression;
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

                var filesToRestore = new List<(ZipArchiveEntry Entry, string Destination)>();
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // Directory entry
                    if (entry.FullName.Equals("mapping.yaml", StringComparison.OrdinalIgnoreCase)) continue;

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
                    filesToRestore.Add((entry, destPath));
                }

                foreach (var (entry, destPath) in filesToRestore)
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
}
