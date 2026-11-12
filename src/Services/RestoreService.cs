using System.IO;
using System.IO.Compression;
using System.Text.Json;
using YamlDotNet.Serialization;

namespace ludusavo.Services;

public class RestoreService : IRestoreService
{
    /// <summary>Local files newer than the backup by more than this are reported as conflicts.</summary>
    private static readonly TimeSpan NewerTolerance = TimeSpan.FromSeconds(2);

    private readonly IConfigService _configService;

    public RestoreService(IConfigService configService)
    {
        _configService = configService;
    }

    private sealed record PlanItem(ZipArchiveEntry Entry, string Destination, DateTime? ModifiedUtc);

    private string GetGameCacheDir(string gameId) =>
        Path.Combine(_configService.CacheDir, BackupService.SanitizeFolderName(gameId));

    public async Task<RestorePreview> PreviewRestoreAsync(string gameId, string? specificZipPath = null)
    {
        var gameCacheDir = GetGameCacheDir(gameId);
        var zipPath = specificZipPath ?? Path.Combine(gameCacheDir, "latest.zip");
        if (!File.Exists(zipPath))
        {
            return new RestorePreview { Success = false, ErrorMessage = $"Backup archive file not found at {zipPath}" };
        }

        try
        {
            return await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var plan = BuildPlan(zip, LoadDriveMap(Path.Combine(gameCacheDir, "mapping.yaml"), zip));
                var preview = new RestorePreview { Success = true, FileCount = plan.Count };

                foreach (var item in plan)
                {
                    if (!item.ModifiedUtc.HasValue || !File.Exists(item.Destination)) continue;
                    var localUtc = File.GetLastWriteTimeUtc(item.Destination);
                    if (localUtc > item.ModifiedUtc.Value + NewerTolerance)
                    {
                        preview.NewerLocalFiles.Add(new RestoreConflict
                        {
                            Path = item.Destination,
                            LocalModifiedUtc = localUtc,
                            BackupModifiedUtc = item.ModifiedUtc.Value
                        });
                    }
                }
                return preview;
            });
        }
        catch (Exception ex)
        {
            return new RestorePreview { Success = false, ErrorMessage = ex.Message };
        }
    }

    public async Task<RestoreResult> RestoreGameAsync(string gameId, string? specificZipPath = null)
    {
        var gameCacheDir = GetGameCacheDir(gameId);
        var zipPath = specificZipPath ?? Path.Combine(gameCacheDir, "latest.zip");

        if (!File.Exists(zipPath))
        {
            return new RestoreResult { Success = false, ErrorMessage = $"Backup archive file not found at {zipPath}" };
        }

        try
        {
            return await Task.Run(() =>
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var plan = BuildPlan(zip, LoadDriveMap(Path.Combine(gameCacheDir, "mapping.yaml"), zip));
                return ApplyPlan(plan, gameCacheDir);
            });
        }
        catch (Exception ex)
        {
            return new RestoreResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Copies every file that is about to be overwritten into a staging folder first, then extracts.
    /// If extraction fails part-way, already-written files are rolled back from that copy so the
    /// save folder is never left half old / half new. On success the copy is kept as
    /// &lt;cache&gt;/&lt;game&gt;/pre-restore so the user can undo a restore by hand.
    /// </summary>
    private static RestoreResult ApplyPlan(List<PlanItem> plan, string gameCacheDir)
    {
        Directory.CreateDirectory(gameCacheDir);
        var preRestoreDir = Path.Combine(gameCacheDir, "pre-restore");
        var stagingDir = Path.Combine(gameCacheDir, $".pre-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        // 1. Back up existing files. Nothing on disk has been touched yet, so a failure here is harmless.
        var backups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = new List<object>();
        try
        {
            for (int i = 0; i < plan.Count; i++)
            {
                var dest = plan[i].Destination;
                if (backups.ContainsKey(dest) || !File.Exists(dest)) continue;
                var backupFile = Path.Combine(stagingDir, $"{i}.bak");
                File.Copy(dest, backupFile);
                backups[dest] = backupFile;
                index.Add(new { original = dest, backup = $"{i}.bak" });
            }
            File.WriteAllText(Path.Combine(stagingDir, "index.json"),
                JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(stagingDir);
            return new RestoreResult
            {
                Success = false,
                ErrorMessage = $"Could not back up current save files, nothing was changed: {ex.Message}"
            };
        }

        // 2. Extract, remembering what was written so it can be undone.
        var written = new List<string>();
        try
        {
            foreach (var (entry, destPath, modifiedUtc) in plan)
            {
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                ClearReadOnly(destPath);
                written.Add(destPath);
                entry.ExtractToFile(destPath, overwrite: true);
                if (modifiedUtc.HasValue)
                {
                    File.SetLastWriteTimeUtc(destPath, modifiedUtc.Value);
                }
            }
        }
        catch (Exception ex)
        {
            var rollbackErrors = Rollback(written, backups);
            if (rollbackErrors == 0)
            {
                TryDeleteDirectory(stagingDir);
                return new RestoreResult
                {
                    Success = false,
                    RolledBack = true,
                    ErrorMessage = $"Restore failed; your previous save files were put back: {ex.Message}"
                };
            }

            return new RestoreResult
            {
                Success = false,
                PreRestoreBackupDir = stagingDir,
                ErrorMessage = $"Restore failed and {rollbackErrors} file(s) could not be rolled back. " +
                               $"Copies of your previous saves are in '{stagingDir}' (see index.json): {ex.Message}"
            };
        }

        // 3. Keep the latest pre-restore copy as an undo point.
        string? keptDir = null;
        try
        {
            TryDeleteDirectory(preRestoreDir);
            Directory.Move(stagingDir, preRestoreDir);
            keptDir = preRestoreDir;
        }
        catch
        {
            keptDir = stagingDir;
        }

        return new RestoreResult
        {
            Success = true,
            RestoredFilesCount = plan.Count,
            PreRestoreBackupDir = backups.Count > 0 ? keptDir : null
        };
    }

    private static int Rollback(List<string> written, Dictionary<string, string> backups)
    {
        int errors = 0;
        for (int i = written.Count - 1; i >= 0; i--)
        {
            var dest = written[i];
            try
            {
                ClearReadOnly(dest);
                if (backups.TryGetValue(dest, out var backupFile))
                {
                    File.Copy(backupFile, dest, overwrite: true);
                }
                else if (File.Exists(dest))
                {
                    File.Delete(dest);
                }
            }
            catch
            {
                errors++;
            }
        }
        return errors;
    }

    private static void ClearReadOnly(string path)
    {
        if (!File.Exists(path)) return;
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { }
    }

    private static Dictionary<string, string> LoadDriveMap(string mappingPath, ZipArchive zip)
    {
        var driveMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Parse(string yaml)
        {
            var root = new DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(yaml);
            if (root != null && root.TryGetValue("drives", out var drivesObj) && drivesObj is Dictionary<object, object> dDict)
            {
                foreach (var kv in dDict)
                {
                    driveMap[kv.Key.ToString()!] = kv.Value.ToString()!;
                }
            }
        }

        if (File.Exists(mappingPath))
        {
            try { Parse(File.ReadAllText(mappingPath)); } catch { }
        }

        // Fall back to the mapping embedded in the zip itself
        var embeddedMapping = zip.GetEntry("mapping.yaml");
        if (embeddedMapping != null && driveMap.Count == 0)
        {
            try
            {
                using var reader = new StreamReader(embeddedMapping.Open());
                Parse(reader.ReadToEnd());
            }
            catch { }
        }

        return driveMap;
    }

    private static List<PlanItem> BuildPlan(ZipArchive zip, Dictionary<string, string> driveMap)
    {
        var portablePaths = ReadPortablePaths(zip);
        var plan = new List<PlanItem>();

        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // Directory entry
            if (entry.FullName.Equals("mapping.yaml", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.Equals("restore-paths.json", StringComparison.OrdinalIgnoreCase)) continue;

            DateTime? portableModifiedUtc = null;
            if (portablePaths.TryGetValue(entry.FullName, out var portable))
            {
                portableModifiedUtc = portable.ModifiedUtc;
                switch (ResolvePortablePath(portable.PlaceholderPath, out var portableDestination))
                {
                    case PortablePathResolution.Resolved:
                        plan.Add(new PlanItem(entry, portableDestination, portable.ModifiedUtc));
                        continue;
                    case PortablePathResolution.Unsafe:
                        throw new InvalidDataException($"Unsafe portable path in backup archive: {entry.FullName}");
                    case PortablePathResolution.NotPortable:
                        // Absolute path or unknown placeholder (e.g. Steam userdata, game install dir):
                        // fall back to the drive-mapped archive path below.
                        break;
                }
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
            plan.Add(new PlanItem(entry, destPath, portableModifiedUtc));
        }

        return plan;
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
                    if (path.ModifiedUtc.HasValue)
                    {
                        path.ModifiedUtc = path.ModifiedUtc.Value.ToUniversalTime();
                    }
                    result[path.ArchivePath] = path;
                }
            }
        }
        catch { }

        return result;
    }

    private enum PortablePathResolution
    {
        Resolved,
        NotPortable,
        Unsafe
    }

    private static PortablePathResolution ResolvePortablePath(string placeholderPath, out string destination)
    {
        destination = string.Empty;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var driveRoot = Path.GetPathRoot(home) ?? "C:\\";
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
            ["<winDir>"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["<winPublic>"] = Path.Combine(driveRoot, "Users", "Public"),
            ["<xdgConfig>"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ["<xdgData>"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        var normalized = placeholderPath.Replace('/', '\\');
        foreach (var (token, root) in roots)
        {
            if (!normalized.Equals(token, StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith(token + "\\", StringComparison.OrdinalIgnoreCase)) continue;

            if (string.IsNullOrEmpty(root)) return PortablePathResolution.NotPortable;

            var relative = normalized[token.Length..].TrimStart('\\');
            if (relative.Split('\\').Any(part => part is "" or "." or "..")) return PortablePathResolution.Unsafe;

            var rootPath = Path.GetFullPath(root);
            var resolved = Path.GetFullPath(Path.Combine(rootPath, relative));
            var rootWithSeparator = Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)) return PortablePathResolution.Unsafe;

            destination = resolved;
            return PortablePathResolution.Resolved;
        }

        // Not one of the known profile placeholders (absolute path such as Steam userdata,
        // or a placeholder we can't map portably): let the caller use the drive-based path.
        return PortablePathResolution.NotPortable;
    }
}
