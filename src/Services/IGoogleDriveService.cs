namespace ludusavo.Services;

public class RemoteGameMeta
{
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public int FileCount { get; set; }
    public long TotalSize { get; set; }
    public string ArchiveHash { get; set; } = string.Empty;
    public int? SteamId { get; set; }
    public string? CustomBannerUrl { get; set; }

    /// <summary>Newest save-file modification time inside the backup (UTC). Null for backups made by older versions.</summary>
    public DateTime? LatestFileModifiedUtc { get; set; }

    public string FormattedSize
    {
        get
        {
            if (TotalSize <= 0) return string.Empty;
            if (TotalSize < 1024) return $"{TotalSize} B";
            if (TotalSize < 1024 * 1024) return $"{TotalSize / 1024.0:F1} KB";
            return $"{TotalSize / (1024.0 * 1024.0):F1} MB";
        }
    }

    public string FormattedTimestamp => Timestamp == DateTime.MinValue || Timestamp.Year <= 1
        ? "Unknown"
        : Timestamp.ToString("yyyy-MM-dd HH:mm");
}

public class GameBackupPayload
{
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string ZipPath { get; set; } = string.Empty;
    public string MetaPath { get; set; } = string.Empty;
    public string? MappingPath { get; set; }
}

public interface IGoogleDriveService
{
    bool IsConfigured { get; }
    Task<(bool success, string? message)> LoginAsync(Action<string>? statusCallback = null);
    Task<(bool success, string? message)> TestConnectionAsync();
    Task<List<string>> ListRemoteGameIdsAsync();
    Task<RemoteGameMeta?> GetRemoteMetaAsync(string gameId);
    Task<Dictionary<string, RemoteGameMeta>> GetAllRemoteMetasAsync(bool forceRefresh = false);
    void InvalidateCatalogCache();
    Task<(bool success, string? error)> UploadGameSaveAsync(string gameId, string zipPath, string metaPath, string mappingPath);
    Task<(bool success, string? error)> UploadGameSavesBatchAsync(List<GameBackupPayload> payloads, Action<string>? progressCallback = null);
    Task<(bool success, string? error)> DownloadGameSaveAsync(string gameId, string targetDir);
    Task<(bool success, string? error)> UploadCustomManifestAsync(string localPath);
    Task<(bool success, string? error)> DownloadCustomManifestAsync(string localPath);
    Task<(long usedBytes, long totalBytes, string? userEmail)> GetStorageQuotaAsync();
}

