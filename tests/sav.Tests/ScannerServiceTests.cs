using ludusavo.Models;
using ludusavo.Services;

namespace sav.Tests;

public class ScannerServiceTests
{
    private static ScannerService CreateScanner() => new(new StubConfigService());

    [Fact]
    public void Placeholder_requires_folder_boundary()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sibling = home + "2" + Path.DirectorySeparatorChar + "save.dat"; // e.g. C:\Users\me2\save.dat

        var result = CreateScanner().ToPlaceholderPath(sibling);

        Assert.DoesNotContain("<home>", result);
        Assert.Equal(sibling.Replace('\\', '/'), result, ignoreCase: true);
    }

    [Fact]
    public void Placeholder_maps_paths_inside_known_folders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var scanner = CreateScanner();

        Assert.Equal("<home>/MyGame/save.dat", scanner.ToPlaceholderPath(Path.Combine(home, "MyGame", "save.dat")));
        Assert.Equal("<home>", scanner.ToPlaceholderPath(home));
    }

    private sealed class StubConfigService : IConfigService
    {
        private readonly string _dir = Path.GetTempPath();
        public AppSettings Settings { get; } = new();
        public string RootDir => _dir;
        public string ConfigDir => _dir;
        public string DataDir => _dir;
        public string CacheDir => _dir;
        public string ManifestCachePath => Path.Combine(_dir, "manifest.json");
        public string SettingsFilePath => Path.Combine(_dir, "settings.json");
        public void SaveSettings(AppSettings newSettings) { }
        public void ReloadSettings() { }
    }
}
