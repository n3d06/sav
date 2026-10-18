using ludusavo.Models;
using ludusavo.Services;

namespace ludusavo.Commands;

public class PushHandler
{
    private readonly IManifestService _manifestService;
    private readonly IScannerService _scannerService;
    private readonly IBackupService _backupService;
    private readonly ISyncService _syncService;
    private readonly IGoogleDriveService _driveService;

    public PushHandler(
        IManifestService manifestService,
        IScannerService scannerService,
        IBackupService backupService,
        ISyncService syncService,
        IGoogleDriveService driveService)
    {
        _manifestService = manifestService;
        _scannerService = scannerService;
        _backupService = backupService;
        _syncService = syncService;
        _driveService = driveService;
    }

    public async Task ExecuteAsync(params string[]? targets)
    {
        if (!_driveService.IsConfigured)
        {
            AnsiConsole.WriteLine("Google Drive is not configured! Please run 'sav config' or 'sav login' first.");
            throw new InvalidOperationException("Google Drive is not configured.");
        }

        List<DetectedGame> games = new();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Preparing list of detected games on this machine...", async ctx =>
            {
                if (!_manifestService.IsLoaded)
                {
                    await _manifestService.LoadManifestAsync();
                }
                var all = _manifestService.GetAllGames();
                games = (await _scannerService.GetDetectedGamesAsync(all, forceRescan: false))
                    .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });

        if (games.Count == 0)
        {
            AnsiConsole.WriteLine("No games with save files found to upload to Google Drive.");
            return;
        }

        var targetInput = targets != null && targets.Length > 0 ? string.Join(" ", targets).Trim() : null;

        // If target parameter is provided from CLI: e.g. push 1 3 5 or push all
        if (!string.IsNullOrWhiteSpace(targetInput))
        {
            if (targetInput.Equals("all", StringComparison.OrdinalIgnoreCase)
                || targetInput.Equals("a", StringComparison.OrdinalIgnoreCase)
                || targetInput.Equals("-a", StringComparison.OrdinalIgnoreCase)
                || targetInput.Equals("--all", StringComparison.OrdinalIgnoreCase))
            {
                await PushBatchAsync(games);
                return;
            }

            if (ConsoleHelper.TryParseSelectionIndices(targetInput, games.Count, out var indices))
            {
                var selected = indices.Select(i => games[i - 1]).ToList();
                if (selected.Count == 1)
                {
                    await PushSingleAsync(selected[0]);
                }
                else
                {
                    await PushBatchAsync(selected);
                }
                return;
            }

            var matches = games.Where(g => g.Name.Contains(targetInput, StringComparison.OrdinalIgnoreCase) || g.Id.Equals(targetInput, StringComparison.OrdinalIgnoreCase)).ToList();
            var matched = matches.Count == 1 ? matches[0] : null;
            if (matched != null)
            {
                await PushSingleAsync(matched);
                return;
            }

            if (matches.Count > 1)
            {
                AnsiConsole.WriteLine($"More than one game matches '{targetInput}'. Use a more specific name or choose a number:");
                foreach (var game in matches)
                    AnsiConsole.WriteLine($"  {games.IndexOf(game) + 1}. {game.Name}");
                throw new InvalidOperationException("Game name is ambiguous.");
            }
            AnsiConsole.WriteLine($"No game matching '{targetInput}' found. Try 'sav ls' to see detected games.");
            throw new InvalidOperationException($"No game matching '{targetInput}' found.");
        }

        int maxIndexWidth = $"[{games.Count}]".Length;
        int maxNameWidth = games.Max(g => g.Name.Length);
        int maxSizeWidth = games.Max(g => ConsoleHelper.FormatSize(g.TotalSizeBytes).Length);
        int maxFileWidth = games.Max(g => $"{g.FileCount} files".Length);

        for (int i = 0; i < games.Count; i++)
        {
            var g = games[i];
            var idxStr = $"[{i + 1}]".PadRight(maxIndexWidth);
            var nameStr = g.Name.PadRight(maxNameWidth);
            var sizeStr = ConsoleHelper.FormatSize(g.TotalSizeBytes).PadLeft(maxSizeWidth);
            var fileStr = (g.FileCount == 1 ? "1 file" : $"{g.FileCount} files").PadRight(maxFileWidth);

            AnsiConsole.WriteLine($"{idxStr}  {nameStr}  {sizeStr}  {fileStr}");
        }
        AnsiConsole.WriteLine($"{"[A]".PadRight(maxIndexWidth)}  Upload all games to Google Drive");
        AnsiConsole.WriteLine($"{"[0]".PadRight(maxIndexWidth)}  Cancel\n");

        var choice = AnsiConsole.Prompt("Enter game numbers to push (e.g. 1 3 5 or 'A' / '0'):");

        choice = choice.Trim();

        if (choice == "0")
        {
            AnsiConsole.WriteLine("Operation cancelled.");
            return;
        }

        if (choice.Equals("A", StringComparison.OrdinalIgnoreCase) || choice.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            await PushBatchAsync(games);
            return;
        }

        if (ConsoleHelper.TryParseSelectionIndices(choice, games.Count, out var selectedIndices))
        {
            var selected = selectedIndices.Select(i => games[i - 1]).ToList();
            if (selected.Count == 1)
            {
                await PushSingleAsync(selected[0]);
            }
            else
            {
                await PushBatchAsync(selected);
            }
        }
        else
        {
            AnsiConsole.WriteLine("Invalid selection.");
            throw new InvalidOperationException("Invalid game selection.");
        }
    }

    private async Task PushSingleAsync(DetectedGame game)
    {
        AnsiConsole.WriteLine($"Starting backup for game: {game.Name}");

        bool success = false;
        string? msg = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Compressing save files: {game.Name}...", async ctx =>
            {
                var syncRes = await _syncService.SyncGamesBatchToCloudAsync(new List<DetectedGame> { game }, statusText =>
                {
                    ctx.Status(statusText);
                });
                success = syncRes.success;
                msg = syncRes.message;
            });

        if (success)
        {
            AnsiConsole.WriteLine($"Successfully uploaded '{game.Name}' to Google Drive.");
        }
        else
        {
            AnsiConsole.WriteLine($"Upload failed: {msg ?? "Unknown error"}");
            throw new InvalidOperationException(msg ?? "Upload failed.");
        }
    }

    private async Task PushBatchAsync(List<DetectedGame> games)
    {
        AnsiConsole.WriteLine($"Starting backup for {games.Count} game(s) to Google Drive...");

        bool success = false;
        string? message = null;
        int successCount = 0;
        int failCount = 0;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Preparing game backups...", async ctx =>
            {
                var res = await _syncService.SyncGamesBatchToCloudAsync(games, statusText =>
                {
                    ctx.Status(statusText);
                });

                success = res.success;
                message = res.message;
                successCount = res.successCount;
                failCount = res.failCount;
            });

        if (success)
        {
            AnsiConsole.WriteLine($"Completed! Successfully uploaded {successCount} game(s) to Google Drive.");
            if (failCount > 0)
            {
                AnsiConsole.WriteLine($"Could not compress {failCount} game(s) due to file errors.");
                throw new InvalidOperationException($"{failCount} game(s) failed to back up.");
            }
        }
        else
        {
            AnsiConsole.WriteLine($"Upload failed: {message ?? "Unknown error"}");
            throw new InvalidOperationException(message ?? "Upload failed.");
        }
    }
}
