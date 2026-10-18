using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ludusavo.Commands;
using ludusavo.Services;

namespace ludusavo;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        var services = new ServiceCollection();
        ConfigureServices(services);
        var serviceProvider = services.BuildServiceProvider();

        if (args.Length > 0)
        {
            if (args.Length > 1 && args.Skip(1).Any(a => a.Equals("--help", StringComparison.OrdinalIgnoreCase) || a.Equals("-h", StringComparison.OrdinalIgnoreCase)))
            {
                ShowCommandHelp(args[0]);
                return 0;
            }
            return await RunSingleCommandAsync(serviceProvider, args);
        }

        ShowHelp();
        return 0;
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<ICredentialManager, WindowsCredentialManager>();
        services.AddSingleton<IConfigService, ConfigService>();
        services.AddSingleton<IManifestService, ManifestService>();
        services.AddSingleton<IScannerService, ScannerService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<IRestoreService, RestoreService>();
        services.AddSingleton<IGoogleDriveService, GoogleDriveService>();
        services.AddSingleton<ISyncService, SyncService>();

        // Handlers
        services.AddTransient<StatusHandler>();
        services.AddTransient<ConfigHandler>();
        services.AddTransient<LsHandler>();
        services.AddTransient<LscHandler>();
        services.AddTransient<SamHandler>();
        services.AddTransient<PushHandler>();
        services.AddTransient<PullHandler>();
        services.AddTransient<AddHandler>();
        services.AddTransient<RemoveHandler>();
    }

    private static async Task<int> RunSingleCommandAsync(IServiceProvider sp, string[] args)
    {
        var cmd = args[0].ToLowerInvariant();
        var extraArgs = args.Length > 1 ? args.Skip(1).ToArray() : Array.Empty<string>();
        var firstArg = extraArgs.Length > 0 ? extraArgs[0] : null;

        try
        {
            switch (cmd)
            {
                case "ls":
                    var showAllLs = extraArgs.Any(a => a.Equals("-a", StringComparison.OrdinalIgnoreCase) || a.Equals("--all", StringComparison.OrdinalIgnoreCase));
                    var forceRescanLs = extraArgs.Any(a => a.Equals("--rescan", StringComparison.OrdinalIgnoreCase));
                    await sp.GetRequiredService<LsHandler>().ExecuteAsync(forceRescan: forceRescanLs, showAll: showAllLs);
                    break;

                case "lsc":
                    var showAllLsc = extraArgs.Any(a => a.Equals("-a", StringComparison.OrdinalIgnoreCase) || a.Equals("--all", StringComparison.OrdinalIgnoreCase));
                    await sp.GetRequiredService<LscHandler>().ExecuteAsync(showAll: showAllLsc);
                    break;

                case "scan":
                case "sam":
                    await sp.GetRequiredService<SamHandler>().ExecuteAsync();
                    break;

                case "push":
                    await sp.GetRequiredService<PushHandler>().ExecuteAsync(extraArgs);
                    break;

                case "pull":
                    await sp.GetRequiredService<PullHandler>().ExecuteAsync(extraArgs);
                    break;

                case "status":
                    await sp.GetRequiredService<StatusHandler>().ExecuteAsync();
                    break;

                case "login":
                    await sp.GetRequiredService<ConfigHandler>().LoginAsync();
                    break;

                case "logout":
                    await sp.GetRequiredService<ConfigHandler>().LogoutAsync();
                    break;

                case "config":
                    await sp.GetRequiredService<ConfigHandler>().ExecuteAsync(extraArgs);
                    break;

                case "add":
                    await sp.GetRequiredService<AddHandler>().ExecuteAsync();
                    break;

                case "remove":
                    await sp.GetRequiredService<RemoveHandler>().ExecuteAsync(firstArg);
                    break;

                case "version":
                case "--version":
                case "-v":
                    ShowVersion();
                    break;

                case "help":
                case "--help":
                case "-h":
                case "/?":
                    ShowHelp();
                    break;

                default:
                    AnsiConsole.WriteLine($"Unknown command: '{cmd}'.");
                    AnsiConsole.WriteLine("Type 'sav help' to see the list of supported commands.");
                    return 1;
            }
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteLine($"Error executing command '{cmd}': {ex.Message}");
            return 1;
        }
    }


    private static void ShowVersion()
    {
        var asm = typeof(Program).Assembly;
        var infoVer = asm.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVer))
        {
            var clean = infoVer.Split('+')[0].TrimStart('v');
            AnsiConsole.WriteLine($"sav version {clean}");
            return;
        }

        var ver = asm.GetName().Version?.ToString(3) ?? "1.0.0";
        AnsiConsole.WriteLine($"sav version {ver}");
    }

    private static void ShowHelp()
    {
        AnsiConsole.WriteLine("usage: sav [-v | --version] [-h | --help] <command> [<args>]");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("Getting started: sav config  ->  sav login  ->  sav scan  ->  sav push");
        AnsiConsole.WriteLine("Restore a backup with 'sav pull' (you will be asked before saves are overwritten).");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("These are common sav commands used in various situations:");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("examine games and save files");
        AnsiConsole.WriteLine("   ls         List game save status (use 'ls -a' for all, 'ls --rescan' to find new games)");
        AnsiConsole.WriteLine("   scan       Rescan all save games and manifest database");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("cloud synchronization (Google Drive)");
        AnsiConsole.WriteLine("   push       Compress and upload save games to Google Drive");
        AnsiConsole.WriteLine("   pull       Download and restore save games from Google Drive");
        AnsiConsole.WriteLine("   lsc        List cloud backups status (use 'lsc -a' for all)");
        AnsiConsole.WriteLine("   status     Check connection and cloud storage status");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("manage custom games");
        AnsiConsole.WriteLine("   add        Add a custom game to tracking list");
        AnsiConsole.WriteLine("   remove     Remove a custom game by index");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("account and configuration");
        AnsiConsole.WriteLine("   login      Log in to Google Drive via browser");
        AnsiConsole.WriteLine("   logout     Log out of current Google Drive account");
        AnsiConsole.WriteLine("   config     View or configure Google Client ID & Secret");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine("Use 'sav <command> --help' for command details.");
    }

    private static void ShowCommandHelp(string command)
    {
        switch (command.ToLowerInvariant())
        {
            case "ls":
                AnsiConsole.WriteLine("sav ls [-a | --all] [--rescan]");
                AnsiConsole.WriteLine("List detected local save games and their Google Drive sync status.");
                AnsiConsole.WriteLine("Use '--rescan' to search the full game database for newly detected saves.");
                break;
            case "lsc":
                AnsiConsole.WriteLine("sav lsc [-a | --all]");
                AnsiConsole.WriteLine("List save backups available on Google Drive.");
                break;
            case "push":
                AnsiConsole.WriteLine("sav push [game name | number ... | all]");
                AnsiConsole.WriteLine("Back up detected saves to Google Drive. Run 'sav push' to choose from a list.");
                AnsiConsole.WriteLine("Examples: sav push \"Stardew Valley\" | sav push 1 3 | sav push all (-a, --all)");
                break;
            case "pull":
                AnsiConsole.WriteLine("sav pull [game name | number ... | all]");
                AnsiConsole.WriteLine("Download and restore backups from Google Drive. Existing saves may be overwritten.");
                AnsiConsole.WriteLine("Examples: sav pull \"Stardew Valley\" | sav pull 1 3 | sav pull all (-a, --all)");
                break;
            case "scan":
            case "sam":
                AnsiConsole.WriteLine("sav scan");
                AnsiConsole.WriteLine("Refresh the game manifest and scan this PC for save files.");
                break;
            case "login":
                AnsiConsole.WriteLine("sav login");
                AnsiConsole.WriteLine("Sign in to Google Drive in your browser. Configure credentials first with 'sav config' if needed.");
                break;
            case "config":
                AnsiConsole.WriteLine("sav config [id <client-id> | secret <client-secret>]");
                AnsiConsole.WriteLine("Run without arguments for interactive Google Drive setup.");
                break;
            default:
                AnsiConsole.WriteLine($"sav {command}: see 'sav help' for available commands.");
                break;
        }
    }
}
