using ludusavo.Models;

namespace ludusavo.Services;

public class RestoreResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int RestoredFilesCount { get; set; }

    /// <summary>True when a failed restore was fully undone and the previous saves are back in place.</summary>
    public bool RolledBack { get; set; }

    /// <summary>Folder holding copies of the save files that existed before this restore (undo point).</summary>
    public string? PreRestoreBackupDir { get; set; }
}

public class RestoreConflict
{
    public string Path { get; set; } = string.Empty;
    public DateTime LocalModifiedUtc { get; set; }
    public DateTime BackupModifiedUtc { get; set; }
}

public class RestorePreview
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int FileCount { get; set; }

    /// <summary>Local files that are newer than the copy in the backup and would be overwritten.</summary>
    public List<RestoreConflict> NewerLocalFiles { get; } = new();
}

public interface IRestoreService
{
    Task<RestorePreview> PreviewRestoreAsync(string gameId, string? specificZipPath = null);
    Task<RestoreResult> RestoreGameAsync(string gameId, string? specificZipPath = null);
}
