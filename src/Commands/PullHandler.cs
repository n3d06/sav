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
                remoteList = dict.Values.OrderBy(m => m.GameName, StringComparer.OrdinalIgnoreCase).ToList();
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

    private async Task PullSingleAsync(RemoteGameMeta meta)
    {
        AnsiConsole.WriteLine($"Starting download and restore: {meta.GameName}");
        if (!AnsiConsole.Confirm($"Restore {meta.FileCount} file(s) for '{meta.GameName}'? Existing save files may be overwritten.", defaultVal: false))
        {
            AnsiConsole.WriteLine("Restore cancelled; no files were changed.");
            return;
        }

        bool success = false;
        string? error = null;
        int restoredCount = 0;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Downloading save archive from Google Drive...", async ctx =>
            {
                var cacheDir = _backupService.GetGameCacheDir(meta.GameId);
                var dl = await _driveService.DownloadGameSaveAsync(meta.GameId, cacheDir);
                if (!dl.success)
                {
                    error = dl.error ?? "Failed to download file from Google Drive";
                    return;
                }

                ctx.Status("Extracting and restoring into save directory...");
                var zipPath = Path.Combine(cacheDir, "latest.zip");
                var restoreRes = await _restoreService.RestoreGameAsync(meta.GameId, zipPath);
                success = restoreRes.Success;
                error = restoreRes.ErrorMessage;
                restoredCount = restoreRes.RestoredFilesCount;
            });

        if (success)
        {
            AnsiConsole.WriteLine($"Successfully restored {restoredCount} save file(s) for '{meta.GameName}'.");
        }
        else
        {
            AnsiConsole.WriteLine($"Restore failed: {error ?? "Unknown error"}");
            throw new InvalidOperationException(error ?? "Restore failed.");
        }
    }

    private async Task PullBatchAsync(List<RemoteGameMeta> list)
    {
        AnsiConsole.WriteLine($"Starting pull for {list.Count} game(s) from Google Drive...");
        var totalFiles = list.Sum(game => game.FileCount);
        if (!AnsiConsole.Confirm($"Restore {totalFiles} file(s) across {list.Count} game(s)? Existing save files may be overwritten.", defaultVal: false))
        {
            AnsiConsole.WriteLine("Restore cancelled; no files were changed.");
            return;
        }

        int successCount = 0;
        int failCount = 0;

        for (int i = 0; i < list.Count; i++)
        {
            var meta = list[i];
            AnsiConsole.Write($"[{i + 1}/{list.Count}] Processing {meta.GameName}... ");

            var cacheDir = _backupService.GetGameCacheDir(meta.GameId);
            var dl = await _driveService.DownloadGameSaveAsync(meta.GameId, cacheDir);
            if (!dl.success)
            {
                AnsiConsole.WriteLine($"Download error: {dl.error ?? ""}");
                failCount++;
                continue;
            }

            var zipPath = Path.Combine(cacheDir, "latest.zip");
            var restoreRes = await _restoreService.RestoreGameAsync(meta.GameId, zipPath);
            if (restoreRes.Success)
            {
                AnsiConsole.WriteLine($"Restored {restoreRes.RestoredFilesCount} files");
                successCount++;
            }
            else
            {
                AnsiConsole.WriteLine($"Extraction error: {restoreRes.ErrorMessage ?? ""}");
                failCount++;
            }
        }

        AnsiConsole.WriteLine($"\nCompleted! Succeeded: {successCount}, Failed: {failCount}.");
        if (failCount > 0)
        {
            throw new InvalidOperationException($"{failCount} game(s) failed to restore.");
        }
    }
}
