using System.IO.Compression;
using System.Text.Json;
using ludusavo.Models;
using ludusavo.Services;

namespace sav.Tests;

public class RestoreServiceTests
{
    [Fact]
    public async Task Restore_uses_current_profile_path_and_preserves_timestamp()
    {
        var testId = $"sav-test-{Guid.NewGuid():N}";
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var targetDir = Path.Combine(localAppData, testId);
        var cacheDir = Path.Combine(Path.GetTempPath(), testId);
        var zipPath = Path.Combine(cacheDir, "backup.zip");
        var expectedTime = DateTime.UtcNow.AddDays(-1).AddMilliseconds(-500);

        Directory.CreateDirectory(cacheDir);
        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var file = zip.CreateEntry("drive-C/original-user/AppData/Local/game/save.dat");
                await using (var writer = new StreamWriter(file.Open()))
                {
                    await writer.WriteAsync("save-data");
                }

                var map = zip.CreateEntry("restore-paths.json");
                await using var mapWriter = new StreamWriter(map.Open());
                await mapWriter.WriteAsync(JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        archivePath = file.FullName,
                        placeholderPath = $"<localappdata>/{testId}/save.dat",
                        modifiedUtc = expectedTime.ToString("O")
                    }
                }));
            }

            var service = new RestoreService(new TestConfigService(cacheDir));
            var result = await service.RestoreGameAsync("test", zipPath);
            var restoredPath = Path.Combine(targetDir, "save.dat");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("save-data", await File.ReadAllTextAsync(restoredPath));
            Assert.Equal(expectedTime.Ticks, File.GetLastWriteTimeUtc(restoredPath).Ticks);
        }
        finally
        {
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_rejects_unsafe_portable_path()
    {
        var testId = $"sav-test-{Guid.NewGuid():N}";
        var cacheDir = Path.Combine(Path.GetTempPath(), testId);
        var zipPath = Path.Combine(cacheDir, "backup.zip");
        Directory.CreateDirectory(cacheDir);

        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var file = zip.CreateEntry("drive-C/game/save.dat");
                await using (var writer = new StreamWriter(file.Open()))
                {
                    await writer.WriteAsync("save-data");
                }

                var map = zip.CreateEntry("restore-paths.json");
                await using var mapWriter = new StreamWriter(map.Open());
                await mapWriter.WriteAsync(JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        archivePath = file.FullName,
                        placeholderPath = "<localappdata>/../outside/save.dat"
                    }
                }));
            }

            var service = new RestoreService(new TestConfigService(cacheDir));
            var result = await service.RestoreGameAsync("test", zipPath);

            Assert.False(result.Success);
            Assert.Contains("Unsafe portable path", result.ErrorMessage);
        }
        finally
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_falls_back_to_drive_path_when_placeholder_is_absolute()
    {
        // e.g. Steam userdata or game install dir: ScannerService stores an absolute path, not a <token>.
        var testId = $"sav-test-{Guid.NewGuid():N}";
        var cacheDir = Path.Combine(Path.GetTempPath(), testId);
        var targetDir = Path.Combine(Path.GetTempPath(), testId + "-target");
        var targetFile = Path.GetFullPath(Path.Combine(targetDir, "save.dat"));
        var root = Path.GetPathRoot(targetFile)!;
        var letter = root.TrimEnd(':', '\\', '/').ToUpperInvariant();
        var archivePath = $"drive-{letter}/{targetFile[root.Length..].Replace('\\', '/')}";
        var zipPath = Path.Combine(cacheDir, "backup.zip");
        var expectedTime = DateTime.UtcNow.AddDays(-2);
        Directory.CreateDirectory(cacheDir);

        try
        {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var file = zip.CreateEntry(archivePath);
                await using (var writer = new StreamWriter(file.Open()))
                {
                    await writer.WriteAsync("steam-save");
                }

                var map = zip.CreateEntry("restore-paths.json");
                await using var mapWriter = new StreamWriter(map.Open());
                await mapWriter.WriteAsync(JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        archivePath,
                        placeholderPath = targetFile.Replace('\\', '/'),
                        modifiedUtc = expectedTime.ToString("O")
                    }
                }));
            }

            var service = new RestoreService(new TestConfigService(cacheDir));
            var result = await service.RestoreGameAsync("test", zipPath);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("steam-save", await File.ReadAllTextAsync(targetFile));
            Assert.Equal(expectedTime.Ticks, File.GetLastWriteTimeUtc(targetFile).Ticks);
        }
        finally
        {
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    private static async Task WriteBackupAsync(string zipPath, params (string archivePath, string placeholder, string content, DateTime modifiedUtc)[] files)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var f in files)
        {
            var entry = zip.CreateEntry(f.archivePath);
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(f.content);
        }

        var map = zip.CreateEntry("restore-paths.json");
        await using var mapWriter = new StreamWriter(map.Open());
        await mapWriter.WriteAsync(JsonSerializer.Serialize(files.Select(f => new
        {
            archivePath = f.archivePath,
            placeholderPath = f.placeholder,
            modifiedUtc = f.modifiedUtc.ToString("O")
        })));
    }

    [Fact]
    public async Task Restore_keeps_copy_of_overwritten_files()
    {
        var testId = $"sav-test-{Guid.NewGuid():N}";
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var targetDir = Path.Combine(localAppData, testId);
        var cacheDir = Path.Combine(Path.GetTempPath(), testId);
        var zipPath = Path.Combine(cacheDir, "backup.zip");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(targetDir, "save.dat"), "old");
            await WriteBackupAsync(zipPath, ("drive-C/x/save.dat", $"<localappdata>/{testId}/save.dat", "new", DateTime.UtcNow.AddDays(-1)));

            var result = await new RestoreService(new TestConfigService(cacheDir)).RestoreGameAsync("test", zipPath);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(targetDir, "save.dat")));
            Assert.NotNull(result.PreRestoreBackupDir);
            Assert.True(File.Exists(Path.Combine(result.PreRestoreBackupDir!, "index.json")));
            var backupFile = Directory.GetFiles(result.PreRestoreBackupDir!, "*.bak").Single();
            Assert.Equal("old", await File.ReadAllTextAsync(backupFile));
        }
        finally
        {
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task Restore_rolls_back_when_extraction_fails_midway()
    {
        var testId = $"sav-test-{Guid.NewGuid():N}";
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var targetDir = Path.Combine(localAppData, testId);
        var cacheDir = Path.Combine(Path.GetTempPath(), testId);
        var zipPath = Path.Combine(cacheDir, "backup.zip");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            var first = Path.Combine(targetDir, "a.dat");
            await File.WriteAllTextAsync(first, "old-a");
            // A directory where the second file should go makes extraction fail after the first file was written.
            Directory.CreateDirectory(Path.Combine(targetDir, "b.dat"));
            var newFile = Path.Combine(targetDir, "new.dat");

            var time = DateTime.UtcNow.AddDays(-1);
            await WriteBackupAsync(zipPath,
                ("drive-C/x/a.dat", $"<localappdata>/{testId}/a.dat", "new-a", time),
                ("drive-C/x/new.dat", $"<localappdata>/{testId}/new.dat", "brand-new", time),
                ("drive-C/x/b.dat", $"<localappdata>/{testId}/b.dat", "new-b", time));

            var result = await new RestoreService(new TestConfigService(cacheDir)).RestoreGameAsync("test", zipPath);

            Assert.False(result.Success);
            Assert.True(result.RolledBack, result.ErrorMessage);
            Assert.Equal("old-a", await File.ReadAllTextAsync(first));
            Assert.False(File.Exists(newFile));
        }
        finally
        {
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    [Fact]
    public async Task Preview_reports_local_files_newer_than_backup()
    {
        var testId = $"sav-test-{Guid.NewGuid():N}";
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var targetDir = Path.Combine(localAppData, testId);
        var cacheDir = Path.Combine(Path.GetTempPath(), testId);
        var zipPath = Path.Combine(cacheDir, "backup.zip");
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            var newerLocal = Path.Combine(targetDir, "newer.dat");
            var olderLocal = Path.Combine(targetDir, "older.dat");
            await File.WriteAllTextAsync(newerLocal, "local");
            await File.WriteAllTextAsync(olderLocal, "local");
            var backupTime = DateTime.UtcNow.AddHours(-1);
            File.SetLastWriteTimeUtc(olderLocal, backupTime.AddHours(-1));

            await WriteBackupAsync(zipPath,
                ("drive-C/x/newer.dat", $"<localappdata>/{testId}/newer.dat", "cloud", backupTime),
                ("drive-C/x/older.dat", $"<localappdata>/{testId}/older.dat", "cloud", backupTime));

            var preview = await new RestoreService(new TestConfigService(cacheDir)).PreviewRestoreAsync("test", zipPath);

            Assert.True(preview.Success, preview.ErrorMessage);
            Assert.Equal(2, preview.FileCount);
            var conflict = Assert.Single(preview.NewerLocalFiles);
            Assert.Equal(newerLocal, conflict.Path, ignoreCase: true);
            Assert.Equal("local", await File.ReadAllTextAsync(newerLocal)); // preview never writes
        }
        finally
        {
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
    }

    private sealed class TestConfigService(string cacheDir) : IConfigService
    {
        public AppSettings Settings { get; } = new();
        public string RootDir => cacheDir;
        public string ConfigDir => cacheDir;
        public string DataDir => cacheDir;
        public string CacheDir => cacheDir;
        public string ManifestCachePath => Path.Combine(cacheDir, "manifest.json");
        public string SettingsFilePath => Path.Combine(cacheDir, "settings.json");
        public void SaveSettings(AppSettings newSettings) { }
        public void ReloadSettings() { }
    }
}
