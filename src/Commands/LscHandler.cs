using ludusavo.Models;
using ludusavo.Services;

namespace ludusavo.Commands;

public class LscHandler
{
    private readonly IGoogleDriveService _driveService;
    private readonly IConfigService _configService;
    private readonly IManifestService _manifestService;
    private readonly IScannerService _scannerService;

    public LscHandler(
        IGoogleDriveService driveService,
        IConfigService configService,
        IManifestService manifestService,
        IScannerService scannerService)
    {
        _driveService = driveService;
        _configService = configService;
        _manifestService = manifestService;
        _scannerService = scannerService;
    }

    public async Task<List<RemoteGameMeta>> ExecuteAsync(bool showAll = false)
    {
        if (!_driveService.IsConfigured)
        {
            AnsiConsole.WriteLine("Google Drive is not configured! Please run 'sav config' or 'sav login' first.");
            return new List<RemoteGameMeta>();
        }

        List<RemoteGameMeta> list = new();
        List<DetectedGame> localGames = new();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Fetching save backups from Google Drive...", async ctx =>
            {
                var metasDict = await _driveService.GetAllRemoteMetasAsync(forceRefresh: true);
                list = metasDict.Values.OrderBy(m => m.GameName, StringComparer.OrdinalIgnoreCase).ToList();

                if (!_manifestService.IsLoaded)
                {
                    await _manifestService.LoadManifestAsync();
                }

                var allGames = _manifestService.GetAllGames();
                localGames = (await _scannerService.GetDetectedGamesAsync(allGames, forceRescan: false)).ToList();
            });

        var settings = _configService.Settings;
        var email = !string.IsNullOrWhiteSpace(settings.UserEmail) ? settings.UserEmail : "Google Drive";
        AnsiConsole.WriteLine($"On Google Drive ('sav' folder): {email}");

        if (list.Count == 0)
        {
            AnsiConsole.WriteLine("No save backups found in Google Drive.");
            AnsiConsole.MarkupLine("  [dim](use \"sav push\" to back up your first game save)[/]");
            return list;
        }

        var localById = localGames.ToDictionary(g => g.Id, g => g, StringComparer.OrdinalIgnoreCase);

        var toRestore = new List<(RemoteGameMeta meta, int index, string reason)>();
        var syncedOrLocalNewer = new List<(RemoteGameMeta meta, int index)>();

        for (int i = 0; i < list.Count; i++)
        {
            var m = list[i];
            int idx = i + 1;
            if (localById.TryGetValue(m.GameId, out var local))
            {
                var localLatestUtc = local.Files.Count > 0
                    ? local.Files.Max(f => f.LastWriteTime.ToUniversalTime())
                    : DateTime.MinValue;
                if (m.Timestamp.ToUniversalTime() > localLatestUtc.AddSeconds(5))
                {
                    toRestore.Add((m, idx, "newer"));
                }
                else
                {
                    syncedOrLocalNewer.Add((m, idx));
                }
            }
            else
            {
                toRestore.Add((m, idx, "not on PC"));
            }
        }

        if (toRestore.Count == 0)
        {
            AnsiConsole.WriteLine($"All {list.Count} save backups on Google Drive are up to date on this machine.");
            AnsiConsole.WriteLine("nothing to pull, local saves are clean.");
            if (!showAll && list.Count > 0)
            {
                AnsiConsole.MarkupLine($"  [dim](use \"sav lsc -a\" to view all {list.Count} cloud backups)[/]");
            }
            else if (showAll && list.Count > 0)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.WriteLine("Cloud backups:");
                foreach (var (m, idx) in syncedOrLocalNewer)
                {
                    var size = string.IsNullOrEmpty(m.FormattedSize) ? "-" : m.FormattedSize;
                    var date = ConsoleHelper.FormatDateTime(m.Timestamp);
                    AnsiConsole.MarkupLine($"\t[{idx}] {m.GameName} [dim]({size} - {date})[/]");
                }
            }
            return list;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("Changes available on Google Drive to restore:");
        AnsiConsole.MarkupLine("  [dim](use \"sav pull <#>\" or \"sav pull all\" to download)[/]");
        foreach (var (m, idx, reason) in toRestore)
        {
            var size = string.IsNullOrEmpty(m.FormattedSize) ? "-" : m.FormattedSize;
            var reasonTag = reason == "newer" ? "[cyan]newer[/]" : "[cyan]not on PC[/]";
            AnsiConsole.MarkupLine($"\t[{idx}] {reasonTag}: {m.GameName} [dim]({size})[/]");
        }

        if (syncedOrLocalNewer.Count > 0)
        {
            AnsiConsole.WriteLine();
            if (showAll)
            {
                AnsiConsole.WriteLine("Up to date on this machine:");
                foreach (var (m, idx) in syncedOrLocalNewer)
                {
                    var size = string.IsNullOrEmpty(m.FormattedSize) ? "-" : m.FormattedSize;
                    AnsiConsole.MarkupLine($"\t[{idx}] {m.GameName} [dim]({size})[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]({syncedOrLocalNewer.Count} backup{(syncedOrLocalNewer.Count > 1 ? "s on cloud are" : " on cloud is")} already up to date on this PC. Use \"sav lsc -a\" to view all)[/]");
            }
        }

        return list;
    }
}
