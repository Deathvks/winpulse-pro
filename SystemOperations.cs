using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WinPulseOptimizer;

internal enum SystemAction
{
    Profile, FactoryProfile, PowerScheme, Tweak, StartupApp, AutoStart, Ethernet, Restore, RestoreAutomatic, RestorePoint, DiskMaintenance
}

internal sealed record SystemRequest(SystemAction Action, string? Name = null, bool Enabled = false,
    UsageProfile Power = UsageProfile.GamingMax, TweakState? Values = null, Guid Adapter = default,
    EthernetOption Ethernet = default, bool UserScope = false, string Title = "", string PreviousProfile = "",
    string? AutomaticProfile = null, bool ReplaceAutomation = false, bool ReportProfile = false)
{
    internal bool RequiresAdministrator => Action switch
    {
        SystemAction.Tweak => Name is not ("SwGameMode" or "SwWindowedOpt" or "SwBgApps" or "SwFastUi" or "SwMouseAccel"),
        SystemAction.StartupApp => !UserScope,
        _ => true
    };
}

internal sealed record SystemResult(bool Changed = false, bool Restart = false, string Notice = "",
    ProfileSettingResult[]? Settings = null, string? Title = null, string? Profile = null, string? Error = null);

internal static class SystemOperations
{
    internal static async Task<SystemResult> ExecuteAsync(SystemRequest request)
    {
        if (request.RequiresAdministrator && !WindowsElevation.IsAdministrator)
            throw new UnauthorizedAccessException("Esta operación requiere permiso de administrador.");

        switch (request.Action)
        {
            case SystemAction.Restore:
                var restored = OptimizerEngine.RestoreSavedChange(request.Enabled);
                return new(Restart: restored.Restart, Notice: restored.Notice, Title: restored.Title, Profile: restored.Profile);
            case SystemAction.RestoreAutomatic:
                var automatic = OptimizerEngine.RestoreAutomaticProfile();
                return new(Restart: automatic.Restart, Notice: automatic.Notice, Profile: automatic.Profile);
            case SystemAction.RestorePoint:
                return new(Notice: await OptimizerEngine.CreateRestorePointAsync());
            case SystemAction.DiskMaintenance:
                int systemTempDeleted = await OptimizerEngine.CompleteDiskMaintenanceAsync();
                return new(Changed: systemTempDeleted > 0, Notice: systemTempDeleted.ToString(System.Globalization.CultureInfo.InvariantCulture));
            default:
                Action apply = request.Action switch
                {
                    SystemAction.Profile when request.Power is UsageProfile.GamingMax or UsageProfile.StudyWork or UsageProfile.ChillBattery
                        && request.Values != null => () => ApplyProfile(request.Power, request.Values),
                    SystemAction.FactoryProfile => OptimizerEngine.RestoreDefaults,
                    SystemAction.PowerScheme when request.Adapter != Guid.Empty => () =>
                        OptimizerEngine.SetActivePowerScheme(request.Adapter),
                    SystemAction.Tweak => GetTweak(request.Name, request.Enabled),
                    SystemAction.StartupApp => () => ApplyStartupSetting(request),
                    SystemAction.AutoStart => () => OptimizerEngine.SetAutoStartSilent(request.Enabled),
                    SystemAction.Ethernet when request.Adapter != Guid.Empty => () =>
                        OptimizerEngine.SetEthernetOption(request.Adapter, request.Ethernet, request.Enabled),
                    _ => throw new InvalidDataException("Operación de WinPulse desconocida o incompleta.")
                };
                var change = OptimizerEngine.ApplyVerifiedChange(request.Title, request.PreviousProfile, apply,
                    request.AutomaticProfile, request.ReplaceAutomation, request.ReportProfile);
                return new(change.Changed, change.Restart, change.Notice, change.Settings);
        }
    }

    private static void ApplyStartupSetting(SystemRequest request)
    {
        var item = OptimizerEngine.GetStartupApps().FirstOrDefault(candidate =>
            candidate.IsUserScope == request.UserScope && string.Equals(candidate.Name, request.Name, StringComparison.OrdinalIgnoreCase));
        if (item == null) throw new InvalidOperationException("La aplicación ya no aparece en el inicio de Windows.");
        OptimizerEngine.SetStartupAppEnabled(item, request.Enabled);
    }

    private static Action GetTweak(string? name, bool enabled) => name switch
    {
        "SwGameMode" => () => OptimizerEngine.SetGameMode(enabled),
        "SwHags" => () => OptimizerEngine.SetHags(enabled),
        "SwWindowedOpt" => () => OptimizerEngine.SetWindowedOptimizations(enabled),
        "SwHighResTimer" => () => OptimizerEngine.SetHighResTimer(enabled),
        "SwGameDvr" => () => OptimizerEngine.SetGameDvr(enabled),
        "SwMmcss" => () => OptimizerEngine.SetMmcssGaming(enabled),
        "SwForegroundPrio" => () => OptimizerEngine.SetForegroundPriority(enabled),
        "SwCoreParking" => () => OptimizerEngine.SetCoreParking(enabled),
        "SwSuspendServices" => () => OptimizerEngine.SetHeavyServicesSuspended(enabled),
        "SwUsbPcie" => () => OptimizerEngine.SetUsbAndPciePowerSave(enabled),
        "SwNagle" => () => OptimizerEngine.SetNagleAlgorithm(enabled),
        "SwBgApps" => () => OptimizerEngine.SetBackgroundApps(enabled),
        "SwFastUi" => () => OptimizerEngine.SetFastUiResponse(enabled),
        "SwMouseAccel" => () => OptimizerEngine.SetMouseAccelerationDisabled(enabled),
        _ => throw new InvalidDataException("El ajuste solicitado no es compatible.")
    };

    private static void ApplyProfile(UsageProfile profile, TweakState state)
    {
        var plan = OptimizerEngine.ApplyUsagePowerScheme(profile);
        OptimizerEngine.SetGameMode(state.GameMode);
        OptimizerEngine.SetHags(state.Hags);
        OptimizerEngine.SetWindowedOptimizations(state.WindowedOptimizations);
        OptimizerEngine.SetHighResTimer(state.HighResTimer);
        OptimizerEngine.SetGameDvr(state.DisableGameDvr);
        OptimizerEngine.SetMmcssGaming(state.MmcssGaming);
        OptimizerEngine.SetForegroundPriority(state.ForegroundPriority);
        OptimizerEngine.SetCoreParking(state.DisableCoreParking, profile == UsageProfile.StudyWork ? 25u : 5u,
            optional: true, planBlocked: !plan.HasValue);
        OptimizerEngine.SetHeavyServicesSuspended(state.SuspendHeavyServices);
        OptimizerEngine.SetUsbAndPciePowerSave(state.DisableUsbPciePowerSave, optional: true, planBlocked: !plan.HasValue);
        OptimizerEngine.SetNagleAlgorithm(state.DisableNagle, optional: true);
        OptimizerEngine.SetBackgroundApps(state.LimitBackgroundApps);
        OptimizerEngine.ApplyProfilePowerMode(profile, plan);
    }
}
