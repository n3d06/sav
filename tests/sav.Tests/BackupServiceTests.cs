using ludusavo.Services;

namespace sav.Tests;

public class BackupServiceTests
{
    [Fact]
    public async Task Archive_hash_cache_matches_and_invalidates_on_change()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sav-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "latest.zip");
        try
        {
            await File.WriteAllTextAsync(file, "first");
            var first = BackupService.GetArchiveHashCached(file);
            Assert.Equal(BackupService.ComputeSha256(file), first);
            Assert.True(File.Exists(file + ".sha256"));
            Assert.Equal(first, BackupService.GetArchiveHashCached(file)); // served from sidecar

            await File.WriteAllTextAsync(file, "second, longer content");
            var second = BackupService.GetArchiveHashCached(file);
            Assert.NotEqual(first, second);
            Assert.Equal(BackupService.ComputeSha256(file), second);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
