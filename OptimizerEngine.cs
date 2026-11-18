using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WinPulseOptimizer
{
    public enum UsageProfile { GamingMax, StudyWork, ChillBattery, FactoryDefault }

    public record PowerPlanDetails(
        string ActivePlanName,
        string ActivePlanGuid,
        string WindowsPowerOverlay,
        string EffectiveModeLabel,
        string DiskTimeout,
        string DesktopSlideShow,
        string SleepTimeout,
        string PcieAspm,
        string ProcessorPower,
        string ProcessorBoost,
        string DisplayTimeout,
        string BatteryAction
    );


    public class StartupAppItem
    {
        public string Name { get; set; } = "";
        public string Command { get; set; } = "";
        public string Scope { get; set; } = "";
        public bool IsUserScope { get; set; }
        public bool IsEnabled { get; set; }
    }

    public record TweakState(
        bool GameMode, bool Hags, bool WindowedOptimizations, bool HighResTimer,
        bool DisableGameDvr, bool MmcssGaming, bool ForegroundPriority, bool DisableCoreParking,
        bool SuspendHeavyServices, bool DisableUsbPciePowerSave, bool DisableNagle, bool LimitBackgroundApps
    );

    public sealed record WindowsPowerPlanItem(Guid Id, string Name, bool IsActive)
    {
        public string Label => IsActive ? $"{Name} (Activo)" : Name;
    }

    internal sealed record MemoryTrimResult(int Adjusted, int Skipped, ulong? BeforeBytes, ulong? AfterBytes);

    public static partial class OptimizerEngine
    {
        private static readonly string Sep = ((char)92).ToString();
        private static string P(params string[] parts) => string.Join(Sep, parts);

        private static readonly string[] HeavyServices = { "SysMain", "WSearch", "DiagTrack" };
        private const string LegacyMapsService = "MapsBroker";

        private const string GuidBalanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
        private const string GuidHighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        private const string GuidUltimatePerformance = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        private const string GuidPowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
        private const string ProcessorBoostSetting = "PERFBOOSTMODE";
        private const uint AggressiveProcessorBoost = 2;

        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr hwProc);

        [StructLayout(LayoutKind.Sequential)]
        private struct AnimationInfo
        {
            public uint cbSize;
            public int iMinAnimate;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref AnimationInfo pvParam, uint fWinIni);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);

        private static readonly string BackupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPulseOptimizer", "Backups");

        private static readonly Guid GamingPlan = new("a5982a3f-25c8-4442-bf64-857b1eb079e1");
        private static readonly Guid ChillPlan = new("a5982a3f-25c8-4442-bf64-857b1eb079e2");
        private static readonly Guid StudyPlan = new("a5982a3f-25c8-4442-bf64-857b1eb079e3");
        private const string StartupApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string InterfacesPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

        public static PowerPlanDetails GetPowerPlanDetails()
        {
            var (plan, name) = ReadActivePlanDetails();

            var (diskAc, diskDc) = QueryAcDc(plan, "DISKIDLE");
            var (bgAc, bgDc) = QueryAcDc(plan, "SLIDESHOW");
            var (sleepAc, sleepDc) = QueryAcDc(plan, "STANDBYIDLE");
            var (pcieAc, pcieDc) = QueryAcDc(plan, "PCIE");
            var (minAc, minDc) = QueryAcDc(plan, "PROCTHROTTLEMIN");
            var (maxAc, maxDc) = QueryAcDc(plan, "PROCTHROTTLEMAX");
            var (coresAc, coresDc) = QueryAcDc(plan, "CPMINCORES");
            var (boostAc, boostDc) = QueryAcDc(plan, ProcessorBoostSetting);
            var (dispAc, dispDc) = QueryAcDc(plan, "VIDEOIDLE");
            var (_, critLevel) = QueryAcDc(plan, "BATLEVELCRIT");
            var (_, lowLevel) = QueryAcDc(plan, "BATLEVELLOW");

            string overlay = ReadDisplayedPowerMode();
            string overlayText = overlay switch
            {
                "ded574b5-45a0-4f42-8737-46345c09c238" => "Mejor rendimiento",
                "961cc777-2547-4f9d-8174-7d86181b8a7a" => "Mejor eficiencia energética",
                "00000000-0000-0000-0000-000000000000" => "Equilibrado",
                _ => "No disponible"
            };

            return new PowerPlanDetails(name, plan.ToString(), overlayText, DescribePlan(plan, name),
                AcDc(FmtSeconds(diskAc), FmtSeconds(diskDc)),
                AcDc(FmtSlideShow(bgAc), FmtSlideShow(bgDc)),
                AcDc(FmtSeconds(sleepAc), FmtSeconds(sleepDc)),
                AcDc(FmtPcie(pcieAc), FmtPcie(pcieDc)),
                AcDc(FmtProcessor(minAc, maxAc, coresAc), FmtProcessor(minDc, maxDc, coresDc)),
                AcDc(FmtBoostMode(boostAc), FmtBoostMode(boostDc)),
                AcDc(FmtSeconds(dispAc), FmtSeconds(dispDc)),
                $"Aviso de batería baja: {FmtPercent(lowLevel)}  |  Nivel crítico: {FmtPercent(critLevel)}");
        }

        private static string AcDc(string ac, string dc) => $"Enchufado: {ac}  |  Batería: {dc}";
        private static string FmtPercent(uint? value) => value.HasValue ? $"{value}%" : "—";
        private static string FmtProcessor(uint? min, uint? max, uint? cores) =>
            $"{FmtPercent(min)}–{FmtPercent(max)} ({FmtPercent(cores)} núcleos)";
        private static string FmtBoostMode(uint? mode) => mode switch
        {
            0 => "Deshabilitado",
            1 => "Habilitado",
            AggressiveProcessorBoost => "Agresiva",
            3 => "Eficiencia habilitada",
            4 => "Eficiencia agresiva",
            5 => "Agresivo en garantizado",
            6 => "Agresivo eficiente en garantizado",
            null => "No disponible",
            _ => $"Valor {mode}"
        };
        private static string FmtSlideShow(uint? value) => value switch { 0 => "Disponible", 1 => "Pausado", _ => "—" };
        private static string FmtSeconds(uint? sec) => sec switch { null => "—", 0 => "Nunca", _ => $"{Math.Round(sec.Value / 60.0)} min" };
        private static string FmtPcie(uint? value) => value switch
        {
            0 => "Desactivado",
            1 => "Ahorro moderado",
            2 => "Ahorro máximo",
            _ => "—"
        };

        private static string DescribePlan(Guid id, string name) =>
            id == GamingPlan ? "Jugar (Máximo Rendimiento)" :
            id == ChillPlan ? "Estar Chill (Silencioso / Ahorro)" :
            id == StudyPlan ? "Estudiar / Trabajar (Equilibrado)" : name;

        public static string GetActivePowerSchemeName()
        {
            var (id, name) = ReadActivePlanDetails();
            return DescribePlan(id, name);
        }

        private static (Guid Id, string Name) ReadActivePlanDetails()
        {
            try
            {
                var plan = ActivePlan();
                return (plan, ReadPlanName(plan) ?? "Plan sin nombre");
            }
            catch (IOException) { return (Guid.Empty, "Plan no disponible"); }
        }

        private static string ReadDisplayedPowerMode()
        {
            bool battery = !HardwareMonitor.IsAcOnline();
            try
            {
                return ReadPowerMode(battery)?.ToString() ??
                    GetString(Registry.LocalMachine, PowerModePath,
                        battery ? "ActiveOverlayDcPowerScheme" : "ActiveOverlayAcPowerScheme", "").ToLowerInvariant();
            }
            catch (IOException) { return ""; }
        }

        private static (uint? Ac, uint? Dc) QueryAcDc(Guid plan, string setting)
        {
            if (plan == Guid.Empty) return (null, null);
            uint? ac = QueryPower(plan, setting, false, out var acValue) == 0 ? acValue : null;
            uint? dc = QueryPower(plan, setting, true, out var dcValue) == 0 ? dcValue : null;
            return (ac, dc);
        }

        internal static (uint MinimumOnAc, uint MaximumOnAc, uint MaximumOnBattery) GetProfileProcessorLimits(UsageProfile profile) =>
            profile switch
            {
                UsageProfile.GamingMax => (100, 100, 100),
                UsageProfile.ChillBattery => (5, 80, 70),
                _ => (5, 100, 100)
            };

        internal static Guid? ReadEnforcedPowerPlan()
        {
            const string policyPath = @"SOFTWARE\Policies\Microsoft\Power\PowerSettings";
            string value = GetString(Registry.LocalMachine, policyPath, "ActivePowerScheme", "");
            return Guid.TryParse(value, out var plan) && plan != Guid.Empty ? plan : null;
        }

        private static bool _strictPlanLimitsCheck;

        internal static Guid ChooseWritablePowerPlan(UsageProfile profile, Guid active, bool modernStandby,
            bool policyEnforced, bool balancedAvailable, WindowsPowerPlanItem[]? availableSchemes = null)
        {
            if (policyEnforced) return active;
            var schemes = availableSchemes ?? GetPowerSchemes();
            bool Has(Guid id) => schemes.Any(s => s.Id == id);
            Guid? ByName(params string[] names)
            {
                foreach (string name in names)
                {
                    var match = schemes.FirstOrDefault(s => string.Equals(s.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));
                    if (match != null) return match.Id;
                }
                return null;
            }

            if (profile == UsageProfile.GamingMax)
            {
                var ultimate = Guid.Parse(GuidUltimatePerformance);
                if (Has(ultimate)) return ultimate;
                if (ByName("Máximo rendimiento", "Ultimate Performance") is Guid customUltimate) return customUltimate;
                if (!modernStandby)
                {
                    var highPerf = Guid.Parse(GuidHighPerformance);
                    if (Has(highPerf)) return highPerf;
                    if (ByName("Alto rendimiento", "High Performance") is Guid customHigh) return customHigh;
                }
            }
            else if (!modernStandby && profile == UsageProfile.ChillBattery)
            {
                var saver = Guid.Parse(GuidPowerSaver);
                if (Has(saver)) return saver;
                if (ByName("Economizador", "Power saver") is Guid customSaver) return customSaver;
            }

            var balanced = Guid.Parse(GuidBalanced);
            return balancedAvailable && Has(balanced) ? balanced : active;
        }

        public static void SetActivePowerScheme(Guid planId)
        {
            if (ReadEnforcedPowerPlan().HasValue)
                throw new InvalidOperationException("Windows fija el plan de energía mediante una directiva y no permite cambiarlo.");
            if (!PlanExists(planId))
                throw new IOException("El plan de energía seleccionado ya no está disponible en Windows.");
            try
            {
                ActivatePlan(planId);
            }
            catch (WindowsPowerException ex) when (ex.Code is 1260 or 50 or 5)
            {
                throw new InvalidOperationException(
                    $"Windows no permitió activar ese plan de energía (código {ex.Code}). En equipos con Modern Standby S0 o directiva activa, Windows restringe el cambio de plan.",
                    ex);
            }
        }

        public static Guid? ApplyUsagePowerScheme(UsageProfile profile)
        {
            Guid active = ActivePlan();
            Guid? enforced = ReadEnforcedPowerPlan();
            bool modernStandby = SupportsModernStandby();
            var schemes = GetPowerSchemes();
            bool balancedAvailable = !enforced.HasValue && schemes.Any(s => s.Id == Guid.Parse(GuidBalanced));
            Guid plan = ChooseWritablePowerPlan(profile, active, modernStandby, enforced.HasValue, balancedAvailable, schemes);
            if (enforced.HasValue)
                _operationNotices?.Add($"La directiva de Windows mantiene el plan {ReadPlanName(active) ?? active.ToString()}.");
            else if ((modernStandby || profile is UsageProfile.FactoryDefault or UsageProfile.StudyWork) && !balancedAvailable)
                _operationNotices?.Add("Equilibrado no está disponible. Se conserva el plan actual.");
            bool switched = TryActivateProfilePlan(plan, out bool canReactivate);
            if (!switched)
            {
                plan = active;
                TryActivateProfilePlan(plan, out canReactivate);
            }
            _strictPlanLimitsCheck = canReactivate;
            bool planBlocked = false;

            bool optionalLimits = !canReactivate;
            var limits = GetProfileProcessorLimits(profile);
            if (profile == UsageProfile.GamingMax)
                WritePower(plan, "PROCTHROTTLEMAX", false, limits.MaximumOnAc, optional: optionalLimits, planBlocked: planBlocked);
            WritePower(plan, "PROCTHROTTLEMIN", false, limits.MinimumOnAc, optional: optionalLimits, planBlocked: planBlocked);
            if (profile != UsageProfile.GamingMax)
                WritePower(plan, "PROCTHROTTLEMAX", false, limits.MaximumOnAc, optional: optionalLimits, planBlocked: planBlocked);
            WritePower(plan, "PROCTHROTTLEMAX", true, limits.MaximumOnBattery, optional: optionalLimits, planBlocked: planBlocked);

            // Profiles reuse existing plans, so restore timeouts left by a prior profile.
            if (profile != UsageProfile.GamingMax)
            {
                RestoreOriginalPowerIndex(plan, "DISKIDLE", battery: false, planBlocked: planBlocked);
                RestoreOriginalPowerIndex(plan, ProcessorBoostSetting, battery: false, planBlocked: planBlocked);
            }
            if (profile is UsageProfile.ChillBattery or UsageProfile.FactoryDefault)
                RestoreOriginalPowerIndex(plan, "STANDBYIDLE", battery: false, planBlocked: planBlocked);

            bool boostChanged = false;
            switch (profile)
            {
                case UsageProfile.GamingMax:
                    boostChanged = WritePower(plan, ProcessorBoostSetting, false, AggressiveProcessorBoost, optional: true, planBlocked: planBlocked);
                    WritePower(plan, "DISKIDLE", false, 0, optional: true, planBlocked: planBlocked);
                    WritePower(plan, "STANDBYIDLE", false, 0, optional: true, planBlocked: planBlocked);
                    break;
                case UsageProfile.StudyWork:
                    WritePower(plan, "STANDBYIDLE", false, 1800, optional: true, planBlocked: planBlocked);
                    break;
            }

            if (canReactivate) ActivatePlan(plan);
            if (boostChanged && (QueryPower(plan, ProcessorBoostSetting, false, out uint boost) != 0 ||
                boost != AggressiveProcessorBoost))
                _operationNotices?.Add("Windows no mantuvo el modo turbo Agresiva después de activar el plan.");
            return plan;
        }

        internal static void ApplyProfilePowerMode(UsageProfile profile, Guid? plan)
        {
            Guid mode = profile switch
            {
                UsageProfile.GamingMax => new("ded574b5-45a0-4f42-8737-46345c09c238"),
                UsageProfile.ChillBattery => new("961cc777-2547-4f9d-8174-7d86181b8a7a"),
                _ => Guid.Empty
            };
            WritePowerMode(false, mode, optional: true);
            WritePowerMode(true, mode, optional: true);

            if (plan.HasValue && (ActivePlan() != plan.Value || (_strictPlanLimitsCheck && !HasProfileProcessorLimits(profile, plan.Value))))
                throw new IOException("Windows no mantuvo el plan o los límites de CPU solicitados después de activarlo.");
        }

        internal static bool HasProfileProcessorLimits(UsageProfile profile, Guid plan)
        {
            var limits = GetProfileProcessorLimits(profile);
            return QueryPower(plan, "PROCTHROTTLEMIN", false, out uint minimum) == 0 && minimum == limits.MinimumOnAc &&
                QueryPower(plan, "PROCTHROTTLEMAX", false, out uint maximum) == 0 && maximum == limits.MaximumOnAc &&
                QueryPower(plan, "PROCTHROTTLEMAX", true, out uint batteryMaximum) == 0 && batteryMaximum == limits.MaximumOnBattery;
        }

        internal static string GetProfilePowerSummary(UsageProfile profile)
        {
            var (plan, name) = ReadActivePlanDetails();
            if (plan == Guid.Empty) return "Windows no informó el plan activo.";
            if (!HasProfileProcessorLimits(profile, plan)) return $"Plan activo: {name}. Límites de CPU del perfil sin verificar.";
            var limits = GetProfileProcessorLimits(profile);
            return $"Plan activo: {name}. CPU configurada al {limits.MinimumOnAc}–{limits.MaximumOnAc}% enchufado y máximo {limits.MaximumOnBattery}% en batería.";
        }

        public static TweakState ReadCurrentState()
        {
            bool gameMode = GetDword(Registry.CurrentUser, P("Software", "Microsoft", "GameBar"), "AutoGameModeEnabled", 1) == 1;
            bool hags = GetDword(Registry.LocalMachine, P("SYSTEM", "CurrentControlSet", "Control", "GraphicsDrivers"), "HwSchMode", 2) == 2;
            string gpuPrefs = GetString(Registry.CurrentUser, P("Software", "Microsoft", "DirectX", "UserGpuPreferences"), "DirectXUserGlobalSettings", "");
            bool windowedOpt = gpuPrefs.Contains("SwapEffectUpgradeEnable=1");
            bool hiResTimer = GetDword(Registry.LocalMachine, P("SYSTEM", "CurrentControlSet", "Control", "Session Manager", "kernel"), "GlobalTimerResolutionRequests", 0) == 1;

            bool disableDvr = GetDword(Registry.CurrentUser, P("System", "GameConfigStore"), "GameDVR_Enabled", 1) == 0;
            bool mmcss = GetDword(Registry.LocalMachine, P("SOFTWARE", "Microsoft", "Windows NT", "CurrentVersion", "Multimedia", "SystemProfile"), "SystemResponsiveness", 20) <= 10;
            bool fgPrio = GetDword(Registry.LocalMachine, P("SYSTEM", "CurrentControlSet", "Control", "PriorityControl"), "Win32PrioritySeparation", 2) == 26;
            bool bgApps = GetDword(Registry.CurrentUser, P("Software", "Microsoft", "Windows", "CurrentVersion", "BackgroundAccessApplications"), "GlobalUserDisabled", 0) == 1;

            Guid plan = ActivePlan();
            var (coresAc, _) = QueryAcDc(plan, "CPMINCORES");
            bool noParking = coresAc == 100;

            var (usbAc, _) = QueryAcDc(plan, "USB");
            var (pcieAc, _) = QueryAcDc(plan, "PCIE");
            bool noUsbSleep = usbAc == 0 && pcieAc == 0;

            bool noNagle = CheckNagleDisabled();
            bool servicesStopped = CheckHeavyServicesStopped();

            return new TweakState(gameMode, hags, windowedOpt, hiResTimer, disableDvr, mmcss, fgPrio, noParking, servicesStopped, noUsbSleep, noNagle, bgApps);
        }

        public static System.Collections.Generic.List<StartupAppItem> GetStartupApps()
        {
            var list = new System.Collections.Generic.List<StartupAppItem>();
            ReadStartupScope(Registry.CurrentUser, "Usuario", true, list);
            ReadStartupScope(Registry.LocalMachine, "Sistema", false, list);
            return list.OrderBy(x => x.Name).ToList();
        }

        private static void ReadStartupScope(RegistryKey root, string scopeLabel, bool isUser, System.Collections.Generic.List<StartupAppItem> list)
        {
            try
            {
                string runPath = P("Software", "Microsoft", "Windows", "CurrentVersion", "Run");
                string approvedPath = StartupApprovedPath;
                using var runKey = root.OpenSubKey(runPath);
                if (runKey == null) return;
                using var approvedKey = root.OpenSubKey(approvedPath);

                foreach (string valName in runKey.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(valName)) continue;
                    string cmd = runKey.GetValue(valName)?.ToString() ?? "";
                    bool enabled = true;
                    if (approvedKey?.GetValue(valName) is byte[] bytes && bytes.Length > 0)
                    {
                        // En Windows 11, byte[0] == 2 o 6 significa Habilitado; 3 o 7 significa Deshabilitado
                        enabled = (bytes[0] % 2 == 0);
                    }
                    list.Add(new StartupAppItem
                    {
                        Name = valName,
                        Command = cmd.Length > 68 ? cmd.Substring(0, 65) + "..." : cmd,
                        Scope = scopeLabel,
                        IsUserScope = isUser,
                        IsEnabled = enabled
                    });
                }
            }
            catch { }
        }

        public static void SetStartupAppEnabled(StartupAppItem item, bool enable)
        {
            var root = item.IsUserScope ? Registry.CurrentUser : Registry.LocalMachine;
            string path = StartupApprovedPath;
            byte[] data = new byte[12];
            data[0] = (byte)(enable ? 2 : 3);
            WriteRegistry(root, path, item.Name, data, RegistryValueKind.Binary);
        }

        private const string AutoStartTask = "WinPulseProAutoStart";

        internal static void UpdateScheduledLaunchPath(string executable)
        {
            string target = executable.Replace("'", "''");
            RunPowerShell($$"""
                $target = '{{target}}'
                $task = Get-ScheduledTask | Where-Object { $_.TaskPath -eq '\' -and $_.TaskName -eq '{{AutoStartTask}}' }
                if ($null -eq $task) { return }

                $actions = @($task.Actions)
                if ($actions.Count -ne 1 -or $actions[0].Arguments -ne '--minimized' -or
                    [IO.Path]::GetFileName(([string]$actions[0].Execute).Trim([char]34)) -ne 'WinPulseOptimizer.exe') {
                    throw 'La tarea de inicio tiene una acción distinta de la que configura WinPulse.'
                }
                if ($actions[0].Execute -eq $target -and $task.Principal.RunLevel -eq 'Limited') { return }

                $enabled = [bool]$task.Settings.Enabled
                $action = New-ScheduledTaskAction -Execute $target -Argument '--minimized' -WorkingDirectory (Split-Path -LiteralPath $target)
                $principal = $task.Principal
                $principal.RunLevel = 'Limited'
                Set-ScheduledTask -TaskName '{{AutoStartTask}}' -TaskPath '\' -Action $action -Principal $principal | Out-Null
                $updated = Get-ScheduledTask -TaskName '{{AutoStartTask}}' -TaskPath '\'
                if ([bool]$updated.Settings.Enabled -ne $enabled) {
                    if ($enabled) { Enable-ScheduledTask -InputObject $updated | Out-Null }
                    else { Disable-ScheduledTask -InputObject $updated | Out-Null }
                    $updated = Get-ScheduledTask -TaskName '{{AutoStartTask}}' -TaskPath '\'
                }
                if (@($updated.Actions).Count -ne 1 -or $updated.Actions[0].Execute -ne $target -or
                    $updated.Actions[0].Arguments -ne '--minimized' -or $updated.Principal.RunLevel -ne 'Limited' -or
                    [bool]$updated.Settings.Enabled -ne $enabled) {
                    throw 'Windows no confirmó la nueva ruta de la tarea de inicio.'
                }
                """);
        }

        internal static void RemoveInstalledAutoStart(string executable)
        {
            string target = executable.Replace("'", "''");
            RunPowerShell($$"""
                $target = '{{target}}'
                $task = Get-ScheduledTask | Where-Object { $_.TaskPath -eq '\' -and $_.TaskName -eq '{{AutoStartTask}}' }
                if ($null -eq $task) { return }
                $actions = @($task.Actions)
                if ($actions.Count -ne 1 -or $actions[0].Arguments -ne '--minimized' -or
                    ([string]$actions[0].Execute).Trim([char]34) -ne $target) { return }
                Unregister-ScheduledTask -InputObject $task -Confirm:$false
                if (Get-ScheduledTask | Where-Object { $_.TaskPath -eq '\' -and $_.TaskName -eq '{{AutoStartTask}}' }) {
                    throw 'Windows no confirmó que se quitó la tarea de inicio de WinPulse.'
                }
                """);
        }

        private static string ReadAutoStart()
        {
            string result = RunPowerShell("$task = Get-ScheduledTask | " +
                $"Where-Object {{ $_.TaskPath -eq '\\' -and $_.TaskName -eq '{AutoStartTask}' }}; " +
                "if ($null -eq $task) { 'Absent' } elseif ($task.State -eq 'Disabled') { 'Disabled' } else { 'Enabled' }");
            return result.Trim() switch
            {
                "Absent" or "Disabled" or "Enabled" => result.Trim(),
                _ => throw new IOException("Windows no devolvió el estado de la tarea de inicio.")
            };
        }

        public static bool? IsAutoStartEnabled()
        {
            try { return ReadAutoStart() == "Enabled"; }
            catch { return null; }
        }

        public static void SetAutoStartSilent(bool enable)
        {
            string current = ReadAutoStart();
            TrackProfileSetting("task", AutoStartTask, "Iniciar WinPulse con Windows",
                current == "Enabled" ? "Activado" : "Desactivado", enable ? "Activado" : "Desactivado", null);
            if ((current == "Enabled") == enable) return;
            Remember("task", AutoStartTask, current);
            RestoreAutoStart(enable ? "Enabled" : "Disabled");
        }

        private static void RestoreAutoStart(string desired)
        {
            string current = ReadAutoStart();
            if (current == desired) return;
            if (desired == "Absent")
                RunCommand("schtasks.exe", $"/delete /tn {AutoStartTask} /f");
            else if (current == "Absent" && desired == "Enabled")
            {
                string exePath = Environment.ProcessPath ?? throw new IOException("No se encontró el ejecutable de WinPulse.");
                RunChecked("schtasks.exe", "/create", "/tn", AutoStartTask, "/tr", $"\"{exePath}\" --minimized",
                    "/sc", "onlogon", "/rl", "limited", "/ru", $@"{Environment.UserDomainName}\{Environment.UserName}");
            }
            else if (current != "Absent" && desired is "Enabled" or "Disabled")
            {
                if (desired == "Enabled")
                    UpdateScheduledLaunchPath(Environment.ProcessPath ?? throw new IOException("No se encontró el ejecutable de WinPulse."));
                RunCommand("schtasks.exe", $"/change /tn {AutoStartTask} /{(desired == "Enabled" ? "enable" : "disable")}");
            }
            else
                throw new IOException("Ya no existe la tarea de inicio que se quería restaurar.");

            if (ReadAutoStart() != desired)
                throw new IOException("Windows no confirmó el cambio de inicio automático.");
        }

        public static (int Trimmed, int Skipped) TrimMemoryWorkingSets()
        {
            int trimmed = 0, skipped = 0;
            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    try
                    {
                        if (proc.Id <= 4) continue;
                        if (EmptyWorkingSet(proc.Handle)) trimmed++;
                        else skipped++;
                    }
                    catch { skipped++; }
                }
            }
            return (trimmed, skipped);
        }

        internal static MemoryTrimResult MeasureMemoryTrim()
        {
            ulong? before = HardwareMonitor.ReadUsedMemoryBytes();
            var (adjusted, skipped) = TrimMemoryWorkingSets();
            return new(adjusted, skipped, before, HardwareMonitor.ReadUsedMemoryBytes());
        }

        public static bool KillProcessSafely(int pid, out string msg)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                string name = p.ProcessName;
                p.Kill();
                if (!p.WaitForExit(5000))
                    throw new TimeoutException($"Windows no confirmó el cierre de '{name}'.");
                msg = $"Programa '{name}' cerrado correctamente.";
                return true;
            }
            catch (Exception ex)
            {
                msg = $"No se pudo cerrar el programa: {ex.Message}";
                return false;
            }
        }

        public static void SetHeavyServicesSuspended(bool suspend)
        {
            if (!suspend)
            {
                ResumePausedServices();
                return;
            }

            foreach (string service in HeavyServices)
            {
                var state = ReadService(service);
                if (state == null)
                {
                    TrackProfileService(service, null, "Stopped", "Servicio no disponible");
                    continue;
                }

                if (state.Value.State == "Running") SetServiceState(service, running: false);
                else TrackProfileService(service, state.Value.State, "Stopped");

                string startMode = ReadServiceStartMode(service);
                if (startMode is "auto" or "delayed-auto")
                {
                    Remember("serviceStart", service, startMode);
                    SetServiceStartMode(service, "demand");
                }
            }
        }

        private static void ResumePausedServices()
        {
            var operations = (_changes ?? SystemChangeJournal.Load()).Undo;
            if (WasServicePausedByWinPulse(operations, LegacyMapsService))
            {
                Remember("serviceRelease", LegacyMapsService, null);
                ReportLegacyMapsService();
                TrackProfileSetting("service", LegacyMapsService, $"Servicio {LegacyMapsService}", null,
                    "En ejecución", null, "Omitido por compatibilidad");
            }

            foreach (string service in HeavyServices)
            {
                if (SavedServiceStartMode(operations, service) is { } savedStart)
                {
                    try
                    {
                        SetServiceStartMode(service, savedStart);
                        Remember("serviceStartRelease", service, null);
                    }
                    catch (IOException ex)
                    {
                        _operationNotices?.Add($"No se pudo restaurar el inicio de {service}. {ex.Message}");
                    }
                }

                if (!WasServicePausedByWinPulse(operations, service)) continue;
                var state = ReadService(service);
                if (state == null || state.Value.StartMode == "Disabled")
                {
                    Remember("serviceRelease", service, null);
                    _operationNotices?.Add($"{service} ya no está disponible para reanudarlo.");
                    TrackProfileService(service, state?.State, "Running", state == null ? "Servicio no disponible" : "Deshabilitado en Windows");
                    continue;
                }
                if (state.Value.State == "Running")
                {
                    Remember("serviceRelease", service, null);
                    TrackProfileService(service, state.Value.State, "Running");
                    continue;
                }

                int savedBefore = RecordedSettingCount;
                try { SetServiceState(service, running: true); }
                catch (IOException ex) when (ReadService(service)?.State == state.Value.State)
                {
                    DiscardUnchangedRecords(savedBefore);
                    _operationNotices?.Add($"No se pudo reanudar {service}. {ex.Message}");
                    TrackProfileService(service, state.Value.State, "Running", "Windows no confirmó la reanudación");
                }
            }
        }

        internal static bool WasServicePausedByWinPulse(
            System.Collections.Generic.IEnumerable<SavedOperation> operations, string service)
        {
            var latest = operations.Reverse()
                .SelectMany(o => o.Settings.AsEnumerable().Reverse())
                .FirstOrDefault(s => (s.Type is "service" or "serviceRelease") &&
                    string.Equals(s.Target, service, StringComparison.OrdinalIgnoreCase));
            return latest is { Type: "service", Value: "Running" };
        }

        private static string? SavedServiceStartMode(
            System.Collections.Generic.IEnumerable<SavedOperation> operations, string service)
        {
            var latest = operations.Reverse()
                .SelectMany(o => o.Settings.AsEnumerable().Reverse())
                .FirstOrDefault(s => (s.Type is "serviceStart" or "serviceStartRelease") &&
                    string.Equals(s.Target, service, StringComparison.OrdinalIgnoreCase));
            return latest is { Type: "serviceStart" } ? latest.Value : null;
        }

        private static bool CheckHeavyServicesStopped()
        {
            try
            {
                var operations = SystemChangeJournal.Load().Undo;
                return HeavyServices.Any(s => WasServicePausedByWinPulse(operations, s) &&
                    ReadService(s)?.State == "Stopped");
            }
            catch { return false; }
        }

        public static int BackupRegistry()
        {
            Directory.CreateDirectory(BackupDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture);
            int exported = 0;
            Export(Registry.CurrentUser, "HKCU", P("Software", "Microsoft", "GameBar"), "GameBar");
            Export(Registry.LocalMachine, "HKLM", P("SOFTWARE", "Microsoft", "Windows NT", "CurrentVersion", "Multimedia", "SystemProfile"), "MMCSS");
            return exported;

            void Export(RegistryKey root, string hive, string key, string label)
            {
                using var existing = root.OpenSubKey(key);
                if (existing == null) return;
                string destination = Path.Combine(BackupDir, $"{label}_{stamp}.reg");
                RunChecked("reg.exe", "export", P(hive, key), destination, "/y");
                if (!File.Exists(destination) || new FileInfo(destination).Length == 0)
                    throw new IOException($"No se pudo comprobar la exportación de {label}.");
                exported++;
            }
        }

        public static Task<string> CreateRestorePointAsync() => Task.Run(() =>
        {
            string description = $"WinPulse Pro {Guid.NewGuid():N}";
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
            string result = RunPowerShell(
                $"Enable-ComputerRestore -Drive '{systemDrive}'; " +
                $"Checkpoint-Computer -Description '{description}' -RestorePointType 'MODIFY_SETTINGS'; " +
                $"$point = Get-ComputerRestorePoint | Where-Object Description -eq '{description}'; " +
                "if ($null -eq $point) { throw 'Windows no creó un punto de restauración nuevo.' }; $point.SequenceNumber",
                timeoutMs: 120_000);
            if (!int.TryParse(result.Trim(), out _))
                throw new IOException("Windows no devolvió un número de punto de restauración válido.");
            return result.Trim();
        });

        public static void SetGameMode(bool enable) =>
            SetDword(Registry.CurrentUser, P("Software", "Microsoft", "GameBar"), "AutoGameModeEnabled", enable ? 1 : 0);

        public static void SetHags(bool enable) =>
            SetDword(Registry.LocalMachine, P("SYSTEM", "CurrentControlSet", "Control", "GraphicsDrivers"), "HwSchMode", enable ? 2 : 1);

        public static void SetHighResTimer(bool enable) =>
            SetDword(Registry.LocalMachine, P("SYSTEM", "CurrentControlSet", "Control", "Session Manager", "kernel"), "GlobalTimerResolutionRequests", enable ? 1 : 0);

        public static void SetForegroundPriority(bool enable) =>
            SetDword(Registry.LocalMachine, P("SYSTEM", "CurrentControlSet", "Control", "PriorityControl"), "Win32PrioritySeparation", enable ? 26 : 2);

        public static void SetWindowedOptimizations(bool enable)
        {
            string val = enable ? "VRROptimizeEnable=1;SwapEffectUpgradeEnable=1;" : "";
            SetString(Registry.CurrentUser, P("Software", "Microsoft", "DirectX", "UserGpuPreferences"), "DirectXUserGlobalSettings", val);
        }

        public static void SetGameDvr(bool disable)
        {
            SetDword(Registry.CurrentUser, P("System", "GameConfigStore"), "GameDVR_Enabled", disable ? 0 : 1);
            SetDword(Registry.CurrentUser, P("Software", "Microsoft", "Windows", "CurrentVersion", "GameDVR"), "AppCaptureEnabled", disable ? 0 : 1);
            SetDword(Registry.LocalMachine, P("SOFTWARE", "Policies", "Microsoft", "Windows", "GameDVR"), "AllowGameDVR", disable ? 0 : 1);
        }

        public static void SetMmcssGaming(bool enable)
        {
            string sysProf = P("SOFTWARE", "Microsoft", "Windows NT", "CurrentVersion", "Multimedia", "SystemProfile");
            string games = P(sysProf, "Tasks", "Games");
            SetDword(Registry.LocalMachine, sysProf, "SystemResponsiveness", enable ? 10 : 20);
            SetDword(Registry.LocalMachine, sysProf, "NetworkThrottlingIndex", enable ? unchecked((int)0xffffffff) : 10);
            SetDword(Registry.LocalMachine, games, "GPU Priority", enable ? 8 : 2);
            SetDword(Registry.LocalMachine, games, "Priority", enable ? 6 : 2);
            SetString(Registry.LocalMachine, games, "Scheduling Category", enable ? "High" : "Medium");
        }

        public static void SetCoreParking(bool disableOnAc, uint minimumOnAc = 5, bool optional = false) =>
            SetCoreParking(disableOnAc, minimumOnAc, optional, planBlocked: false);

        internal static void SetCoreParking(bool disableOnAc, uint minimumOnAc, bool optional, bool planBlocked)
        {
            if (planBlocked && _profileSettings == null) return;
            Guid plan = planBlocked ? ReadActivePlanDetails().Id : ActivePlan();
            if (!optional) TryReactivateActivePlan(plan, optional: true);
            bool changed = WritePower(plan, "CPMINCORES", false, disableOnAc ? 100u : minimumOnAc, optional, planBlocked);
            changed |= WritePower(plan, "CPMINCORES", true, 5, optional, planBlocked);
            if (changed) TryReactivateActivePlan(plan, optional: true);
        }

        public static void SetUsbAndPciePowerSave(bool disableOnAc, bool optional = false) =>
            SetUsbAndPciePowerSave(disableOnAc, optional, planBlocked: false);

        internal static void SetUsbAndPciePowerSave(bool disableOnAc, bool optional, bool planBlocked)
        {
            if (planBlocked && _profileSettings == null) return;
            Guid plan = planBlocked ? ReadActivePlanDetails().Id : ActivePlan();
            if (!optional) TryReactivateActivePlan(plan, optional: true);
            bool changed = WritePower(plan, "USB", false, disableOnAc ? 0u : 1u, optional, planBlocked);
            changed |= WritePower(plan, "PCIE", false, disableOnAc ? 0u : 2u, optional, planBlocked);
            changed |= WritePower(plan, "PCIE", true, 2, optional, planBlocked);
            if (changed) TryReactivateActivePlan(plan, optional: true);
        }

        public static void SetNagleAlgorithm(bool disable, bool optional = false)
        {
            string path = InterfacesPath;
            using var interfaces = Registry.LocalMachine.OpenSubKey(path);
            int matched = 0;
            foreach (string sub in interfaces?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                using var nic = interfaces!.OpenSubKey(sub);
                if (nic == null || !HasConfiguredIp(nic)) continue;
                matched++;
                WriteRegistry(Registry.LocalMachine, P(path, sub), "TcpAckFrequency", disable ? 1 : null, RegistryValueKind.DWord);
                WriteRegistry(Registry.LocalMachine, P(path, sub), "TCPNoDelay", disable ? 1 : null, RegistryValueKind.DWord);
            }

            if (disable && matched == 0)
            {
                const string message = "No hay adaptadores con una dirección IP configurada para aplicar el ajuste TCP.";
                if (!optional) throw new IOException(message);
                _operationNotices?.Add(message);
                TrackProfileSetting("network", "", "Ajuste de latencia TCP", null,
                    "TcpAckFrequency = 1 · TCPNoDelay = 1", null, "Sin adaptadores con IP configurada");
            }
        }

        private static bool HasConfiguredIp(RegistryKey nic)
        {
            var addresses = nic.GetValue("IPAddress") switch
            {
                string[] all => all,
                string single => new[] { single },
                _ => Array.Empty<string>()
            };
            return addresses.Append(nic.GetValue("DhcpIPAddress") as string)
                .Any(address => System.Net.IPAddress.TryParse(address, out var ip) && !ip.Equals(System.Net.IPAddress.Any));
        }

        public static void SetBackgroundApps(bool disable) =>
            SetDword(Registry.CurrentUser, P("Software", "Microsoft", "Windows", "CurrentVersion", "BackgroundAccessApplications"), "GlobalUserDisabled", disable ? 1 : 0);

        public static bool IsFastUiResponseEnabled()
        {
            string minAnimate = GetString(Registry.CurrentUser, P("Control Panel", "Desktop", "WindowMetrics"), "MinAnimate", "1");
            string menuDelay = GetString(Registry.CurrentUser, P("Control Panel", "Desktop"), "MenuShowDelay", "400");
            return minAnimate == "0" && int.TryParse(menuDelay, NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay) && delay <= 50;
        }

        public static void SetFastUiResponse(bool enable)
        {
            int delay = enable ? 20 : 400;
            string minAnimate = enable ? "0" : "1";
            SetString(Registry.CurrentUser, P("Control Panel", "Desktop"), "MenuShowDelay", delay.ToString(CultureInfo.InvariantCulture));
            SetString(Registry.CurrentUser, P("Control Panel", "Desktop", "WindowMetrics"), "MinAnimate", minAnimate);
            NotifyMenuShowDelay(delay);
            NotifyWindowAnimation(!enable);
        }

        internal static void NotifyMenuShowDelay(int delayMs) =>
            SystemParametersInfo(0x006B, (uint)Math.Clamp(delayMs, 0, 10000), IntPtr.Zero, 0x03);

        internal static void NotifyWindowAnimation(bool animate)
        {
            var info = new AnimationInfo { cbSize = (uint)Marshal.SizeOf<AnimationInfo>(), iMinAnimate = animate ? 1 : 0 };
            SystemParametersInfo(0x0049, info.cbSize, ref info, 0x03);
        }

        public static bool IsMouseAccelerationDisabled()
        {
            string mousePath = P("Control Panel", "Mouse");
            return GetString(Registry.CurrentUser, mousePath, "MouseSpeed", "1") == "0" &&
                GetString(Registry.CurrentUser, mousePath, "MouseThreshold1", "6") == "0" &&
                GetString(Registry.CurrentUser, mousePath, "MouseThreshold2", "10") == "0";
        }

        public static void SetMouseAccelerationDisabled(bool disable)
        {
            string mousePath = P("Control Panel", "Mouse");
            int speed = disable ? 0 : 1;
            int t1 = disable ? 0 : 6;
            int t2 = disable ? 0 : 10;
            SetString(Registry.CurrentUser, mousePath, "MouseSpeed", speed.ToString(CultureInfo.InvariantCulture));
            SetString(Registry.CurrentUser, mousePath, "MouseThreshold1", t1.ToString(CultureInfo.InvariantCulture));
            SetString(Registry.CurrentUser, mousePath, "MouseThreshold2", t2.ToString(CultureInfo.InvariantCulture));
            NotifyMouseParameters(t1, t2, speed);
        }

        internal static void NotifyMouseParameters(int threshold1, int threshold2, int speed) =>
            SystemParametersInfo(0x0004, 0, new[] { threshold1, threshold2, speed }, 0x03);

        internal static void NotifyMouseFromRegistry()
        {
            string mousePath = P("Control Panel", "Mouse");
            int t1 = int.TryParse(GetString(Registry.CurrentUser, mousePath, "MouseThreshold1", "6"), out int v1) ? v1 : 6;
            int t2 = int.TryParse(GetString(Registry.CurrentUser, mousePath, "MouseThreshold2", "10"), out int v2) ? v2 : 10;
            int speed = int.TryParse(GetString(Registry.CurrentUser, mousePath, "MouseSpeed", "1"), out int vs) ? vs : 1;
            NotifyMouseParameters(t1, t2, speed);
        }

        public static Task<int> CleanTemporaryFilesAsync() => Task.Run(() =>
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] dirs =
            {
                Path.GetTempPath(),
                Path.Combine(localApp, "D3DSCache"),
                Path.Combine(localApp, "NVIDIA", "DXCache"),
                Path.Combine(localApp, "NVIDIA", "GLCache"),
                Path.Combine(localApp, "AMD", "DxCache"),
                Path.Combine(localApp, "AMD", "DxcCache"),
                Path.Combine(localApp, "AMD", "GLCache"),
                Path.Combine(localApp, "AMD", "VkCache")
            };

            return DeleteFilesInDirectories(dirs);
        });

        public static Task<int> CompleteDiskMaintenanceAsync() => Task.Run(() =>
        {
            int windowsTempDeleted = DeleteFilesInDirectories(new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
            });
            RunCommand("ipconfig.exe", "/flushdns");
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
            RunPowerShell($"Optimize-Volume -DriveLetter '{systemDrive[0]}' -ReTrim", timeoutMs: 120_000);
            return windowsTempDeleted;
        });

        private static int DeleteFilesInDirectories(System.Collections.Generic.IEnumerable<string> directories)
        {
            int filesDeleted = 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var dir in directories.Where(Directory.Exists))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", options))
                {
                    try { File.Delete(file); filesDeleted++; } catch { }
                }
            }
            return filesDeleted;
        }

        public static void RestoreDefaults()
        {
            var plan = ApplyUsagePowerScheme(UsageProfile.FactoryDefault);
            SetGameMode(true);
            SetHags(true);
            SetWindowedOptimizations(true);
            SetHighResTimer(false);
            SetGameDvr(false);
            SetMmcssGaming(false);
            SetForegroundPriority(false);
            SetCoreParking(false, 5, optional: true, planBlocked: !plan.HasValue);
            SetHeavyServicesSuspended(false);
            SetUsbAndPciePowerSave(false, optional: true, planBlocked: !plan.HasValue);
            SetNagleAlgorithm(false);
            SetBackgroundApps(false);
            SetFastUiResponse(false);
            SetMouseAccelerationDisabled(false);
            ApplyProfilePowerMode(UsageProfile.FactoryDefault, plan);
        }

        private static bool CheckNagleDisabled()
        {
            try
            {
                using var interfaces = Registry.LocalMachine.OpenSubKey(InterfacesPath);
                if (interfaces == null) return false;
                bool applicable = false;
                foreach (string sub in interfaces.GetSubKeyNames())
                {
                    using var nic = interfaces.OpenSubKey(sub);
                    if (nic == null || !HasConfiguredIp(nic)) continue;
                    applicable = true;
                    if (nic.GetValue("TCPNoDelay") is not 1 || nic.GetValue("TcpAckFrequency") is not 1)
                        return false;
                }
                return applicable;
            }
            catch { return false; }
        }

        private static int GetDword(RegistryKey root, string path, string name, int def)
        {
            try { using var k = root.OpenSubKey(path); return k?.GetValue(name) is int v ? v : def; }
            catch { return def; }
        }

        private static string GetString(RegistryKey root, string path, string name, string def)
        {
            try { using var k = root.OpenSubKey(path); return k?.GetValue(name)?.ToString() ?? def; }
            catch { return def; }
        }

        private static void SetDword(RegistryKey root, string path, string name, int val) =>
            WriteRegistry(root, path, name, val, RegistryValueKind.DWord);

        private static void SetString(RegistryKey root, string path, string name, string val) =>
            WriteRegistry(root, path, name, val, RegistryValueKind.String);

        private static void RunCommand(string file, string args) =>
            ExecuteCommand(new ProcessStartInfo(SystemExecutable(file), args), 15_000);

        private static string RunChecked(string file, params string[] args) =>
            RunChecked(file, 15_000, args);

        private static string RunChecked(string file, int timeoutMs, params string[] args)
        {
            var start = new ProcessStartInfo(SystemExecutable(file));
            foreach (string arg in args) start.ArgumentList.Add(arg);
            return ExecuteCommand(start, timeoutMs);
        }

        private static string RunPowerShell(string script, int timeoutMs = 30_000) =>
            RunChecked(@"WindowsPowerShell\v1.0\powershell.exe", timeoutMs, "-NoProfile", "-NonInteractive", "-Command",
                "$ErrorActionPreference = 'Stop'; " + script);

        private static string SystemExecutable(string file) => Path.Combine(Environment.SystemDirectory, file);

        private static string ExecuteCommand(ProcessStartInfo start, int timeoutMs)
        {
            start.CreateNoWindow = true;
            start.UseShellExecute = false;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            string name = Path.GetFileName(start.FileName);
            using var process = Process.Start(start) ?? throw new IOException($"No se pudo ejecutar {name}.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) when (process.HasExited) { }
                }
                if (!process.WaitForExit(5000))
                    throw new TimeoutException($"{name} agotó el tiempo de espera y sigue en ejecución.");
                throw new TimeoutException($"{name} agotó el tiempo de espera.");
            }

            string[] streams = Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            string stdout = streams[0];
            string stderr = streams[1];
            if (process.ExitCode != 0)
            {
                string details = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                throw new IOException($"{name} devolvió el código {process.ExitCode}. {details}".TrimEnd());
            }
            return stdout;
        }

    }
}
