using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ludusavo.Models;
using YamlDotNet.Serialization;

namespace ludusavo.Services;

public class BackupService : IBackupService
{
    private readonly IConfigService _configService;

    public BackupService(IConfigService configService)
    {
        _configService = configService;
    }

    public static string SanitizeFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString().Trim();
    }

    public string GetGameCacheDir(string gameId)
    {
        var safeFolder = SanitizeFolderName(gameId);
        var dir = Path.Combine(_configService.CacheDir, safeFolder);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    public static string ComputeSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }

    public async Task<BackupResult> CreateBackupAsync(DetectedGame game)
    {
        if (game == null || game.Files.Count == 0)
        {
            return new BackupResult { Success = false, ErrorMessage = "No save files found for this game." };
        }

        string? tempZipToCleanup = null;
        try
        {
            var gameCacheDir = GetGameCacheDir(game.Id);
            var timestamp = DateTime.UtcNow;

            // Generate mapping for Ludusavi
            var drives = new Dictionary<string, string>();
            var fileList = new List<object>();

            var zipPath = Path.Combine(gameCacheDir, "latest.zip");
            var metaPath = Path.Combine(gameCacheDir, "meta.json");
            var mappingPath = Path.Combine(gameCacheDir, "mapping.yaml");

            // Temporary file to write zip atomically
            var safeId = SanitizeFolderName(game.Id);
            var tempZip = Path.Combine(gameCacheDir, $".latest-{Guid.NewGuid():N}.tmp");
            tempZipToCleanup = tempZip;

            await Task.Run(() =>
            {
                using (var zipStream = new FileStream(tempZip, FileMode.Create, FileAccess.Write))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    foreach (var file in game.Files)
                    {
                        if (!File.Exists(file.AbsolutePath)) continue;

                        var fullPath = Path.GetFullPath(file.AbsolutePath);
                        var root = Path.GetPathRoot(fullPath) ?? "C:\\";
                        var letter = root.TrimEnd(':', '\\', '/').ToUpper();
                        if (string.IsNullOrEmpty(letter)) letter = "C";

                        var driveKey = $"drive-{letter}";
                        drives[driveKey] = $"{letter}:";

                        var relPath = fullPath.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                        var archiveEntryName = $"{driveKey}/{relPath}";

                        // Single-pass read: stream file, compute SHA256 and compress into zip simultaneously
                        string hash;
                        var entry = archive.CreateEntry(archiveEntryName, CompressionLevel.Optimal);
                        using (var srcStream = OpenFileStreamSafely(fullPath))
                        using (var entryStream = entry.Open())
                        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                        {
                            var buffer = new byte[81920]; // 80KB buffer
                            int bytesRead;
                            while ((bytesRead = srcStream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                sha.AppendData(buffer, 0, bytesRead);
                                entryStream.Write(buffer, 0, bytesRead);
                            }
                            hash = BitConverter.ToString(sha.GetHashAndReset()).Replace("-", "").ToLowerInvariant();
                        }

                        file.Sha256 = hash;

                        fileList.Add(new
                        {
                            path = file.PlaceholderPath,
                            archivePath = archiveEntryName,
                            size = file.Size,
                            hash = hash,
                            modified = file.LastWriteTime.ToString("o")
                        });
                    }

                    if (fileList.Count == 0)
                    {
                        throw new InvalidOperationException("No save files could be read; the existing backup was kept.");
                    }

                    // Embed mapping.yaml in archive for portability and Ludusavi compatibility
                    var serializer = new SerializerBuilder().Build();
                    var mappingObj = new Dictionary<string, object>
                    {
                        { "drives", drives }
                    };
                    var yamlContent = serializer.Serialize(mappingObj);
                    var mappingEntry = archive.CreateEntry("mapping.yaml", CompressionLevel.Optimal);
                    using (var entryStream = mappingEntry.Open())
                    using (var writer = new StreamWriter(entryStream, Encoding.UTF8))
                    {
                        writer.Write(yamlContent);
                    }
                }

                // Publish the completed archive in one same-volume replace operation.
                File.Move(tempZip, zipPath, overwrite: true);
                tempZipToCleanup = null;

                // Write mapping.yaml to cache directory as well
                var localSerializer = new SerializerBuilder().Build();
                var localMappingObj = new Dictionary<string, object>
                {
                    { "drives", drives }
                };
                File.WriteAllText(mappingPath, localSerializer.Serialize(localMappingObj));

                // Compute overall archive hash
                var archiveHash = ComputeSha256(zipPath);
                var archiveSize = new FileInfo(zipPath).Length;

                // Write meta.json
                var metaObj = new
                {
                    gameId = game.Id,
                    gameName = game.Name,
                    timestamp = timestamp.ToString("o"),
                    updatedAt = timestamp.ToString("o"),
                    lastModified = timestamp.ToString("o"),
                    fileCount = fileList.Count,
                    totalSize = archiveSize,
                    zipSize = archiveSize,
                    size = archiveSize,
                    archiveHash = archiveHash,
                    sha256 = archiveHash,
                    steamId = game.SteamId,
                    customBannerUrl = game.CustomBannerUrl,
                    files = fileList
                };

                var metaJson = JsonSerializer.Serialize(metaObj, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(metaPath, metaJson);
            });

            return new BackupResult
            {
                Success = true,
                ZipPath = zipPath,
                MetaPath = metaPath,
                MappingPath = mappingPath,
                ArchiveHash = ComputeSha256(zipPath),
                ArchiveSizeBytes = new FileInfo(zipPath).Length,
                Timestamp = timestamp
            };
        }
        catch (Exception ex)
        {
            return new BackupResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            if (tempZipToCleanup != null && File.Exists(tempZipToCleanup))
            {
                try { File.Delete(tempZipToCleanup); } catch { }
            }
        }
    }

    private static FileStream OpenFileStreamSafely(string filePath, int maxRetries = 3)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                // Open with FileShare.ReadWrite so locked/in-use game save files can still be safely read
                return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                Thread.Sleep(300);
            }
        }

        return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }
}
