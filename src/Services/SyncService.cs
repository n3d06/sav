using System.IO;
using ludusavo.Models;

namespace ludusavo.Services;

public class SyncService : ISyncService
{
    private readonly IGoogleDriveService _driveService;
    private readonly IBackupService _backupService;
    private readonly IRestoreService _restoreService;
    private readonly IConfigService _configService;

    public SyncService(
        IGoogleDriveService driveService,
        IBackupService backupService,
        IRestoreService restoreService,
        IConfigService configService)
    {
        _driveService = driveService;
        _backupService = backupService;
        _restoreService = restoreService;
        _configService = configService;
    }

    public async Task CheckSyncStatusAsync(DetectedGame game)
    {
        if (game == null) return;

        if (!_driveService.IsConfigured)
        {
            game.Status = game.Files.Count > 0 ? SyncStatus.LocalOnly : SyncStatus.Unknown;
            return;
        }

        try
        {
            var remote = await _driveService.GetRemoteMetaAsync(game.Id);
            UpdateGameStatusWithRemote(game, remote);
        }
        catch
        {
            game.Status = game.Files.Count > 0 ? SyncStatus.LocalOnly : SyncStatus.Unknown;
        }
    }

    private void UpdateGameStatusWithRemote(DetectedGame game, RemoteGameMeta? remote)
    {
        if (remote == null)
        {
            game.Status = game.Files.Count > 0 ? SyncStatus.LocalOnly : SyncStatus.Unknown;
            return;
        }

        game.RemoteModified = remote.Timestamp;
        var remoteTimestampUtc = remote.Timestamp.ToUniversalTime();

        var localZip = Path.Combine(_backupService.GetGameCacheDir(game.Id), "latest.zip");
        if (File.Exists(localZip))
        {
            var localHash = BackupService.ComputeSha256(localZip);
            if (!string.IsNullOrEmpty(remote.ArchiveHash) &&
                string.Equals(localHash, remote.ArchiveHash, StringComparison.OrdinalIgnoreCase))
            {
                var latestLocalFileTimeUtc = game.Files.Count > 0
                    ? game.Files.Max(f => f.LastWriteTime.ToUniversalTime())
                    : DateTime.MinValue;
                if (latestLocalFileTimeUtc > remoteTimestampUtc.AddSeconds(5))
                {
                    game.Status = SyncStatus.LocalNewer;
                }
                else
                {
                    game.Status = SyncStatus.Synced;
                }
                return;
            }
        }

        var localLatestUtc = game.Files.Count > 0
            ? game.Files.Max(f => f.LastWriteTime.ToUniversalTime())
            : DateTime.MinValue;

        if (localLatestUtc == DateTime.MinValue && remoteTimestampUtc > DateTime.MinValue)
        {
            game.Status = SyncStatus.RemoteNewer;
        }
        else if (localLatestUtc > remoteTimestampUtc.AddSeconds(5))
        {
            game.Status = SyncStatus.LocalNewer;
        }
        else if (remoteTimestampUtc > localLatestUtc.AddSeconds(5))
        {
            game.Status = SyncStatus.RemoteNewer;
        }
        else
        {
            game.Status = SyncStatus.Synced;
        }
    }

    public async Task CheckAllSyncStatusesAsync(IEnumerable<DetectedGame> games)
    {
        if (games == null) return;
        var list = games.ToList();
        if (list.Count == 0) return;

        if (!_driveService.IsConfigured)
        {
            foreach (var g in list)
            {
                g.Status = g.Files.Count > 0 ? SyncStatus.LocalOnly : SyncStatus.Unknown;
            }
            return;
        }

        try
        {
            var catalog = await _driveService.GetAllRemoteMetasAsync();
            foreach (var game in list)
            {
                if (catalog.TryGetValue(game.Id, out var remoteMeta))
                {
                    UpdateGameStatusWithRemote(game, remoteMeta);
                }
                else
                {
                    game.Status = game.Files.Count > 0 ? SyncStatus.LocalOnly : SyncStatus.Unknown;
                }
            }
        }
        catch
        {
            foreach (var g in list)
            {
                g.Status = g.Files.Count > 0 ? SyncStatus.LocalOnly : SyncStatus.Unknown;
            }
        }
    }

    public async Task<(bool success, string? message)> SyncGameToCloudAsync(DetectedGame game)
    {
        if (game == null) return (false, "Game null");

        game.IsBusy = true;
        game.StatusMessage = "Backing up save files...";

        try
        {
            var backupResult = await _backupService.CreateBackupAsync(game);
            if (!backupResult.Success)
            {
                game.IsBusy = false;
                game.StatusMessage = backupResult.ErrorMessage ?? "Backup error";
                return (false, backupResult.ErrorMessage);
            }

            if (_driveService.IsConfigured)
            {
                game.StatusMessage = "Uploading to Google Drive...";
                var uploadResult = await _driveService.UploadGameSaveAsync(
                    game.Id,
                    backupResult.ZipPath,
                    backupResult.MetaPath,
                    backupResult.MappingPath);

                if (!uploadResult.success)
                {
                    game.IsBusy = false;
                    game.StatusMessage = uploadResult.error ?? "Google Drive upload error";
                    return (false, uploadResult.error);
                }

                game.Status = SyncStatus.Synced;
                game.StatusMessage = "Synced to Google Drive!";
            }
            else
            {
                game.Status = SyncStatus.LocalOnly;
                game.StatusMessage = "Saved locally (Google Drive not configured)";
            }

            game.IsBusy = false;
            return (true, game.StatusMessage);
        }
        catch (Exception ex)
        {
            game.IsBusy = false;
            game.StatusMessage = ex.Message;
            return (false, ex.Message);
        }
    }

    public async Task<(bool success, string? message, int successCount, int failCount)> SyncGamesBatchToCloudAsync(
        List<DetectedGame> games,
        Action<string>? progressCallback = null)
    {
        if (games == null || games.Count == 0)
        {
            return (true, "No games need synchronization.", 0, 0);
        }

        var payloads = new List<GameBackupPayload>();
        var backedUpGames = new List<DetectedGame>();
        int failCount = 0;

        string? lastError = null;

        for (int i = 0; i < games.Count; i++)
        {
            var game = games[i];
            var progressText = games.Count == 1 
                ? $"Compressing save files: {game.Name}..." 
                : $"[{i + 1}/{games.Count}] Compressing save files: {game.Name}...";
            progressCallback?.Invoke(progressText);

            var backupRes = await _backupService.CreateBackupAsync(game);
            if (!backupRes.Success)
            {
                failCount++;
                lastError = backupRes.ErrorMessage;
                continue;
            }

            payloads.Add(new GameBackupPayload
            {
                GameId = game.Id,
                GameName = game.Name,
                ZipPath = backupRes.ZipPath,
                MetaPath = backupRes.MetaPath,
                MappingPath = backupRes.MappingPath
            });
            backedUpGames.Add(game);
        }

        if (payloads.Count == 0)
        {
            var errMsg = !string.IsNullOrEmpty(lastError) 
                ? lastError 
                : "Could not back up save files for any game.";
            return (false, errMsg, 0, failCount);
        }

        if (_driveService.IsConfigured)
        {
            var uploadRes = await _driveService.UploadGameSavesBatchAsync(payloads, progressCallback);
            if (!uploadRes.success)
            {
                return (false, uploadRes.error ?? "Failed to upload to Google Drive", 0, games.Count);
            }

            foreach (var g in backedUpGames)
            {
                g.Status = SyncStatus.Synced;
                g.StatusMessage = "Synced to Google Drive!";
            }
        }
        else
        {
            foreach (var g in backedUpGames)
            {
                g.Status = SyncStatus.LocalOnly;
                g.StatusMessage = "Saved locally (Google Drive not configured)";
            }
        }

        return (true, $"Successfully backed up {backedUpGames.Count} game(s) to Google Drive.", backedUpGames.Count, failCount);
    }

    public async Task<(bool success, string? message)> RestoreGameFromCloudAsync(DetectedGame game)
    {
        if (game == null) return (false, "Game null");

        game.IsBusy = true;
        game.StatusMessage = "Downloading from Google Drive...";

        try
        {
            var cacheDir = _backupService.GetGameCacheDir(game.Id);
            if (_driveService.IsConfigured)
            {
                var dl = await _driveService.DownloadGameSaveAsync(game.Id, cacheDir);
                if (!dl.success)
                {
                    game.IsBusy = false;
                    game.StatusMessage = dl.error ?? "Google Drive download error";
                    return (false, dl.error);
                }
            }

            game.StatusMessage = "Extracting save files to PC...";
            var restoreRes = await _restoreService.RestoreGameAsync(game.Id);
            if (!restoreRes.Success)
            {
                game.IsBusy = false;
                game.StatusMessage = restoreRes.ErrorMessage ?? "Save restore error";
                return (false, restoreRes.ErrorMessage);
            }

            game.Status = SyncStatus.Synced;
            game.StatusMessage = $"Successfully restored {restoreRes.RestoredFilesCount} files!";
            game.IsBusy = false;
            return (true, game.StatusMessage);
        }
        catch (Exception ex)
        {
            game.IsBusy = false;
            game.StatusMessage = ex.Message;
            return (false, ex.Message);
        }
    }

    public async Task<(bool success, string? message)> UploadCustomManifestAsync()
    {
        if (!_driveService.IsConfigured) return (false, "Google Drive is not configured");
        var customPath = Path.Combine(_configService.CacheDir, "custom_manifest.json");
        if (!File.Exists(customPath)) return (true, null);

        return await _driveService.UploadCustomManifestAsync(customPath);
    }

    public async Task<(bool success, string? message)> DownloadCustomManifestAsync()
    {
        if (!_driveService.IsConfigured) return (false, "Google Drive is not configured");
        var customPath = Path.Combine(_configService.CacheDir, "custom_manifest.json");
        return await _driveService.DownloadCustomManifestAsync(customPath);
    }
}
