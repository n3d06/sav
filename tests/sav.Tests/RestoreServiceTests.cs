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
