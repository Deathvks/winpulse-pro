using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace WinPulseOptimizer;

internal sealed record ProfileVerificationResult(
    bool IsFullyActive,
    bool PendingRestart,
    string[] Differences
);

public static partial class OptimizerEngine
{
    internal static TweakState DefaultProfileAdjustments(UsageProfile profile) => profile switch
    {
        UsageProfile.GamingMax => new(true, true, true, true, true, true, true, true, true, true, true, true),
        UsageProfile.StudyWork => new(true, true, true, false, true, false, true, false, false, true, true, false),
        UsageProfile.ChillBattery => new(true, true, true, false, true, false, false, false, false, false, false, true),
        UsageProfile.FactoryDefault => new(true, true, true, false, false, false, false, false, false, false, false, false),
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };

    internal static ProfileVerificationResult VerifyProfileState(UsageProfile profile, TweakState values)
    {
        var differences = new List<string>();
        ChangeStore store;
        try { store = SystemChangeJournal.Load(); }
        catch { store = new ChangeStore(); }

        VerifyPowerConfiguration(store, profile, values, differences);
        VerifyPowerModes(store, profile, differences);
        VerifyProfileTweaks(store, values, differences);

        bool pendingRestart = HasPendingRestart(store,
            store.RestartBaselines.Select(row => new SavedSetting("registry", row.Target, row.Value)));
        return new ProfileVerificationResult(differences.Count == 0, pendingRestart, differences.ToArray());
    }

    internal static bool IsTweakInState(string tweakName, bool enabled)
    {
        ChangeStore store;
        try { store = SystemChangeJournal.Load(); }
        catch { store = new ChangeStore(); }

        return tweakName switch
        {
            "SwGameMode" => IsRegistryDwordEqual(Registry.CurrentUser, P("Software", "Microsoft", "GameBar"), "AutoGameModeEnabled", enabled ? 1 : 0),
            "SwHags" => IsRegistryDwordEqual(Registry.LocalMachine, P("SYSTEM", "CurrentControlSet", "Control", "GraphicsDrivers"), "HwSchMode", enabled ? 2 : 1),
            "SwWindowedOpt" => IsRegistryStringEqual(Registry.CurrentUser, P("Software", "Microsoft", "DirectX", "UserGpuPreferences"), "DirectXUserGlobalSettings",
                enabled ? "VRROptimizeEnable=1;SwapEffectUpgradeEnable=1;" : ""),
            "SwHighResTimer" => IsRegistryDwordEqual(Registry.LocalMachine, P("SYSTEM", "CurrentControlSet", "Control", "Session Manager", "kernel"), "GlobalTimerResolutionRequests", enabled ? 1 : 0),
            "SwGameDvr" => IsGameDvrInState(enabled),
            "SwMmcss" => IsMmcssInState(enabled),
            "SwForegroundPrio" => IsRegistryDwordEqual(Registry.LocalMachine, P("SYSTEM", "CurrentControlSet", "Control", "PriorityControl"), "Win32PrioritySeparation", enabled ? 26 : 2),
            "SwBgApps" => IsRegistryDwordEqual(Registry.CurrentUser, P("Software", "Microsoft", "Windows", "CurrentVersion", "BackgroundAccessApplications"), "GlobalUserDisabled", enabled ? 1 : 0),
            "SwFastUi" => IsRegistryStringEqual(Registry.CurrentUser, P("Control Panel", "Desktop"), "MenuShowDelay", enabled ? "20" : "400") &&
                IsRegistryStringEqual(Registry.CurrentUser, P("Control Panel", "Desktop", "WindowMetrics"), "MinAnimate", enabled ? "0" : "1"),
            "SwMouseAccel" => IsRegistryStringEqual(Registry.CurrentUser, P("Control Panel", "Mouse"), "MouseSpeed", enabled ? "0" : "1") &&
                IsRegistryStringEqual(Registry.CurrentUser, P("Control Panel", "Mouse"), "MouseThreshold1", enabled ? "0" : "6") &&
                IsRegistryStringEqual(Registry.CurrentUser, P("Control Panel", "Mouse"), "MouseThreshold2", enabled ? "0" : "10"),
            "SwTransparency" => IsRegistryDwordEqual(Registry.CurrentUser, P("Software", "Microsoft", "Windows", "CurrentVersion", "Themes", "Personalize"),
                "EnableTransparency", enabled ? 0 : 1),
            "SwNagle" => !NagleDiffers(enabled, requireAdapter: enabled),
            "SwSuspendServices" => !ServicesDiffer(store, enabled),
            "SwCoreParking" => IsCoreParkingInState(enabled),
            "SwUsbPcie" => IsUsbPcieInState(enabled),
            _ => false
        };
    }

    private static void VerifyPowerConfiguration(ChangeStore store, UsageProfile profile, TweakState values, List<string> differences)
    {
        Guid active;
        try { active = ActivePlan(); }
        catch (IOException)
        {
            differences.Add("Plan de energía activo");
            return;
        }

        Guid? enforced = ReadEnforcedPowerPlan();
        bool modernStandby = SupportsModernStandby();
        var schemes = GetPowerSchemes();
        bool balancedAvailable = !enforced.HasValue && schemes.Any(s => s.Id == Guid.Parse(GuidBalanced));
        Guid expectedPlan = ChooseWritablePowerPlan(profile, active, modernStandby, enforced.HasValue, balancedAvailable, schemes);
        string? expectedPlanDisplay = ReadReportValue(() => FormatReportPlan(expectedPlan));
        bool planBlocked = IsSettingBlockedInStore(store, "activePlan", "", expectedPlanDisplay);

        if (active != expectedPlan && !planBlocked)
            differences.Add("Plan de energía activo");

        Guid targetPlan = active != expectedPlan && planBlocked ? active : expectedPlan;
        if (targetPlan == Guid.Empty) return;

        var limits = GetProfileProcessorLimits(profile);
        if (RequiredPowerDiffers(store, targetPlan, "PROCTHROTTLEMIN", false, limits.MinimumOnAc) ||
            RequiredPowerDiffers(store, targetPlan, "PROCTHROTTLEMAX", false, limits.MaximumOnAc) ||
            RequiredPowerDiffers(store, targetPlan, "PROCTHROTTLEMAX", true, limits.MaximumOnBattery))
        {
            differences.Add("Límites de CPU");
        }

        uint minCoresAc = values.DisableCoreParking ? 100u : (profile == UsageProfile.StudyWork ? 25u : 5u);
        if (OptionalPowerDiffers(store, targetPlan, "CPMINCORES", false, minCoresAc) ||
            OptionalPowerDiffers(store, targetPlan, "CPMINCORES", true, 5u))
        {
            differences.Add("Núcleos activos");
        }

        if (OptionalPowerDiffers(store, targetPlan, "USB", false, values.DisableUsbPciePowerSave ? 0u : 1u) ||
            OptionalPowerDiffers(store, targetPlan, "PCIE", false, values.DisableUsbPciePowerSave ? 0u : 2u) ||
            OptionalPowerDiffers(store, targetPlan, "PCIE", true, 2u))
        {
            differences.Add("Ahorro de energía USB/PCIe");
        }

        if (profile == UsageProfile.GamingMax)
        {
            if (OptionalPowerDiffers(store, targetPlan, ProcessorBoostSetting, false, AggressiveProcessorBoost))
                differences.Add("Modo turbo de CPU");
            if (OptionalPowerDiffers(store, targetPlan, "DISKIDLE", false, 0u))
                differences.Add("Apagado de disco");
            if (OptionalPowerDiffers(store, targetPlan, "STANDBYIDLE", false, 0u))
                differences.Add("Tiempo de suspensión");
        }
        else
        {
            if (profile == UsageProfile.StudyWork && OptionalPowerDiffers(store, targetPlan, "STANDBYIDLE", false, 1800u))
                differences.Add("Tiempo de suspensión");
            else if (profile != UsageProfile.StudyWork && OriginalPowerDiffers(store, targetPlan, "STANDBYIDLE", false))
                differences.Add("Tiempo de suspensión");

            if (OriginalPowerDiffers(store, targetPlan, "DISKIDLE", false))
                differences.Add("Apagado de disco");
            if (OriginalPowerDiffers(store, targetPlan, ProcessorBoostSetting, false))
                differences.Add("Modo turbo de CPU");
        }
    }

    private static void VerifyPowerModes(ChangeStore store, UsageProfile profile, List<string> differences)
    {
        Guid expectedMode = profile switch
        {
            UsageProfile.GamingMax => new("ded574b5-45a0-4f42-8737-46345c09c238"),
            UsageProfile.ChillBattery => new("961cc777-2547-4f9d-8174-7d86181b8a7a"),
            _ => Guid.Empty
        };

        foreach (bool battery in new[] { false, true })
        {
            try
            {
                Guid? currentMode = ReadPowerMode(battery);
                if (currentMode.HasValue && currentMode.Value != expectedMode &&
                    !IsSettingBlockedInStore(store, "powerMode", battery ? "DC" : "AC", FormatReportMode(expectedMode)))
                {
                    if (!differences.Contains("Modo de energía de Windows"))
                        differences.Add("Modo de energía de Windows");
                }
            }
            catch (WindowsPowerException ex) when (IsOptionalPowerRestriction(ex.Code)) { }
        }
    }

    private static void VerifyProfileTweaks(ChangeStore store, TweakState values, List<string> differences)
    {
        if (!IsRegistryDwordEqual(Registry.CurrentUser, P("Software", "Microsoft", "GameBar"), "AutoGameModeEnabled", values.GameMode ? 1 : 0))
            differences.Add("Modo Juego");
        if (!IsRegistryDwordEqual(Registry.LocalMachine, P("SYSTEM", "CurrentControlSet", "Control", "GraphicsDrivers"), "HwSchMode", values.Hags ? 2 : 1))
            differences.Add("Aceleración de GPU (HAGS)");
        if (!IsRegistryStringEqual(Registry.CurrentUser, P("Software", "Microsoft", "DirectX", "UserGpuPreferences"), "DirectXUserGlobalSettings",
                values.WindowedOptimizations ? "VRROptimizeEnable=1;SwapEffectUpgradeEnable=1;" : ""))
            differences.Add("Juegos en ventana");
        if (!IsRegistryDwordEqual(Registry.LocalMachine, P("SYSTEM", "CurrentControlSet", "Control", "Session Manager", "kernel"), "GlobalTimerResolutionRequests", values.HighResTimer ? 1 : 0))
            differences.Add("Temporizador de alta precisión");
        if (!IsGameDvrInState(values.DisableGameDvr))
            differences.Add("Game DVR");
        if (!IsMmcssInState(values.MmcssGaming))
            differences.Add("Prioridad MMCSS");
        if (!IsRegistryDwordEqual(Registry.LocalMachine, P("SYSTEM", "CurrentControlSet", "Control", "PriorityControl"), "Win32PrioritySeparation", values.ForegroundPriority ? 26 : 2))
            differences.Add("Prioridad de ventana activa");
        if (!IsRegistryDwordEqual(Registry.CurrentUser, P("Software", "Microsoft", "Windows", "CurrentVersion", "BackgroundAccessApplications"), "GlobalUserDisabled", values.LimitBackgroundApps ? 1 : 0))
            differences.Add("Apps en segundo plano");
        if (NagleDiffers(values.DisableNagle, requireAdapter: false))
            differences.Add("Latencia TCP");
        if (ServicesDiffer(store, values.SuspendHeavyServices))
            differences.Add(values.SuspendHeavyServices ? "Servicios pesados en ejecución" : "Servicios pausados por reanudar");
    }

    private static bool IsSettingBlockedInStore(ChangeStore store, string type, string target, string? expectedDisplay) =>
        store.LatestSettings.Any(s => s.Type == type && SameTarget(s.Target, target) &&
            IsBlocked(s.Problem) && (expectedDisplay == null || s.Requested == expectedDisplay));

    private static bool IsPowerSettingBlocked(ChangeStore store, Guid plan, string setting, bool battery, uint expected)
    {
        string target = $"{plan}|{setting}|{(battery ? "DC" : "AC")}";
        string? expectedDisplay = ReadReportValue(() => FormatReportPower(setting, expected));
        return IsSettingBlockedInStore(store, "power", target, expectedDisplay);
    }

    private static bool RequiredPowerDiffers(ChangeStore store, Guid plan, string setting, bool battery, uint expected)
    {
        uint status = QueryPower(plan, setting, battery, out uint current);
        if (status == 0 && current == expected) return false;
        return !IsPowerSettingBlocked(store, plan, setting, battery, expected);
    }

    private static bool OptionalPowerDiffers(ChangeStore store, Guid plan, string setting, bool battery, uint expected)
    {
        uint status = QueryPower(plan, setting, battery, out uint current);
        if (status != 0 || current == expected) return false;
        return !IsPowerSettingBlocked(store, plan, setting, battery, expected);
    }

    private static bool OriginalPowerDiffers(ChangeStore store, Guid plan, string setting, bool battery)
    {
        string target = $"{plan}|{setting}|{(battery ? "DC" : "AC")}";
        var original = store.Original.FirstOrDefault(s => s.Type == "power" && SameTarget(s.Target, target));
        if (original == null || !uint.TryParse(original.Value, CultureInfo.InvariantCulture, out uint expected))
            return false;
        return OptionalPowerDiffers(store, plan, setting, battery, expected);
    }

    private static bool IsCoreParkingInState(bool disableOnAc)
    {
        try
        {
            Guid plan = ActivePlan();
            return QueryPower(plan, "CPMINCORES", false, out uint ac) == 0 && ac == (disableOnAc ? 100u : 5u) &&
                QueryPower(plan, "CPMINCORES", true, out uint dc) == 0 && dc == 5u;
        }
        catch { return false; }
    }

    private static bool IsUsbPcieInState(bool disableOnAc)
    {
        try
        {
            Guid plan = ActivePlan();
            return QueryPower(plan, "USB", false, out uint usbAc) == 0 && usbAc == (disableOnAc ? 0u : 1u) &&
                QueryPower(plan, "PCIE", false, out uint pcieAc) == 0 && pcieAc == (disableOnAc ? 0u : 2u) &&
                QueryPower(plan, "PCIE", true, out uint pcieDc) == 0 && pcieDc == 2u;
        }
        catch { return false; }
    }

    private static bool IsRegistryDwordEqual(RegistryKey root, string path, string name, int expected)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is int actual &&
                key.GetValueKind(name) == RegistryValueKind.DWord && actual == expected;
        }
        catch { return false; }
    }

    private static bool IsRegistryStringEqual(RegistryKey root, string path, string name, string expected)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string actual &&
                key.GetValueKind(name) == RegistryValueKind.String && actual == expected;
        }
        catch { return false; }
    }

    private static bool IsGameDvrInState(bool disable) =>
        IsRegistryDwordEqual(Registry.CurrentUser, P("System", "GameConfigStore"), "GameDVR_Enabled", disable ? 0 : 1) &&
        IsRegistryDwordEqual(Registry.CurrentUser, P("Software", "Microsoft", "Windows", "CurrentVersion", "GameDVR"), "AppCaptureEnabled", disable ? 0 : 1) &&
        IsRegistryDwordEqual(Registry.LocalMachine, P("SOFTWARE", "Policies", "Microsoft", "Windows", "GameDVR"), "AllowGameDVR", disable ? 0 : 1);

    private static bool IsMmcssInState(bool enable)
    {
        string sysProf = P("SOFTWARE", "Microsoft", "Windows NT", "CurrentVersion", "Multimedia", "SystemProfile");
        string games = P(sysProf, "Tasks", "Games");
        return IsRegistryDwordEqual(Registry.LocalMachine, sysProf, "SystemResponsiveness", enable ? 10 : 20) &&
            IsRegistryDwordEqual(Registry.LocalMachine, sysProf, "NetworkThrottlingIndex", enable ? unchecked((int)0xffffffff) : 10) &&
            IsRegistryDwordEqual(Registry.LocalMachine, games, "GPU Priority", enable ? 8 : 2) &&
            IsRegistryDwordEqual(Registry.LocalMachine, games, "Priority", enable ? 6 : 2) &&
            IsRegistryStringEqual(Registry.LocalMachine, games, "Scheduling Category", enable ? "High" : "Medium");
    }

    private static bool NagleDiffers(bool disable, bool requireAdapter)
    {
        try
        {
            using var interfaces = Registry.LocalMachine.OpenSubKey(InterfacesPath);
            if (interfaces == null) return requireAdapter;
            int matched = 0;
            foreach (string sub in interfaces.GetSubKeyNames())
            {
                using var nic = interfaces.OpenSubKey(sub);
                if (nic == null || !HasConfiguredIp(nic)) continue;
                matched++;
                object? ack = nic.GetValue("TcpAckFrequency", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                object? noDelay = nic.GetValue("TCPNoDelay", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                bool matches = disable
                    ? ack is 1 && nic.GetValueKind("TcpAckFrequency") == RegistryValueKind.DWord &&
                      noDelay is 1 && nic.GetValueKind("TCPNoDelay") == RegistryValueKind.DWord
                    : ack == null && noDelay == null;
                if (!matches) return true;
            }
            return requireAdapter && matched == 0;
        }
        catch { return true; }
    }

    private static bool ServicesDiffer(ChangeStore store, bool suspend)
    {
        try
        {
            if (suspend)
                return HeavyServices.Any(service => ReadService(service)?.State == "Running");

            return HeavyServices.Any(service =>
                WasServicePausedByWinPulse(store.Undo, service) &&
                ReadService(service) is { StartMode: not "Disabled", State: not "Running" });
        }
        catch { return true; }
    }
}
