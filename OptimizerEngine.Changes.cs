using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using System.Threading;
using Microsoft.Win32;

namespace WinPulseOptimizer
{
    public static partial class OptimizerEngine
    {
        private static readonly Mutex ChangeMutex = new(false, @"Local\WinPulseOptimizer.SystemChanges");
        private static ChangeStore? _changes;
        private static List<string>? _operationNotices;

        internal static (bool Changed, bool Restart, string Notice, ProfileSettingResult[] Settings) ApplyVerifiedChange(
            string title, string previousProfile, Action apply, string? automaticProfile = null,
            bool replaceAutomation = false, bool reportProfile = false)
        {
            return LockChanges(store =>
            {
                if (store.Pending != null || store.RestoringOriginal.HasValue)
                    throw new InvalidOperationException("Hay un cambio pendiente. Recupéralo desde el historial.");
                if (automaticProfile != null && store.AutomationOperationId != null)
                    throw new InvalidOperationException("Recupera el perfil anterior antes de aplicar otro perfil automático.");
                store.Pending = new(title, previousProfile, new(), Guid.NewGuid(), automaticProfile);
                _changes = store;
                var operationNotices = new List<string>();
                _operationNotices = operationNotices;
                _profileSettings = new();
                var operation = store.Pending;
                try
                {
                    apply();
                    var settings = ReadProfileSettings(store);
                    RecordUntrackedSettings(store, operation.Settings);
                    bool restart = HasPendingRestart(store, operation.Settings);
                    SystemChangeJournal.Commit(store, replaceAutomation);
                    return (operation.Settings.Count != 0, restart,
                        string.Join(" ", operationNotices), reportProfile ? settings : []);
                }
                catch (Exception error)
                {
                    ProfileSettingResult[] settings;
                    try
                    {
                        _changes = null;
                        RestoreSettings(operation.Settings);
                        settings = ReadProfileSettings(store, failed: true);
                        store.Pending = null;
                        SystemChangeJournal.Save(store);
                    }
                    catch (Exception rollbackError)
                    {
                        string pending = $"{error.Message} Recuperación pendiente: {rollbackError.Message}";
                        throw reportProfile ? new ProfileChangeException(pending, error, ReadProfileSettings(failed: true))
                            : new InvalidOperationException(pending, error);
                    }
                    string result = operation.Settings.Count == 0
                        ? "No había cambios registrados que revertir."
                        : "Se revirtieron los cambios de esta operación.";
                    string message = $"{error.Message} {result}";
                    throw reportProfile ? new ProfileChangeException(message, error, settings)
                        : new InvalidOperationException(message, error);
                }
                finally
                {
                    _changes = null;
                    _operationNotices = null;
                    _profileSettings = null;
                }
            });
        }

        internal static (string Title, string Profile, bool Restart, string Notice) RestoreSavedChange(bool original)
        {
            return LockChanges(store =>
            {
                if (store.RestoringOriginal is bool interrupted && interrupted != original)
                    throw new InvalidOperationException("Completa primero la restauración que quedó pendiente.");
                var operation = store.Pending ?? store.Undo.LastOrDefault();
                if (original && store.Pending != null)
                    throw new InvalidOperationException("Recupera primero el cambio que quedó pendiente.");
                if (original ? store.Original.Count == 0 : operation == null)
                    throw new InvalidOperationException("Todavía no hay cambios guardados para restaurar.");
                var settings = original ? store.Original : operation!.Settings;
                store.RestoringOriginal = original;
                SystemChangeJournal.Save(store);
                string notice = RestoreSettings(settings);
                RecordUntrackedSettings(store, settings, overwrite: true);
                if (original)
                {
                    store.Undo.Clear();
                    store.Original.Clear();
                    store.AutomationOperationId = null;
                }
                else if (store.Pending != null) store.Pending = null;
                else store.Undo.RemoveAt(store.Undo.Count - 1);
                if (operation != null && operation.Id == store.AutomationOperationId) store.AutomationOperationId = null;
                var profile = original ? store.OriginalProfile ?? "" : operation!.PreviousProfile;
                if (original) store.OriginalProfile = null;
                store.RestoringOriginal = null;
                SystemChangeJournal.Save(store);
                return (original ? "Configuración original restaurada" : $"Deshiciste: {operation!.Title}",
                    profile, HasPendingRestart(store, settings), notice);
            });
        }

        internal static (string Profile, bool Restart, string Notice) RestoreAutomaticProfile()
        {
            return LockChanges(store =>
            {
                if (store.Pending != null || store.RestoringOriginal.HasValue)
                    throw new InvalidOperationException("Completa primero la recuperación que quedó pendiente en el historial.");
                int index = store.Undo.FindIndex(o => o.Id == store.AutomationOperationId);
                if (index < 0) throw new InvalidOperationException("El perfil anterior ya no está guardado.");
                var operation = store.Undo[index];
                var later = store.Undo.Skip(index + 1).SelectMany(o => o.Settings).ToList();
                if (operation.Settings.Any(saved => later.Any(saved.Matches)))
                    throw new InvalidOperationException("Otro cambio posterior afecta al perfil anterior. Puedes recuperarlo desde el historial.");
                string notice = RestoreSettings(operation.Settings);
                RecordUntrackedSettings(store, operation.Settings, overwrite: true);
                store.Undo.RemoveAt(index);
                store.AutomationOperationId = null;
                SystemChangeJournal.Save(store);
                return (operation.PreviousProfile,
                    HasPendingRestart(store, operation.Settings), notice);
            });
        }

        private static T LockChanges<T>(Func<ChangeStore, T> action)
        {
            try
            {
                if (!ChangeMutex.WaitOne(TimeSpan.Zero))
                    throw new InvalidOperationException("Otra instancia de WinPulse está aplicando cambios.");
            }
            catch (AbandonedMutexException) { }
            try
            {
                _statusStore = SystemChangeJournal.Load();
                return action(_statusStore);
            }
            finally
            {
                _statusStore = null;
                ChangeMutex.ReleaseMutex();
            }
        }

        private static void Remember(string type, string target, string? value)
        {
            if (_changes != null)
                SystemChangeJournal.Remember(_changes, new(type, target, value));
        }

        private static int RecordedSettingCount => _changes?.Pending?.Settings.Count ?? 0;

        private static void DiscardUnchangedRecords(int countBeforeAttempt)
        {
            if (_changes?.Pending?.Settings is not { } saved || saved.Count <= countBeforeAttempt) return;
            saved.RemoveRange(countBeforeAttempt, saved.Count - countBeforeAttempt);
            SystemChangeJournal.Save(_changes);
        }

        private static string RestoreSettings(List<SavedSetting> settings)
        {
            if (settings.Count == 0) return "";
            string notice = "";
            var createdPlans = settings.Where(s => s.Type == "newPlan").Select(s => s.Target).ToHashSet();
            foreach (var start in settings.Where(s => s.Type == "serviceStart").Reverse())
                SetServiceStartMode(start.Target, start.Value!);
            foreach (var saved in settings.AsEnumerable().Reverse())
            {
                switch (saved.Type)
                {
                    case "registry":
                        if (!IsOverlay(saved.Target)) RestoreRegistry(saved);
                        break;
                    case "power":
                        var parts = saved.Target.Split('|');
                        if (!createdPlans.Contains(parts[0]))
                            WritePower(Guid.Parse(parts[0]), parts[1], parts[2] == "DC",
                                uint.Parse(saved.Value!, CultureInfo.InvariantCulture));
                        break;
                    case "service" when saved.Target == LegacyMapsService:
                        notice = LegacyMapsNotice;
                        break;
                    case "service":
                        SetServiceState(saved.Target, saved.Value == "Running");
                        break;
                    case "serviceStart" or "serviceRelease" or "serviceStartRelease":
                        break;
                    case "task" when saved.Target == AutoStartTask:
                        RestoreAutoStart(saved.Value!);
                        break;
                    case "ethernet":
                        RestoreEthernetOption(saved);
                        break;
                    case "activePlan" or "newPlan" or "powerMode":
                        break;
                    default:
                        throw new InvalidDataException("El registro contiene un ajuste de tipo desconocido.");
                }
            }

            var active = settings.FirstOrDefault(s => s.Type == "activePlan");
            if (active != null || settings.Any(s => s.Type == "power"))
            {
                Guid current = ActivePlan();
                Guid restored = active != null ? Guid.Parse(active.Value!) : current;
                if (current != restored || settings.Any(s => s.Type == "power" &&
                    s.Target.StartsWith($"{restored}|", StringComparison.OrdinalIgnoreCase)))
                    ActivatePlan(restored);
            }

            foreach (var saved in settings.Where(s => s.Type == "registry" && IsOverlay(s.Target)))
                RestoreRegistry(saved);
            foreach (var saved in settings.Where(s => s.Type == "powerMode"))
                WritePowerMode(saved.Target == "DC", Guid.Parse(saved.Value!));
            foreach (var guid in createdPlans)
            {
                var plan = Guid.Parse(guid);
                if (plan != GamingPlan && plan != StudyPlan && plan != ChillPlan)
                    throw new InvalidDataException("El registro contiene un plan que WinPulse no puede eliminar.");
                if (PlanExists(plan)) VerifyWindowsResult(PowerDeleteScheme(IntPtr.Zero, plan), "Eliminar el plan creado por WinPulse");
                if (PlanExists(plan)) throw new IOException($"Windows no confirmó la eliminación del plan {plan}.");
            }
            return notice;
        }

        private const string LegacyMapsNotice = "MapsBroker queda a cargo de Windows.";

        private static void ReportLegacyMapsService() => _operationNotices?.Add(LegacyMapsNotice);

        private static bool IsOverlay(string target) =>
            target.EndsWith("|ActiveOverlayAcPowerScheme", StringComparison.Ordinal) ||
            target.EndsWith("|ActiveOverlayDcPowerScheme", StringComparison.Ordinal);

        private static bool NeedsRestart(string target) =>
            target.EndsWith("|HwSchMode", StringComparison.Ordinal) ||
            target.EndsWith("|GlobalTimerResolutionRequests", StringComparison.Ordinal) ||
            target.EndsWith("|TcpAckFrequency", StringComparison.Ordinal) ||
            target.EndsWith("|TCPNoDelay", StringComparison.Ordinal) || IsOverlay(target);

        private sealed record RegistryValue(RegistryValueKind Kind, JsonElement Data);

        private static readonly Dictionary<string, string[]> RegistrySettings = new(StringComparer.OrdinalIgnoreCase)
        {
            [@"HKCU|Software\Microsoft\GameBar"] = new[] { "AutoGameModeEnabled" },
            [@"HKLM|SYSTEM\CurrentControlSet\Control\GraphicsDrivers"] = new[] { "HwSchMode" },
            [@"HKLM|SYSTEM\CurrentControlSet\Control\Session Manager\kernel"] = new[] { "GlobalTimerResolutionRequests" },
            [@"HKLM|SYSTEM\CurrentControlSet\Control\PriorityControl"] = new[] { "Win32PrioritySeparation" },
            [@"HKCU|Software\Microsoft\DirectX\UserGpuPreferences"] = new[] { "DirectXUserGlobalSettings" },
            [@"HKCU|System\GameConfigStore"] = new[] { "GameDVR_Enabled" },
            [@"HKCU|Software\Microsoft\Windows\CurrentVersion\GameDVR"] = new[] { "AppCaptureEnabled" },
            [@"HKLM|SOFTWARE\Policies\Microsoft\Windows\GameDVR"] = new[] { "AllowGameDVR" },
            [@"HKLM|SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"] = new[] { "SystemResponsiveness", "NetworkThrottlingIndex" },
            [@"HKLM|SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games"] = new[] { "GPU Priority", "Priority", "Scheduling Category" },
            [@"HKCU|Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications"] = new[] { "GlobalUserDisabled" },
            [@"HKCU|Control Panel\Desktop"] = new[] { "MenuShowDelay" },
            [@"HKCU|Control Panel\Desktop\WindowMetrics"] = new[] { "MinAnimate" },
            [@"HKCU|Control Panel\Mouse"] = new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" },
            [@"HKCU|Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"] = new[] { "EnableTransparency" },
            [$"HKLM|{PowerModePath}"] = new[] { "ActiveOverlayAcPowerScheme", "ActiveOverlayDcPowerScheme" }
        };

        private static bool CanRestoreRegistry(string scope, string path, string name)
        {
            if (RegistrySettings.TryGetValue($"{scope}|{path}", out var names))
                return names.Contains(name, StringComparer.OrdinalIgnoreCase);

            if (path.Equals(StartupApprovedPath, StringComparison.OrdinalIgnoreCase))
                return scope is "HKLM" or "HKCU";

            string prefix = InterfacesPath + Sep;
            return scope == "HKLM" && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(path[prefix.Length..], out _) && name is "TcpAckFrequency" or "TCPNoDelay";
        }

        private static string? ReadRegistryValue(RegistryKey? key, string name)
        {
            var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return value == null ? null : JsonSerializer.Serialize(
                new RegistryValue(key!.GetValueKind(name), JsonSerializer.SerializeToElement(value)));
        }

        private static void WriteRegistry(RegistryKey root, string path, string name, object? value, RegistryValueKind kind)
        {
            string? expected = value == null ? null : JsonSerializer.Serialize(
                new RegistryValue(kind, JsonSerializer.SerializeToElement(value)));
            string scope = root == Registry.CurrentUser ? "HKCU" : "HKLM";
            string target = $"{scope}|{path}|{name}";
            bool report = _changes != null && _profileSettings != null;
            string ReadCurrent()
            {
                using var current = root.OpenSubKey(path);
                return FormatReportRegistry(ReadRegistryValue(current, name));
            }
            ProfileSettingAttempt? Track(string? before, string? note = null)
            {
                string label = path.StartsWith(InterfacesPath + Sep, StringComparison.OrdinalIgnoreCase)
                    ? $"{name} · {ReportInterfaceName(path[(InterfacesPath.Length + 1)..])}" : ReportRegistryName(name);
                return TrackProfileSetting("registry", target, label, before, FormatReportRegistry(expected),
                    ReadCurrent, note, restart: NeedsRestart(target));
            }
            RegistryKey? writable;
            try { writable = root.OpenSubKey(path, writable: true); }
            catch (Exception ex) when (report && ex is UnauthorizedAccessException or SecurityException)
            {
                Track(ReadReportValue(ReadCurrent), "Acceso denegado");
                throw;
            }
            using var key = writable;
            string? previous = ReadRegistryValue(key, name);
            var row = report ? Track(FormatReportRegistry(previous)) : null;
            if (previous == expected) return;
            Remember("registry", target, previous);
            RememberRestartBaseline(target, previous);
            using var created = key == null && value != null ? root.CreateSubKey(path, writable: true) : null;
            var destination = key ?? created;
            try
            {
                if (value == null) destination?.DeleteValue(name, throwOnMissingValue: false);
                else (destination ?? throw new IOException($"No se pudo abrir {path}.")).SetValue(name, value, kind);
            }
            catch (Exception ex) when (row != null && ex is UnauthorizedAccessException or SecurityException)
            {
                row.Note = "Acceso denegado";
                throw;
            }
            if (ReadRegistryValue(destination, name) != expected)
                throw new IOException($"Windows no confirmó el valor de {name}.");
            if (row != null) row.Written = true;
        }

        private static void RestoreRegistry(SavedSetting setting)
        {
            var parts = setting.Target.Split('|', 3);
            if (parts.Length != 3 || !CanRestoreRegistry(parts[0], parts[1], parts[2]))
                throw new InvalidDataException("El registro contiene un valor que WinPulse no puede restaurar.");
            var root = parts[0] == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
            var saved = setting.Value == null ? null : JsonSerializer.Deserialize<RegistryValue>(setting.Value);
            object? value = saved?.Kind switch
            {
                null => null,
                RegistryValueKind.DWord => saved.Data.GetInt32(),
                RegistryValueKind.QWord => saved.Data.GetInt64(),
                RegistryValueKind.String or RegistryValueKind.ExpandString => saved.Data.GetString(),
                RegistryValueKind.MultiString => saved.Data.Deserialize<string[]>(),
                RegistryValueKind.Binary or RegistryValueKind.None => saved.Data.GetBytesFromBase64(),
                _ => throw new InvalidDataException($"Tipo de registro incompatible para {parts[2]}.")
            };
            WriteRegistry(root, parts[1], parts[2], value, saved?.Kind ?? RegistryValueKind.None);
            if (parts[0] == "HKCU" && parts[2] == "MenuShowDelay" &&
                int.TryParse(value as string ?? "400", NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay))
                NotifyMenuShowDelay(delay);
            else if (parts[0] == "HKCU" && parts[2] == "MinAnimate")
                NotifyWindowAnimation((value as string ?? "1") != "0");
            else if (parts[0] == "HKCU" && parts[2] is "MouseSpeed" or "MouseThreshold1" or "MouseThreshold2")
                NotifyMouseFromRegistry();
            else if (parts[0] == "HKCU" && parts[2] == "EnableTransparency")
                NotifyPersonalizationChanged();
        }

        private static readonly Dictionary<string, (Guid Group, Guid Setting)> PowerSettings = new()
        {
            ["PROCTHROTTLEMIN"] = (new("54533251-82be-4824-96c1-47b60b740d00"), new("893dee8e-2bef-41e0-89c6-b55d0929964c")),
            ["PROCTHROTTLEMAX"] = (new("54533251-82be-4824-96c1-47b60b740d00"), new("bc5038f7-23e0-4960-96da-33abaf5935ec")),
            ["CPMINCORES"] = (new("54533251-82be-4824-96c1-47b60b740d00"), new("0cc5b647-c1df-4637-891a-dec35c318583")),
            [ProcessorBoostSetting] = (new("54533251-82be-4824-96c1-47b60b740d00"), new("be337238-0d82-4146-a960-4f3749d470c7")),
            ["DISKIDLE"] = (new("0012ee47-9041-4b5d-9b77-535fba8b1442"), new("6738e2c4-e8a5-4a42-b16a-e040e769756e")),
            ["STANDBYIDLE"] = (new("238c9fa8-0aad-41ed-83f4-97be242c8f20"), new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da")),
            ["USB"] = (new("2a737441-1930-4402-8d77-b2bebba308a3"), new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226")),
            ["PCIE"] = (new("501a4d13-42af-4429-9fd1-a8218c268e20"), new("ee12f906-d277-404b-b6da-e5fa1a576df5")),
            ["SLIDESHOW"] = (new("0d7dbae2-4294-44ce-a28c-b21febe1c592"), new("309dce9b-befb-4119-9929-a87fb8f40a1d")),
            ["VIDEOIDLE"] = (new("7516b95f-f776-4464-8c53-06167f40cc99"), new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e")),
            ["BATLEVELCRIT"] = (new("e73a048d-bf27-4f12-9731-8b2076e8891f"), new("9a66d8d7-4ff7-4ef9-b5a2-5a326ca2a469")),
            ["BATLEVELLOW"] = (new("e73a048d-bf27-4f12-9731-8b2076e8891f"), new("8183ba9a-e910-48da-8769-14ae6dc1170a"))
        };

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);

        [DllImport("powrprof.dll")]
        private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid,
            uint accessFlags, uint index, IntPtr buffer, ref uint bufferSize);

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveScheme(IntPtr root, in Guid scheme);

        [DllImport("powrprof.dll")]
        private static extern uint PowerDeleteScheme(IntPtr root, in Guid scheme);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadFriendlyName(IntPtr root, in Guid scheme, IntPtr subgroup,
            IntPtr setting, IntPtr buffer, ref uint size);

        [StructLayout(LayoutKind.Explicit, Size = 76)]
        private struct SystemPowerCapabilities
        {
            [FieldOffset(20)]
            public byte AoAc;
        }

        [DllImport("powrprof.dll")]
        private static extern byte GetPwrCapabilities(out SystemPowerCapabilities capabilities);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadACValueIndex(IntPtr root, in Guid scheme, in Guid group, in Guid setting, out uint value);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadDCValueIndex(IntPtr root, in Guid scheme, in Guid group, in Guid setting, out uint value);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteACValueIndex(IntPtr root, in Guid scheme, in Guid group, in Guid setting, uint value);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteDCValueIndex(IntPtr root, in Guid scheme, in Guid group, in Guid setting, uint value);

        private sealed class WindowsPowerException(uint code, string action)
            : IOException($"{action}: {new Win32Exception((int)code).Message} ({code}).")
        {
            public uint Code { get; } = code;
        }

        private static void VerifyWindowsResult(uint status, string action)
        {
            if (status != 0) throw new WindowsPowerException(status, action);
        }

        private static bool SupportsModernStandby() =>
            GetPwrCapabilities(out var capabilities) != 0 && capabilities.AoAc != 0;

        private static Guid ActivePlan()
        {
            VerifyWindowsResult(PowerGetActiveScheme(IntPtr.Zero, out var pointer), "Leer el plan activo");
            try { return Marshal.PtrToStructure<Guid>(pointer); }
            finally { LocalFree(pointer); }
        }

        public static WindowsPowerPlanItem[] GetPowerSchemes()
        {
            Guid active;
            try { active = ActivePlan(); }
            catch (IOException) { active = Guid.Empty; }

            var plans = new List<WindowsPowerPlanItem>();
            IntPtr buffer = Marshal.AllocHGlobal(16);
            try
            {
                for (uint index = 0; ; index++)
                {
                    uint size = 16;
                    uint status = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16, index, buffer, ref size);
                    if (status == 259) break;
                    if (status != 0) break;
                    var id = Marshal.PtrToStructure<Guid>(buffer);
                    string name = ReadPlanName(id) ?? id.ToString();
                    plans.Add(new WindowsPowerPlanItem(id, name, id == active));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }

            return plans.OrderByDescending(p => p.IsActive).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        private static void ActivatePlan(Guid guid)
        {
            var previous = ActivePlan();
            if (previous != guid)
                Remember("activePlan", "", previous.ToString());
            if (_changes != null && (previous != guid || _changes.Pending!.Settings.Any(s => s.Type == "power")))
                RememberPowerModes();
            VerifyWindowsResult(PowerSetActiveScheme(IntPtr.Zero, guid), "Activar el plan de energía");
            if (ActivePlan() != guid) throw new IOException("Windows no confirmó el plan de energía activo.");
        }

        private static bool TryActivateProfilePlan(Guid plan, out bool canReactivate)
        {
            Guid previous = ActivePlan();
            ProfileSettingAttempt? row = _profileSettings == null ? null
                : TrackProfileSetting("activePlan", "", "Plan de energía activo",
                    ReadReportValue(() => FormatReportPlan(previous)), ReadReportValue(() => FormatReportPlan(plan)) ?? plan.ToString(),
                    () => FormatReportPlan(ActivePlan()));
            int savedBefore = RecordedSettingCount;
            try
            {
                ActivatePlan(plan);
                if (row != null) row.Written = previous != plan;
                canReactivate = true;
                return true;
            }
            catch (WindowsPowerException ex) when ((ex.Code is 1260 or 50 or 5) && ActivePlan() == previous)
            {
                DiscardUnchangedRecords(savedBefore);
                canReactivate = false;
                if (previous == plan)
                {
                    // El plan solicitado ya es el activo; Windows solo rechazó reactivarlo.
                    return true;
                }
                if (row != null)
                    row.Note = ex.Code == 1260 ? BlockedPlan : $"Bloqueado por Windows ({ex.Code})";
                _operationNotices?.Add(ex.Code == 1260
                    ? "La directiva de Windows bloqueó el cambio de plan (1260). Se omitieron los límites de CPU, el modo turbo y los demás ajustes del plan."
                    : $"Windows no permitió cambiar al plan solicitado ({ex.Code}). Se conservó el plan actual y se omitieron sus límites de energía.");
                return false;
            }
        }

        private static void TryReactivateActivePlan(Guid plan, bool optional)
        {
            try
            {
                ActivatePlan(plan);
            }
            catch (WindowsPowerException ex) when (optional && (ex.Code is 1260 or 50 or 5) && ActivePlan() == plan)
            {
                // El plan sigue activo; Windows no requiere o no admite reactivarlo aquí.
            }
        }

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetUserConfiguredACPowerMode(out Guid mode);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetUserConfiguredACPowerMode(in Guid mode);

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetUserConfiguredDCPowerMode(in Guid mode);

        private const string PowerModePath = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";

        private static Guid? ReadPowerMode(bool battery)
        {
            uint status;
            Guid mode;
            try
            {
                status = battery
                    ? PowerGetUserConfiguredDCPowerMode(out mode)
                    : PowerGetUserConfiguredACPowerMode(out mode);
            }
            catch (EntryPointNotFoundException) { return null; }
            VerifyWindowsResult(status, battery ? "Leer el modo de energía en batería" : "Leer el modo de energía enchufado");
            return mode;
        }

        private static void RememberPowerModes()
        {
            foreach (bool battery in new[] { false, true })
            {
                Guid? mode;
                try { mode = ReadPowerMode(battery); }
                catch (WindowsPowerException ex) when (IsOptionalPowerRestriction(ex.Code)) { continue; }
                if (mode.HasValue)
                    Remember("powerMode", battery ? "DC" : "AC", mode.ToString());
                else
                {
                    string valueName = battery ? "ActiveOverlayDcPowerScheme" : "ActiveOverlayAcPowerScheme";
                    using var key = Registry.LocalMachine.OpenSubKey(PowerModePath);
                    Remember("registry", $"HKLM|{PowerModePath}|{valueName}", ReadRegistryValue(key, valueName));
                }
            }
        }

        internal static bool IsOptionalPowerRestriction(uint status) =>
            status is 2 or 5 or 50 or 87 or 1168 or 1260;

        private static void ReportUnavailablePowerMode(bool battery, uint status)
        {
            string source = battery ? "en batería" : "enchufado";
            string message = status switch
            {
                0 => $"El modo de energía de Windows {source} no está disponible.",
                5 or 1260 => $"Windows bloqueó el modo de energía {source} ({status}).",
                _ => $"Windows no admite ese modo de energía {source} ({status})."
            };
            _operationNotices?.Add(message);
        }

        private static void WritePowerMode(bool battery, Guid mode, bool optional = false)
        {
            Guid? previous;
            ProfileSettingAttempt? row = null;
            ProfileSettingAttempt? Track(Guid? value) => _changes == null || _profileSettings == null ? null
                : TrackProfileSetting("powerMode", battery ? "DC" : "AC",
                    $"Modo de energía de Windows · {(battery ? "batería" : "enchufado")}",
                    value is Guid old ? FormatReportMode(old) : null, FormatReportMode(mode),
                    () => ReadPowerMode(battery) is Guid actual ? FormatReportMode(actual) : null);
            try { previous = ReadPowerMode(battery); }
            catch (WindowsPowerException ex) when (optional && IsOptionalPowerRestriction(ex.Code))
            {
                row = Track(null);
                if (row != null) row.Note = ReportPowerRestriction(ex.Code);
                ReportUnavailablePowerMode(battery, ex.Code);
                return;
            }
            row = Track(previous);
            if (previous == mode) return;
            if (previous == null)
            {
                if (!optional) throw new IOException("Windows ya no permite restaurar el modo de energía guardado.");
                if (row != null) row.Note = UnavailableValue;
                ReportUnavailablePowerMode(battery, 0);
                return;
            }

            int savedBefore = RecordedSettingCount;
            Remember("powerMode", battery ? "DC" : "AC", previous.ToString());
            uint status = battery ? PowerSetUserConfiguredDCPowerMode(mode) : PowerSetUserConfiguredACPowerMode(mode);
            if (optional && IsOptionalPowerRestriction(status) && ReadPowerMode(battery) == previous)
            {
                DiscardUnchangedRecords(savedBefore);
                if (row != null) row.Note = ReportPowerRestriction(status);
                ReportUnavailablePowerMode(battery, status);
                return;
            }
            VerifyWindowsResult(status, battery ? "Configurar el modo de energía en batería" : "Configurar el modo de energía enchufado");
            if (ReadPowerMode(battery) != mode)
                throw new IOException("Windows no confirmó el modo de energía configurado.");
            if (row != null) row.Written = true;
        }

        private static uint ReadPlanNameSize(Guid guid)
        {
            uint size = 0;
            uint status = PowerReadFriendlyName(IntPtr.Zero, guid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
            if (status == 2) return 0;
            VerifyWindowsResult(status, $"Consultar el plan de energía {guid}");
            return size;
        }

        private static bool PlanExists(Guid guid) => GetPowerSchemes().Any(scheme => scheme.Id == guid);

        private static string? ReadPlanName(Guid guid)
        {
            uint size = ReadPlanNameSize(guid);
            if (size == 0) return null;
            var buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                uint status = PowerReadFriendlyName(IntPtr.Zero, guid, IntPtr.Zero, IntPtr.Zero, buffer, ref size);
                VerifyWindowsResult(status, $"Leer el nombre del plan {guid}");
                return Marshal.PtrToStringUni(buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private static void RestoreOriginalPowerIndex(Guid plan, string setting, bool battery, bool planBlocked)
        {
            string target = $"{plan}|{setting}|{(battery ? "DC" : "AC")}";
            var original = _changes?.Original.FirstOrDefault(s => s.Type == "power" && s.Target == target);
            if (original != null)
                WritePower(plan, setting, battery, uint.Parse(original.Value!, CultureInfo.InvariantCulture),
                    optional: true, planBlocked: planBlocked);
        }

        private static uint ReadPower(Guid plan, string setting, bool battery)
        {
            uint status = QueryPower(plan, setting, battery, out var value);
            VerifyWindowsResult(status, $"Leer {DescribePowerSetting(setting, battery)}");
            return value;
        }

        private static uint QueryPower(Guid plan, string setting, bool battery, out uint value)
        {
            var (group, id) = PowerSettings[setting];
            return battery
                ? PowerReadDCValueIndex(IntPtr.Zero, plan, group, id, out value)
                : PowerReadACValueIndex(IntPtr.Zero, plan, group, id, out value);
        }

        private static string DescribePowerSetting(string setting, bool battery)
        {
            string name = setting switch
            {
                "PROCTHROTTLEMIN" => "el mínimo de CPU",
                "PROCTHROTTLEMAX" => "el máximo de CPU",
                "CPMINCORES" => "el aparcamiento de núcleos",
                ProcessorBoostSetting => "el modo turbo de la CPU",
                "DISKIDLE" => "el tiempo de apagado de disco",
                "STANDBYIDLE" => "el tiempo de suspensión",
                "USB" => "el ahorro de USB",
                "PCIE" => "el ahorro de PCI Express",
                _ => $"el ajuste {setting}"
            };
            return $"{name} {(battery ? "en batería" : "enchufado")}";
        }

        private static bool WritePower(Guid plan, string setting, bool battery, uint value, bool optional = false, bool planBlocked = false)
        {
            uint previous;
            string description = DescribePowerSetting(setting, battery);
            string target = $"{plan}|{setting}|{(battery ? "DC" : "AC")}";
            ProfileSettingAttempt? Track(uint? old, string? note = null) => _changes == null || _profileSettings == null ? null
                : TrackProfileSetting("power", target, char.ToUpperInvariant(description[3]) + description[4..],
                    old.HasValue ? FormatReportPower(setting, old.Value) : null, FormatReportPower(setting, value),
                    () => FormatReportPower(setting, ReadPower(plan, setting, battery)), note);
            if (planBlocked)
            {
                if (_profileSettings != null)
                    Track(QueryPower(plan, setting, battery, out uint current) == 0 ? current : null, BlockedPlan);
                return false;
            }
            try { previous = ReadPower(plan, setting, battery); }
            catch (WindowsPowerException ex)
            {
                Track(null, ReportPowerRestriction(ex.Code));
                if (!optional || !IsOptionalPowerRestriction(ex.Code)) throw;
                _operationNotices?.Add($"Windows no permitió consultar {description} ({ex.Code}).");
                return false;
            }
            var row = Track(previous);
            if (previous == value) return false;
            int savedBefore = RecordedSettingCount;
            Remember("power", target, previous.ToString(CultureInfo.InvariantCulture));
            var (group, id) = PowerSettings[setting];
            uint result = battery
                ? PowerWriteDCValueIndex(IntPtr.Zero, plan, group, id, value)
                : PowerWriteACValueIndex(IntPtr.Zero, plan, group, id, value);
            if (optional && IsOptionalPowerRestriction(result) && ReadPower(plan, setting, battery) == previous)
            {
                DiscardUnchangedRecords(savedBefore);
                if (row != null) row.Note = ReportPowerRestriction(result);
                _operationNotices?.Add($"Windows no permitió modificar {description} ({result}).");
                return false;
            }
            if (row != null && result != 0) row.Note = ReportPowerRestriction(result);
            VerifyWindowsResult(result, $"Guardar {description}");
            if (ReadPower(plan, setting, battery) != value)
                throw new IOException($"Windows no confirmó {description}.");
            if (row != null) row.Written = true;
            return true;
        }

        private static (string State, string StartMode)? ReadService(string service)
        {
            if (!HeavyServices.Contains(service, StringComparer.Ordinal))
                throw new InvalidDataException($"Servicio desconocido: {service}.");
            using var query = new ManagementObjectSearcher(
                $"SELECT State, StartMode FROM Win32_Service WHERE Name = '{service}'");
            using var results = query.Get();
            foreach (ManagementObject item in results)
            {
                using (item) return ((string)item["State"], (string)item["StartMode"]);
            }
            return null;
        }

        private static void SetServiceState(string service, bool running)
        {
            var initial = ReadService(service) ?? throw new IOException($"No se encuentra el servicio {service}.");
            string desired = running ? "Running" : "Stopped";
            var row = _changes == null ? null : TrackProfileService(service, initial.State, desired);
            if (initial.State == desired) return;
            if (initial.State is not ("Running" or "Stopped"))
                throw new IOException($"El servicio {service} está en transición o en pausa. Estado: {initial.State}.");
            Remember("service", service, initial.State);
            RunCommand("sc.exe", $"{(running ? "start" : "stop")} {service}");
            if (!SpinWait.SpinUntil(() =>
                {
                    if (ReadService(service)?.State == desired) return true;
                    Thread.Sleep(150);
                    return false;
                }, TimeSpan.FromSeconds(10)))
                throw new IOException($"Windows no confirmó el estado de {service}.");
            if (row != null) row.Written = true;
        }

        private static string ReadServiceStartMode(string service)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(P("SYSTEM", "CurrentControlSet", "Services", service));
                if (key?.GetValue("Start") is int start)
                {
                    if (start == 4) return "disabled";
                    if (start == 3) return "demand";
                    if (start == 2)
                        return key.GetValue("DelayedAutostart") is 1 ? "delayed-auto" : "auto";
                }
            }
            catch { }

            return ReadService(service)?.StartMode switch
            {
                "Disabled" => "disabled",
                "Manual" => "demand",
                _ => "auto"
            };
        }

        private static void SetServiceStartMode(string service, string mode)
        {
            if (!HeavyServices.Contains(service, StringComparer.Ordinal))
                throw new InvalidDataException($"Servicio desconocido: {service}.");
            if (mode is not ("auto" or "delayed-auto" or "demand" or "disabled"))
                throw new InvalidDataException($"Modo de inicio no válido para {service}: {mode}.");
            if (ReadServiceStartMode(service) == mode) return;

            RunCommand("sc.exe", $"config {service} start= {mode}");
            if (ReadServiceStartMode(service) != mode)
                throw new IOException($"Windows no confirmó el modo de inicio ({mode}) para {service}.");
        }
    }
}
