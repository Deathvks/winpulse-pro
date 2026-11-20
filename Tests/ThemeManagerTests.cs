using System.Windows.Media;
using Xunit;

namespace WinPulseOptimizer.Tests;

public class ThemeManagerTests
{
    [Theory]
    [InlineData("light", AppThemeMode.Light)]
    [InlineData("LIGHT", AppThemeMode.Light)]
    [InlineData("dark", AppThemeMode.Dark)]
    [InlineData("system", AppThemeMode.System)]
    [InlineData(null, AppThemeMode.System)]
    [InlineData("", AppThemeMode.System)]
    public void ParseMode_NormalizesAndDefaultsToSystem(string? raw, AppThemeMode expected)
    {
        Assert.Equal(expected, ThemeManager.ParseMode(raw));
    }

    [Fact]
    public void ResolveIsLightTheme_FollowsModeAndWindowsPreference()
    {
        Assert.True(ThemeManager.ResolveIsLightTheme(AppThemeMode.Light, 0));
        Assert.False(ThemeManager.ResolveIsLightTheme(AppThemeMode.Dark, 1));
        Assert.True(ThemeManager.ResolveIsLightTheme(AppThemeMode.System, 1));
        Assert.False(ThemeManager.ResolveIsLightTheme(AppThemeMode.System, 0));
        Assert.False(ThemeManager.ResolveIsLightTheme(AppThemeMode.System, null));
    }

    [Fact]
    public void Palettes_DefineIdenticalValidBrushKeys()
    {
        Assert.Equal(ThemeManager.DarkPalette.Keys, ThemeManager.LightPalette.Keys);
        foreach (var hex in ThemeManager.DarkPalette.Values)
            Assert.IsType<Color>(ColorConverter.ConvertFromString(hex));
        foreach (var hex in ThemeManager.LightPalette.Values)
            Assert.IsType<Color>(ColorConverter.ConvertFromString(hex));
    }
}
