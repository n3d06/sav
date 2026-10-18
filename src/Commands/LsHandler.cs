using ludusavo.Models;
using ludusavo.Services;

namespace ludusavo.Commands;

public class LsHandler
{
    private readonly IManifestService _manifestService;
    private readonly IScannerService _scannerService;
    private readonly ISyncService _syncService;
    private readonly IConfigService _configService;
    private readonly IGoogleDriveService _driveService;

    public LsHandler(
        IManifestService manifestService,
        IScannerService scannerService,
        ISyncService syncService,
        IConfigService configService,
        IGoogleDriveService driveService)
    {
        _manifestService = manifestService;
        _scannerService = scannerService;
        _syncService = syncService;
        _configService = configService;
        _driveService = driveService;
    }

    public async Task<List<DetectedGame>> ExecuteAsync(bool forceRescan = false, bool showAll = false)
    {
        List<DetectedGame> games = new();
        Dictionary<string, RemoteGameMeta> remoteMetas = new();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(forceRescan ? "Rescanning all games on this machine..." : "Scanning save games...", async ctx =>
            {
                if (!_manifestService.IsLoaded)
                {
                    ctx.Status("Loading game database...");
                    await _manifestService.LoadManifestAsync();
                }

                ctx.Status("Scanning save game paths...");
                var allGames = _manifestService.GetAllGames();
                games = (await _scannerService.GetDetectedGamesAsync(allGames, forceRescan: forceRescan))
                    .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (_driveService.IsConfigured)
                {
                    ctx.Status("Comparing with Google Drive...");
                    await _syncService.CheckAllSyncStatusesAsync(games);
                    try
                    {
                        remoteMetas = await _driveService.GetAllRemoteMetasAsync();
                    }
                    catch { }
                }
            });

        var settings = _configService.Settings;
        if (_driveService.IsConfigured && !string.IsNullOrWhiteSpace(settings.UserEmail))
        {
            AnsiConsole.WriteLine($"On Google Drive: {settings.UserEmail}");
        }
        else
        {
            AnsiConsole.MarkupLine("Google Drive: [dim]Not connected (run 'sav login' to sync)[/]");
        }

        if (games.Count == 0 && remoteMetas.Count == 0)
        {
            AnsiConsole.WriteLine("No game saves detected on this machine.");
            AnsiConsole.MarkupLine("  [dim](use \"sav scan\" or \"sav add\" to track games)[/]");
            return games;
        }

        var toPush = new List<(DetectedGame game, int index)>();
        var untracked = new List<(DetectedGame game, int index)>();
        var toPull = new List<(DetectedGame game, int index)>();
        var synced = new List<(DetectedGame game, int index)>();

        for (int i = 0; i < games.Count; i++)
        {
            var g = games[i];
            int idx = i + 1;
            switch (g.Status)
            {
                case SyncStatus.LocalNewer:
                    toPush.Add((g, idx));
                    break;
                case SyncStatus.RemoteNewer:
                    toPull.Add((g, idx));
                    break;
                case SyncStatus.Synced:
                    synced.Add((g, idx));
                    break;
                case SyncStatus.LocalOnly:
                case SyncStatus.Unknown:
                default:
                    untracked.Add((g, idx));
                    break;
            }
        }

        var localGameIds = new HashSet<string>(games.Select(g => g.Id), StringComparer.OrdinalIgnoreCase);
        var remoteOnly = remoteMetas.Values
            .Where(r => !localGameIds.Contains(r.GameId))
            .OrderBy(r => r.GameName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool hasDifferences = toPush.Count > 0 || untracked.Count > 0 || toPull.Count > 0 || remoteOnly.Count > 0;

        if (!hasDifferences)
        {
            AnsiConsole.WriteLine("All save games are up to date with Google Drive.");
            AnsiConsole.WriteLine("nothing to push or pull, saves are clean.");
            if (!showAll && synced.Count > 0)
            {
                AnsiConsole.MarkupLine($"  [dim](use \"sav ls -a\" to list all {synced.Count} tracked games)[/]");
            }
            else if (showAll && synced.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.WriteLine("Tracked games:");
                foreach (var (g, idx) in synced)
                {
                    AnsiConsole.MarkupLine($"\t[{idx}] {g.Name} [dim]({g.TotalSizeFormatted})[/]");
                }
            }
            return games;
        }

        if (toPush.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine("Changes not backed up to Google Drive:");
            AnsiConsole.MarkupLine("  [dim](use \"sav push <#>\" to upload saves)[/]");
            foreach (var (g, idx) in toPush)
            {
                AnsiConsole.MarkupLine($"\t[{idx}] [yellow]modified[/]: {g.Name} [dim]({g.TotalSizeFormatted})[/]");
            }
        }

        if (untracked.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine("Untracked games (local only):");
            AnsiConsole.MarkupLine("  [dim](use \"sav push <#>\" to backup for the first time)[/]");
            foreach (var (g, idx) in untracked)
            {
                AnsiConsole.MarkupLine($"\t[{idx}] [red]untracked[/]: {g.Name} [dim]({g.TotalSizeFormatted})[/]");
            }
        }

        if (toPull.Count > 0 || remoteOnly.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.WriteLine("Changes available on Google Drive to restore:");
            AnsiConsole.MarkupLine("  [dim](use \"sav pull <#>\" or \"sav pull all\" to download)[/]");
            foreach (var (g, idx) in toPull)
            {
                AnsiConsole.MarkupLine($"\t[{idx}] [cyan]newer[/]: {g.Name} [dim]({g.TotalSizeFormatted})[/]");
            }
            foreach (var r in remoteOnly)
            {
                var size = string.IsNullOrEmpty(r.FormattedSize) ? "-" : r.FormattedSize;
                AnsiConsole.MarkupLine($"\t[cyan]cloud only:[/] {r.GameName} [dim]({size})[/]");
            }
        }

        if (synced.Count > 0)
        {
            AnsiConsole.WriteLine();
            if (showAll)
            {
                AnsiConsole.WriteLine("Up to date with Google Drive:");
                foreach (var (g, idx) in synced)
                {
                    AnsiConsole.MarkupLine($"\t[{idx}] {g.Name} [dim]({g.TotalSizeFormatted})[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]({synced.Count} game{(synced.Count > 1 ? "s are" : " is")} up to date with Google Drive. Use \"sav ls -a\" to view all)[/]");
            }
        }

        return games;
    }
}

