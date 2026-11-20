using System;
using Xunit;

namespace WinPulseOptimizer.Tests;

public class PowerProfileTests
{
    private static readonly Guid Balanced = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
    private static readonly Guid HighPerformance = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    private static readonly Guid Ultimate = Guid.Parse("e9a42b02-d5df-448d-aa00-03f14749eb61");
    private static readonly Guid PowerSaver = Guid.Parse("a1841308-3541-4fab-bc81-f71556f20b4a");

    private static WindowsPowerPlanItem[] StandardPlans(Guid active) =>
    [
        new(Balanced, "Equilibrado", active == Balanced),
        new(HighPerformance, "Alto rendimiento", active == HighPerformance),
        new(Ultimate, "Máximo rendimiento", active == Ultimate),
        new(PowerSaver, "Economizador", active == PowerSaver)
    ];

    [Fact]
    public void ChooseWritablePowerPlan_RespectsPolicyEnforcedPlan()
    {
        var customPlan = Guid.NewGuid();
        var chosen = OptimizerEngine.ChooseWritablePowerPlan(
            UsageProfile.StudyWork, customPlan, modernStandby: true, policyEnforced: true,
            balancedAvailable: false, StandardPlans(customPlan), supportsBattery: true);

        Assert.Equal(customPlan, chosen);
    }

    [Fact]
    public void ChooseWritablePowerPlan_DesktopVsLaptop_ChillProfile()
    {
        var schemes = StandardPlans(Balanced);

        // Laptop S3 elige Economizador para estirar batería.
        var laptopChill = OptimizerEngine.ChooseWritablePowerPlan(
            UsageProfile.ChillBattery, Balanced, modernStandby: false, policyEnforced: false,
            balancedAvailable: true, schemes, supportsBattery: true);
        Assert.Equal(PowerSaver, laptopChill);

        // Sobremesa S3 conserva Equilibrado para modo silencioso/fresco sin penalizar respuesta.
        var desktopChill = OptimizerEngine.ChooseWritablePowerPlan(
            UsageProfile.ChillBattery, Balanced, modernStandby: false, policyEnforced: false,
            balancedAvailable: true, schemes, supportsBattery: false);
        Assert.Equal(Balanced, desktopChill);
    }

    [Fact]
    public void ChooseWritablePowerPlan_GamingUsesUltimateWhenAvailable()
    {
        var schemes = StandardPlans(Balanced);
        var plan = OptimizerEngine.ChooseWritablePowerPlan(
            UsageProfile.GamingMax, Balanced, modernStandby: false, policyEnforced: false,
            balancedAvailable: true, schemes, supportsBattery: false);

        Assert.Equal(Ultimate, plan);
    }

    [Fact]
    public void DefaultProfileAdjustments_GamingHasHighResTimerDisabledByDefault()
    {
        var gaming = OptimizerEngine.DefaultProfileAdjustments(UsageProfile.GamingMax);
        Assert.True(gaming.GameMode);
        Assert.True(gaming.Hags);
        Assert.True(gaming.ForegroundPriority);
        Assert.True(gaming.DisableCoreParking);
        Assert.False(gaming.HighResTimer);
    }

    [Fact]
    public void ProfileLimitsAndCoreParking_AdaptToCurrentBatteryCapability()
    {
        bool hasBattery = HardwareMonitor.SupportsBatterySettings;
        var gamingLimits = OptimizerEngine.GetProfileProcessorLimits(UsageProfile.GamingMax);
        Assert.Equal((5u, 100u, 100u), gamingLimits);

        var chillLimits = OptimizerEngine.GetProfileProcessorLimits(UsageProfile.ChillBattery);
        Assert.Equal(5u, chillLimits.MinimumOnAc);
        Assert.Equal(hasBattery ? 80u : 85u, chillLimits.MaximumOnAc);

        uint studyCores = OptimizerEngine.GetMinimumActiveCoresPercent(UsageProfile.StudyWork, disableCoreParking: false);
        Assert.Equal(hasBattery ? 25u : 50u, studyCores);
        Assert.Equal(100u, OptimizerEngine.GetMinimumActiveCoresPercent(UsageProfile.StudyWork, disableCoreParking: true));

        uint pcieChill = OptimizerEngine.GetPciePowerSaveAc(UsageProfile.ChillBattery, disableOnAc: false);
        Assert.Equal(hasBattery ? 2u : 1u, pcieChill);
        Assert.Equal(0u, OptimizerEngine.GetPciePowerSaveAc(UsageProfile.ChillBattery, disableOnAc: true));
    }

    [Fact]
    public void FormatSettingStatus_ReportsVerifiedBlockedAndPending()
    {
        var tracked = new TrackedSetting("power", "target", "Test", "1", null);

        var verified = OptimizerEngine.FormatSettingStatus(tracked, "1", pending: false, inactivePlan: false);
        Assert.Equal(SettingState.Verified, verified.State);

        var different = OptimizerEngine.FormatSettingStatus(tracked, "0", pending: false, inactivePlan: false);
        Assert.Equal(SettingState.Different, different.State);

        var pending = OptimizerEngine.FormatSettingStatus(tracked, "0", pending: true, inactivePlan: false);
        Assert.Equal(SettingState.PendingRestart, pending.State);

        var blocked = OptimizerEngine.FormatSettingStatus(
            tracked with { Problem = "Bloqueado" }, "0", pending: false, inactivePlan: false);
        Assert.Equal(SettingState.Blocked, blocked.State);
    }
}
