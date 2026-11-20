using System;
using System.Collections.Generic;
using Xunit;

namespace WinPulseOptimizer.Tests;

public class MonitoringAndRulesTests
{
    [Fact]
    public void ParseNvidiaTelemetry_ParsesValidCsvLine()
    {
        var gpu = HardwareMonitor.ParseNvidiaTelemetry("NVIDIA GeForce RTX 4060 Laptop GPU, 58, 2048, 8192, 42, 1890");
        Assert.Equal("NVIDIA GeForce RTX 4060 Laptop GPU", gpu.Name);
        Assert.Equal(58, gpu.TempC);
        Assert.Equal(2.0, gpu.UsedVramGb);
        Assert.Equal(8.0, gpu.TotalVramGb);
        Assert.Equal(42, gpu.LoadPercent);
        Assert.Equal(1890, gpu.ClockMhz);
    }

    [Fact]
    public void ParseNvidiaTelemetry_ReturnsUnavailableOnMalformedOutput()
    {
        var gpu = HardwareMonitor.ParseNvidiaTelemetry("NVIDIA-SMI has failed");
        Assert.Null(gpu.Name);
        Assert.Null(gpu.TempC);
    }

    [Fact]
    public void GameRuleMatching_NormalizesAndRespectsDenyOverAllow()
    {
        Assert.Equal("cs2", HardwareMonitor.NormalizeExecutableName(@"  ""C:\Games\cs2.EXE""  "));

        var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mygame" };
        var deny = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cs2" };

        Assert.False(HardwareMonitor.MatchGameRule("cs2", allow, deny));
        Assert.True(HardwareMonitor.MatchGameRule("mygame", allow, deny));
        Assert.Null(HardwareMonitor.MatchGameRule("notepad", allow, deny));
    }

    [Fact]
    public void PathAndProcessProtectionChecks_RejectSystemAndGameDirs()
    {
        Assert.False(HardwareMonitor.CanAdjustBackgroundMemory("svchost"));
        Assert.False(HardwareMonitor.CanAdjustBackgroundMemory("cs2"));
        Assert.True(HardwareMonitor.CanAdjustBackgroundMemory("chrome"));

        Assert.True(HardwareMonitor.IsGameInstallPath(@"D:\SteamLibrary\steamapps\common\Game\game.exe"));
        Assert.False(HardwareMonitor.IsGameInstallPath(@"C:\Program Files\App\app.exe"));

        Assert.False(ProcessResourceMonitor.IsEligibleAppPath(@"C:\Windows\System32\notepad.exe"));
        Assert.False(ProcessResourceMonitor.IsEligibleAppPath(@"C:\Program Files\WindowsApps\App\app.exe"));
        Assert.True(ProcessResourceMonitor.IsEligibleAppPath(@"C:\Program Files\App\app.exe"));
    }

    [Fact]
    public void InterpretBatteryFlag_DistinguishesPresentAbsentAndUnknown()
    {
        Assert.False(HardwareMonitor.InterpretBatteryFlag(128)); // No system battery
        Assert.True(HardwareMonitor.InterpretBatteryFlag(1));    // High battery
        Assert.Null(HardwareMonitor.InterpretBatteryFlag(255));  // Unknown status
    }

    [Fact]
    public void ChooseAutomaticProfile_AppliesGameAndBatteryPriorityRules()
    {
        Assert.Null(MainWindow.ChooseAutomaticProfile(null, true, true, true));
        Assert.Equal("JUGAR", MainWindow.ChooseAutomaticProfile(true, gameDetected: true, allowGameOnBattery: false, chillOnBattery: true));
        Assert.Equal("CHILL", MainWindow.ChooseAutomaticProfile(false, gameDetected: true, allowGameOnBattery: false, chillOnBattery: true));
        Assert.Equal("JUGAR", MainWindow.ChooseAutomaticProfile(false, gameDetected: true, allowGameOnBattery: true, chillOnBattery: true));
    }

    [Fact]
    public void ReadRules_ParsesAndDeduplicatesValidEntries()
    {
        var parsed = MainWindow.ReadRules("cs2.exe\nVALORANT-Win64-Shipping; CS2.EXE \n");
        Assert.Equal(2, parsed.Length);
        Assert.Equal("cs2", parsed[0]);
        Assert.Equal("VALORANT-Win64-Shipping", parsed[1]);
    }

    [Fact]
    public void UiFormatters_RenderMemoryTrimAndProcessDeltaAccurately()
    {
        var result = new MemoryTrimResult(40, 12, 8UL * 1_073_741_824, 7UL * 1_073_741_824);
        string text = MainWindow.DescribeMemoryTrim(result);
        Assert.Contains("8,00", text.Replace('.', ','));
        Assert.Contains("1024", text.Replace(".", "").Replace(",", ""));

        Assert.Equal("3 más", MainWindow.FormatProcessDelta(10, 13));
        Assert.Equal("2 menos", MainWindow.FormatProcessDelta(10, 8));
        Assert.Equal("sin cambio", MainWindow.FormatProcessDelta(10, 10));
    }
}
