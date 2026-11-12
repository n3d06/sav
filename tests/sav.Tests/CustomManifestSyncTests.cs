using ludusavo.Models;
using ludusavo.Services;

namespace sav.Tests;

public class CustomManifestSyncTests
{
    [Fact]
    public async Task Scan_uploads_local_changes_instead_of_overwriting_them()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sav-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var config = new DirConfigService(dir);
            var manifest = new ManifestService(config);
            await manifest.AddCustomGameAsync(new GameEntry { Id = "custom_test1", Name = "Offline Game" });
            Assert.True(File.Exists(ManifestService.GetCustomManifestPendingPath(dir)));

            var drive = new FakeDrive();
            var sync = new SyncService(drive, null!, null!, config);
            var result = await sync.DownloadCustomManifestAsync();

            Assert.True(result.success, result.message);
            Assert.Equal(1, drive.Uploads);
            Assert.Equal(0, drive.Downloads);
            Assert.False(File.Exists(ManifestService.GetCustomManifestPendingPath(dir)));
            Assert.Contains("Offline Game", await File.ReadAllTextAsync(Path.Combine(dir, "custom_manifest.json")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Scan_downloads_cloud_list_when_nothing_is_pending()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sav-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var drive = new FakeDrive();
            var sync = new SyncService(drive, null!, null!, new DirConfigService(dir));
            await sync.DownloadCustomManifestAsync();

            Assert.Equal(1, drive.Downloads);
            Assert.Equal(0, drive.Uploads);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_upload_keeps_changes_pending()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sav-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var config = new DirConfigService(dir);
            await new ManifestService(config).AddCustomGameAsync(new GameEntry { Id = "custom_test2", Name = "Game" });

            var drive = new FakeDrive { UploadSucceeds = false };
            var result = await new SyncService(drive, null!, null!, config).UploadCustomManifestAsync();

            Assert.False(result.success);
            Assert.True(File.Exists(ManifestService.GetCustomManifestPendingPath(dir)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class DirConfigService(string dir) : IConfigService
    {
        public AppSettings Settings { get; } = new();
        public string RootDir => dir;
        public string ConfigDir => dir;
        public string DataDir => dir;
        public string CacheDir => dir;
        public string ManifestCachePath => Path.Combine(dir, "manifest.json");
        public string SettingsFilePath => Path.Combine(dir, "settings.json");
        public void SaveSettings(AppSettings newSettings) { }
        public void ReloadSettings() { }
    }

    private sealed class FakeDrive : IGoogleDriveService
    {
        public int Uploads { get; private set; }
        public int Downloads { get; private set; }
        public bool UploadSucceeds { get; set; } = true;
        public bool IsConfigured => true;

        public Task<(bool success, string? error)> UploadCustomManifestAsync(string localPath)
        {
            Uploads++;
            return Task.FromResult(UploadSucceeds ? (true, (string?)null) : (false, "offline"));
        }

        public Task<(bool success, string? error)> DownloadCustomManifestAsync(string localPath)
        {
            Downloads++;
            return Task.FromResult((true, (string?)null));
        }

        public Task<(bool success, string? message)> LoginAsync(Action<string>? statusCallback = null) => throw new NotImplementedException();
        public Task<(bool success, string? message)> TestConnectionAsync() => throw new NotImplementedException();
        public Task<List<string>> ListRemoteGameIdsAsync() => throw new NotImplementedException();
        public Task<RemoteGameMeta?> GetRemoteMetaAsync(string gameId) => throw new NotImplementedException();
        public Task<Dictionary<string, RemoteGameMeta>> GetAllRemoteMetasAsync(bool forceRefresh = false) => throw new NotImplementedException();
        public void InvalidateCatalogCache() { }
        public Task<(bool success, string? error)> UploadGameSaveAsync(string gameId, string zipPath, string metaPath, string mappingPath) => throw new NotImplementedException();
        public Task<(bool success, string? error)> UploadGameSavesBatchAsync(List<GameBackupPayload> payloads, Action<string>? progressCallback = null) => throw new NotImplementedException();
        public Task<(bool success, string? error)> DownloadGameSaveAsync(string gameId, string targetDir) => throw new NotImplementedException();
        public Task<(long usedBytes, long totalBytes, string? userEmail)> GetStorageQuotaAsync() => throw new NotImplementedException();
    }
}
