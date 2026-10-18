using ludusavo.Models;
using ludusavo.Services;

namespace ludusavo.Commands;

public class RemoveHandler
{
    private readonly IManifestService _manifestService;
    private readonly ISyncService _syncService;
    private readonly IGoogleDriveService _driveService;

    public RemoveHandler(IManifestService manifestService, ISyncService syncService, IGoogleDriveService driveService)
    {
        _manifestService = manifestService;
        _syncService = syncService;
        _driveService = driveService;
    }

    public async Task ExecuteAsync(string? target = null)
    {
        if (!_manifestService.IsLoaded)
        {
            await _manifestService.LoadManifestAsync();
        }

        var customGames = _manifestService.GetAllGames()
            .Where(g => g.Id.StartsWith("custom_", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (customGames.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No custom games found to remove.[/]");
            return;
        }

        // If command-line parameter is supplied
        if (!string.IsNullOrWhiteSpace(target))
        {
            if (int.TryParse(target, out int num) && num >= 1 && num <= customGames.Count)
            {
                await RemoveGameAsync(customGames[num - 1]);
                return;
            }

            var matched = customGames.FirstOrDefault(g => g.Name.Equals(target, StringComparison.OrdinalIgnoreCase) || g.Id.Equals(target, StringComparison.OrdinalIgnoreCase));
            if (matched != null)
            {
                await RemoveGameAsync(matched);
                return;
            }

            AnsiConsole.MarkupLine($"[red]No custom game matching '{target}' found.[/]");
            return;
        }

        for (int i = 0; i < customGames.Count; i++)
        {
            var g = customGames[i];
            AnsiConsole.WriteLine($"[{i + 1}] {g.Name} ({g.Id})");
        }
        AnsiConsole.WriteLine("[0] Cancel\n");

        var choice = AnsiConsole.Prompt("Enter custom game index to remove (or '0' to cancel):");

        choice = choice.Trim();

        if (choice == "0")
        {
            AnsiConsole.MarkupLine("Operation cancelled.");
            return;
        }

        if (int.TryParse(choice, out int idx) && idx >= 1 && idx <= customGames.Count)
        {
            await RemoveGameAsync(customGames[idx - 1]);
        }
        else
        {
            AnsiConsole.MarkupLine("[red]Invalid selection.[/]");
        }
    }

    private async Task RemoveGameAsync(GameEntry game)
    {
        var confirm = AnsiConsole.Confirm($"Are you sure you want to remove custom game '{game.Name}'? (Note: Save files on disk will NOT be deleted)", false);
        if (!confirm)
        {
            AnsiConsole.WriteLine("Deletion cancelled.");
            return;
        }

        (bool success, string? message)? manifestSync = null;
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync(_driveService.IsConfigured ? $"Removing {game.Name} and syncing to Google Drive..." : $"Removing {game.Name}...", async ctx =>
            {
                await _manifestService.RemoveCustomGameAsync(game.Id);
                if (_driveService.IsConfigured)
                {
                    try
                    {
                        manifestSync = await _syncService.UploadCustomManifestAsync();
                    }
                    catch (Exception ex) { manifestSync = (false, ex.Message); }
                }
            });

        AnsiConsole.WriteLine($"Successfully removed custom game '{game.Name}'.");
        if (manifestSync is { success: false })
        {
            AnsiConsole.MarkupLine($"[yellow]Removal was saved on this PC, but could not be synced to Google Drive: {manifestSync.Value.message}[/]");
        }
    }
}

