using ludusavo.Services;

namespace ludusavo.Commands;

public class PullHandler
{
    private readonly IGoogleDriveService _driveService;
    private readonly IRestoreService _restoreService;
    private readonly IBackupService _backupService;

    public PullHandler(
        IGoogleDriveService driveService,
        IRestoreService restoreService,
        IBackupService backupService)
    {
        _driveService = driveService;
        _restoreService = restoreService;
        _backupService = backupService;
    }

    /// <summary>
    /// The one ordering used to number cloud backups. 'sav pull &lt;#&gt;', 'sav lsc' and the pull
    /// hints in 'sav ls' must all use it so the numbers they show refer to the same game.
    /// </summary>
    public static List<RemoteGameMeta> OrderForSelection(IEnumerable<RemoteGameMeta> metas) =>
        metas.OrderBy(m => m.GameName, StringComparer.OrdinalIgnoreCase)
             .ThenBy(m => m.GameId, StringComparer.OrdinalIgnoreCase)
             .ToList();

    public async Task ExecuteAsync(params string[]? targets)
    {
        if (!_driveService.IsConfigured)
        {
            AnsiConsole.WriteLine("Google Drive is not configured! Please run 'sav config' or 'sav login' first.");
            throw new InvalidOperationException("Google Drive is not configured.");
        }

        List<RemoteGameMeta> remoteList = new();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Fetching save backups from Google Drive...", async ctx =>
            {
                var dict = await _driveService.GetAllRemoteMetasAsync(forceRefresh: true);
                remoteList = OrderForSelection(dict.Values);
            });

        if (remoteList.Count == 0)
        {
            AnsiConsole.WriteLine("No save backups found on Google Drive to download.");
            return;
        }

        var targetInput = targets != null && targets.Length > 0 ? string.Join(" ", targets).Trim() : null;

        // If target parameter is provided from CLI: e.g. pull 1 3 5 or pull all
        if (!string.IsNullOrWhiteSpace(targetInput))
        {
            if (targetInput.Equals("all", StringComparison.OrdinalIgnoreCase)
                || targetInput.Equals("a", StringComparison.OrdinalIgnoreCase)
                || targetInput.Equals("-a", StringComparison.OrdinalIgnoreCase)
                || targetInput.Equals("--all", StringComparison.OrdinalIgnoreCase))
            {
                await PullBatchAsync(remoteList);
                return;
            }

            if (ConsoleHelper.TryParseSelectionIndices(targetInput, remoteList.Count, out var indices))
            {
                var selected = indices.Select(i => remoteList[i - 1]).ToList();
                if (selected.Count == 1)
                {
                    await PullSingleAsync(selected[0]);
                }
                else
                {
                    await PullBatchAsync(selected);
                }
                return;
            }

            var matches = remoteList.Where(m => m.GameName.Contains(targetInput, StringComparison.OrdinalIgnoreCase) || m.GameId.Equals(targetInput, StringComparison.OrdinalIgnoreCase)).ToList();
            var matched = matches.Count == 1 ? matches[0] : null;
            if (matched != null)
            {
                await PullSingleAsync(matched);
                return;
            }

            if (matches.Count > 1)
            {
                AnsiConsole.WriteLine($"More than one backup matches '{targetInput}'. Use a more specific name or choose a number:");
                foreach (var game in matches)
                    AnsiConsole.WriteLine($"  {remoteList.IndexOf(game) + 1}. {game.GameName}");
                throw new InvalidOperationException("Backup name is ambiguous.");
            }
            AnsiConsole.WriteLine($"No save backup matching '{targetInput}' found. Try 'sav lsc' to see cloud backups.");
            throw new InvalidOperationException($"No save backup matching '{targetInput}' found.");
        }

        int maxIndexWidth = $"[{remoteList.Count}]".Length;
        int maxNameWidth = remoteList.Max(m => m.GameName.Length);
        int maxSizeWidth = remoteList.Max(m => (string.IsNullOrEmpty(m.FormattedSize) ? "-" : m.FormattedSize).Length);

        for (int i = 0; i < remoteList.Count; i++)
        {
            var m = remoteList[i];
            var idxStr = $"[{i + 1}]".PadRight(maxIndexWidth);
            var nameStr = m.GameName.PadRight(maxNameWidth);
            var sizeStr = (string.IsNullOrEmpty(m.FormattedSize) ? "-" : m.FormattedSize).PadLeft(maxSizeWidth);
            var dateStr = ConsoleHelper.FormatDateTime(m.Timestamp);

            AnsiConsole.WriteLine($"{idxStr}  {nameStr}  {sizeStr}  {dateStr}");
        }
        AnsiConsole.WriteLine($"{"[A]".PadRight(maxIndexWidth)}  Restore all game saves to this machine");
        AnsiConsole.WriteLine($"{"[0]".PadRight(maxIndexWidth)}  Cancel\n");

        var choice = AnsiConsole.Prompt("Enter game numbers to pull (e.g. 1 3 5 or 'A' / '0'):");

        choice = choice.Trim();

        if (choice == "0")
        {
            AnsiConsole.WriteLine("Operation cancelled.");
            return;
        }

        if (choice.Equals("A", StringComparison.OrdinalIgnoreCase) || choice.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            await PullBatchAsync(remoteList);
            return;
        }

        if (ConsoleHelper.TryParseSelectionIndices(choice, remoteList.Count, out var selectedIndices))
        {
            var selected = selectedIndices.Select(i => remoteList[i - 1]).ToList();
            if (selected.Count == 1)
            {
                await PullSingleAsync(selected[0]);
            }
            else
            {
                await PullBatchAsync(selected);
            }
        }
        else
        {
            AnsiConsole.WriteLine("Invalid selection.");
            throw new InvalidOperationException("Invalid backup selection.");
        }
    }

    /// <summary>A downloaded-but-not-yet-applied backup, kept outside the cache's latest.zip until restored.</summary>
    private sealed record PreparedPull(RemoteGameMeta Meta, string IncomingDir, string IncomingZip, RestorePreview Preview);

    private async Task<(PreparedPull? prepared, string? error)> PrepareAsync(RemoteGameMeta meta)
    {
        var cacheDir = _backupService.GetGameCacheDir(meta.GameId);
        var incomingDir = Path.Combine(cacheDir, "incoming");
        var dl = await _driveService.DownloadGameSaveAsync(meta.GameId, incomingDir);
        if (!dl.success)
        {
            Discard(incomingDir);
            return (null, $"Download error: {dl.error ?? "Failed to download file from Google Drive"}");
        }

        var incomingZip = Path.Combine(incomingDir, "latest.zip");
        var preview = await _restoreService.PreviewRestoreAsync(meta.GameId, incomingZip);
        if (!preview.Success)
        {
            Discard(incomingDir);
            return (null, $"Invalid backup: {preview.ErrorMessage ?? "unknown error"}");
        }

        return (new PreparedPull(meta, incomingDir, incomingZip, preview), null);
    }

    private async Task<RestoreResult> ApplyAsync(PreparedPull p)
    {
        try
        {
            var result = await _restoreService.RestoreGameAsync(p.Meta.GameId, p.IncomingZip);
            if (result.Success)
            {
                // The restored archive now matches this PC, so it becomes the cached copy used for sync status.
                var cachedZip = Path.Combine(_backupService.GetGameCacheDir(p.Meta.GameId), "latest.zip");
                File.Move(p.IncomingZip, cachedZip, overwrite: true);
            }
            return result;
        }
        finally
        {
            Discard(p.IncomingDir);
        }
    }

    private static void Discard(string incomingDir)
    {
        try
        {
            if (Directory.Exists(incomingDir)) Directory.Delete(incomingDir, recursive: true);
        }
        catch { }
    }

    private static void PrintNewerLocalFiles(RestorePreview preview, string indent = "  ")
    {
        const int maxShown = 5;
        foreach (var c in preview.NewerLocalFiles.Take(maxShown))
        {
            AnsiConsole.WriteLine($"{indent}{c.Path}  (this PC: {ConsoleHelper.FormatDateTime(c.LocalModifiedUtc)}, backup: {ConsoleHelper.FormatDateTime(c.BackupModifiedUtc)})");
        }
        if (preview.NewerLocalFiles.Count > maxShown)
        {
            AnsiConsole.WriteLine($"{indent}... and {preview.NewerLocalFiles.Count - maxShown} more");
        }
    }

    private static void PrintRestoreResult(RestoreResult res)
    {
        if (res.Success && !string.IsNullOrEmpty(res.PreRestoreBackupDir))
        {
            AnsiConsole.WriteLine($"Previous save files were copied to: {res.PreRestoreBackupDir}");
        }
    }

    private async Task PullSingleAsync(RemoteGameMeta meta)
    {
        AnsiConsole.WriteLine($"Starting download and restore: {meta.GameName}");

        PreparedPull? prepared = null;
        string? error = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Downloading save archive from Google Drive...", async ctx =>
            {
                (prepared, error) = await PrepareAsync(meta);
            });

        if (prepared == null)
        {
            AnsiConsole.WriteLine($"Restore failed: {error}");
            throw new InvalidOperationException(error ?? "Restore failed.");
        }

        var preview = prepared.Preview;
        bool confirmed;
        if (preview.NewerLocalFiles.Count > 0)
        {
            AnsiConsole.WriteLine($"WARNING: {preview.NewerLocalFiles.Count} save file(s) on this PC are NEWER than the cloud backup:");
            PrintNewerLocalFiles(preview);
            confirmed = AnsiConsole.Confirm($"Overwrite them with the older cloud saves for '{meta.GameName}'?", defaultVal: false);
        }
        else
        {
            confirmed = AnsiConsole.Confirm($"Restore {preview.FileCount} file(s) for '{meta.GameName}'? Existing save files may be overwritten.", defaultVal: false);
        }

        if (!confirmed)
        {
            Discard(prepared.IncomingDir);
            AnsiConsole.WriteLine("Restore cancelled; no files were changed.");
            return;
        }

        RestoreResult result = new();
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Extracting and restoring into save directory...", async ctx =>
            {
                result = await ApplyAsync(prepared);
            });

        if (result.Success)
        {
            AnsiConsole.WriteLine($"Successfully restored {result.RestoredFilesCount} save file(s) for '{meta.GameName}'.");
            PrintRestoreResult(result);
        }
        else
        {
            AnsiConsole.WriteLine($"Restore failed: {result.ErrorMessage ?? "Unknown error"}");
            throw new InvalidOperationException(result.ErrorMessage ?? "Restore failed.");
        }
    }

    private async Task PullBatchAsync(List<RemoteGameMeta> list)
    {
        AnsiConsole.WriteLine($"Starting pull for {list.Count} game(s) from Google Drive...");

        int failCount = 0;
        var prepared = new List<PreparedPull>();

        // 1. Download and inspect everything first; nothing on disk changes yet.
        for (int i = 0; i < list.Count; i++)
        {
            var meta = list[i];
            AnsiConsole.Write($"[{i + 1}/{list.Count}] Downloading {meta.GameName}... ");
            var (p, error) = await PrepareAsync(meta);
            if (p == null)
            {
                AnsiConsole.WriteLine(error ?? "failed");
                failCount++;
                continue;
            }
            AnsiConsole.WriteLine(p.Preview.NewerLocalFiles.Count > 0 ? "OK (this PC has newer saves)" : "OK");
            prepared.Add(p);
        }

        if (prepared.Count == 0)
        {
            AnsiConsole.WriteLine("\nNothing could be downloaded; no files were changed.");
            throw new InvalidOperationException($"{failCount} game(s) failed to download.");
        }

        // 2. Ask once, with an extra explicit question for games where this PC is newer.
        var totalFiles = prepared.Sum(p => p.Preview.FileCount);
        AnsiConsole.WriteLine();
        if (!AnsiConsole.Confirm($"Restore {totalFiles} file(s) across {prepared.Count} game(s)? Existing save files may be overwritten.", defaultVal: false))
        {
            prepared.ForEach(p => Discard(p.IncomingDir));
            AnsiConsole.WriteLine("Restore cancelled; no files were changed.");
            return;
        }

        var conflicted = prepared.Where(p => p.Preview.NewerLocalFiles.Count > 0).ToList();
        if (conflicted.Count > 0)
        {
            AnsiConsole.WriteLine($"WARNING: {conflicted.Count} game(s) have save files on this PC that are NEWER than the cloud backup:");
            foreach (var p in conflicted)
            {
                AnsiConsole.WriteLine($"  {p.Meta.GameName}:");
                PrintNewerLocalFiles(p.Preview, "    ");
            }
            if (!AnsiConsole.Confirm("Overwrite those newer local saves too? (No = skip these games)", defaultVal: false))
            {
                conflicted.ForEach(p => Discard(p.IncomingDir));
                prepared = prepared.Except(conflicted).ToList();
                AnsiConsole.WriteLine($"Skipping {conflicted.Count} game(s).");
            }
        }

        // 3. Apply.
        int successCount = 0;
        string? lastBackupDir = null;
        for (int i = 0; i < prepared.Count; i++)
        {
            var p = prepared[i];
            AnsiConsole.Write($"[{i + 1}/{prepared.Count}] Restoring {p.Meta.GameName}... ");
            var res = await ApplyAsync(p);
            if (res.Success)
            {
                AnsiConsole.WriteLine($"Restored {res.RestoredFilesCount} files");
                lastBackupDir ??= res.PreRestoreBackupDir;
                successCount++;
            }
            else
            {
                AnsiConsole.WriteLine($"Extraction error: {res.ErrorMessage ?? ""}");
                failCount++;
            }
        }

        AnsiConsole.WriteLine($"\nCompleted! Succeeded: {successCount}, Failed: {failCount}.");
        if (lastBackupDir != null)
        {
            AnsiConsole.WriteLine("Previous save files were copied to each game's 'pre-restore' folder in the sav cache.");
        }
        if (failCount > 0)
        {
            throw new InvalidOperationException($"{failCount} game(s) failed to restore.");
        }
    }
}
