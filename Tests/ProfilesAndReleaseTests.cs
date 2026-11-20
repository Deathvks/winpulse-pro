using System;
using System.IO;
using System.Text.Json;
using Xunit;

namespace WinPulseOptimizer.Tests;

public class ProfilesAndReleaseTests
{
    [Fact]
    public void ParseNewerRelease_ReturnsReleaseOnlyWhenNewerStableSetupExists()
    {
        using var newerDoc = JsonDocument.Parse("""
            {
              "draft": false,
              "prerelease": false,
              "tag_name": "v1.2.0",
              "assets": [
                { "name": "WinPulseSetup.exe", "state": "uploaded", "size": 2500000 }
              ]
            }
            """);

        var release = GitHubReleaseChecker.ParseNewerRelease(newerDoc.RootElement, new Version(1, 1, 68));
        Assert.NotNull(release);
        Assert.Equal(new Version(1, 2, 0), release!.Version);

        var sameOrOlder = GitHubReleaseChecker.ParseNewerRelease(newerDoc.RootElement, new Version(1, 2, 0));
        Assert.Null(sameOrOlder);

        using var draftDoc = JsonDocument.Parse("""
            {
              "draft": true,
              "prerelease": false,
              "tag_name": "v1.3.0",
              "assets": [
                { "name": "WinPulseSetup.exe", "state": "uploaded", "size": 2500000 }
              ]
            }
            """);
        Assert.Null(GitHubReleaseChecker.ParseNewerRelease(draftDoc.RootElement, new Version(1, 1, 68)));
    }

    [Fact]
    public void GameProfileTransfer_RoundTripsExportAndRead()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"winpulse_test_{Guid.NewGuid():N}.json");
        try
        {
            var profile = new GameProfile(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "CS2 Competitivo",
                UsageProfile.GamingMax,
                OptimizerEngine.DefaultProfileAdjustments(UsageProfile.GamingMax),
                ["cs2"],
                PreferDedicatedGpu: true);

            GameProfileTransfer.Export(tempFile, [profile]);
            var loaded = GameProfileTransfer.Read(tempFile);

            var item = Assert.Single(loaded);
            Assert.Equal("CS2 Competitivo", item.Name);
            Assert.Equal(UsageProfile.GamingMax, item.PowerBase);
            Assert.Equal(["cs2"], item.Executables);
            Assert.True(item.PreferDedicatedGpu);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
