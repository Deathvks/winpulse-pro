using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using Microsoft.Win32;

namespace WinPulseOptimizer
{
    internal sealed record TrackedSetting(string Type, string Target, string Name, string? Requested, string? Problem = null);
    internal sealed record RestartBaseline(string Target, string? Value, DateTime? BootUtc);

    internal enum SettingState { PendingRestart, Blocked, Different, Unavailable, InactivePlan, Read, Verified }

    internal sealed record SettingStatusRow(string Name, string Requested, string Actual, SettingState State, string Status, bool IsBlocked);
    internal sealed record SettingStatusSnapshot(DateTime CheckedAt, SettingStatusRow[] Rows, bool RecoveryPending, bool HasTracking);

    public static partial class OptimizerEngine
    {
        private static ChangeStore? _statusStore;
        private static readonly Lazy<DateTime?> WindowsBoot = new(ReadWindowsBootTime);
        private sealed record SettingReading(string Display, string? Raw = null);

        private static DateTime? ReadWindowsBootTime()
        {
            try
            {
                using var query = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
                using var results = query.Get();
                foreach (ManagementObject row in results)
                {
                    using (row)
                        if (row["LastBootUpTime"] is string value)
                            return ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime();
                }
            }
            catch (Exception) { }
            return null;
        }

        private static void RememberRestartBaseline(string target, string? previous)
        {
            if (_statusStore is not { } store || !NeedsRestart(target) || IsOverlay(target)) return;
            var boot = WindowsBoot.Value;
            int index = store.RestartBaselines.FindIndex(row => SameTarget(row.Target, target));
            if (index >= 0)
            {
                var existing = store.RestartBaselines[index];
                if (!HasNewBoot(existing.BootUtc, boot))
                {
                    if (existing.BootUtc == null && boot != null)
                    {
                        store.RestartBaselines[index] = existing with { BootUtc = boot };
                        SystemChangeJournal.Save(store);
                    }
                    return;
                }
                store.RestartBaselines[index] = new(target, previous, boot);
            }
            else store.RestartBaselines.Add(new(target, previous, boot));
            SystemChangeJournal.Save(store);
        }

        private static bool SameTarget(string left, string right) =>
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        private static bool HasNewBoot(DateTime? previous, DateTime? current) =>
            previous.HasValue && current.HasValue && current.Value > previous.Value;

        internal static bool IsPendingRestart(RestartBaseline baseline, DateTime? boot, string? value) =>
            !HasNewBoot(baseline.BootUtc, boot) && baseline.Value != value;

        private static bool HasPendingRestart(ChangeStore store, IEnumerable<SavedSetting> changes) =>
            changes.Any(change => change.Type == "registry" &&
                store.RestartBaselines.FirstOrDefault(row => SameTarget(row.Target, change.Target)) is { } baseline &&
                ReadRegistryStatus(change.Target) is { } actual && IsPendingRestart(baseline, WindowsBoot.Value, actual.Raw));

        private static bool IsBlocked(string? reason) => reason != null &&
            (reason.StartsWith("Bloqueado", StringComparison.OrdinalIgnoreCase) ||
             reason.StartsWith("Acceso denegado", StringComparison.OrdinalIgnoreCase) ||
             reason.StartsWith("Deshabilitado en Windows", StringComparison.OrdinalIgnoreCase));

        private static void RecordTrackedSetting(ChangeStore store, TrackedSetting setting, bool overwrite = true)
        {
            int index = store.LatestSettings.FindIndex(item => item.Type == setting.Type && SameTarget(item.Target, setting.Target));
            if (index < 0) store.LatestSettings.Add(setting);
            else if (overwrite) store.LatestSettings[index] = setting;
        }

        private static void RecordUntrackedSettings(ChangeStore store, IEnumerable<SavedSetting> settings, bool overwrite = false)
        {
            foreach (var entry in settings)
            {
                if (!overwrite && store.LatestSettings.Any(item => item.Type == entry.Type && SameTarget(item.Target, entry.Target)))
                    continue;
                if (DescribeTrackedSetting(entry) is { } setting)
                    RecordTrackedSetting(store, overwrite ? setting with { Requested = DescribeSavedValue(entry) } : setting, overwrite);
            }
        }

        private static string? DescribeSavedValue(SavedSetting entry) => ReadReportValue(() => entry.Type switch
        {
            "registry" => FormatReportRegistry(entry.Value),
            "activePlan" when Guid.TryParse(entry.Value, out Guid plan) => FormatReportPlan(plan),
            "powerMode" when Guid.TryParse(entry.Value, out Guid mode) => FormatReportMode(mode),
            "power" when uint.TryParse(entry.Value, out uint index) => FormatReportPower(entry.Target.Split('|')[1], index),
            "service" when entry.Value is { } state => FormatReportService(state),
            "task" => entry.Value == "Enabled" ? "Activado" : "Desactivado",
            "ethernet" => entry.Value == "1" ? "Activado" : "Desactivado",
            _ => null
        });

        private static TrackedSetting? DescribeTrackedSetting(SavedSetting entry)
        {
            var parts = entry.Target.Split('|');
            string? name = entry.Type switch
            {
                "activePlan" => "Plan de energía activo",
                "powerMode" => $"Modo de energía de Windows · {(entry.Target == "DC" ? "batería" : "enchufado")}",
                "power" when parts.Length == 3 => DescribePowerSetting(parts[1], parts[2] == "DC")[3..],
                "registry" when parts.Length == 3 && CanRestoreRegistry(parts[0], parts[1], parts[2]) =>
                    parts[1].StartsWith(InterfacesPath + Sep, StringComparison.OrdinalIgnoreCase)
                        ? $"{parts[2]} · {ReportInterfaceName(parts[1][(InterfacesPath.Length + 1)..])}" : ReportRegistryName(parts[2]),
                "service" when HeavyServices.Contains(entry.Target) => $"Servicio {entry.Target}",
                "task" when entry.Target == AutoStartTask => "Iniciar WinPulse con Windows",
                "ethernet" when parts.Length == 2 && parts[1] == "*InterruptModeration" =>
                    $"Moderación de interrupciones · {ReportInterfaceName(parts[0])}",
                "ethernet" when parts.Length == 2 && parts[1] == "*EEE" => $"Ethernet de bajo consumo · {ReportInterfaceName(parts[0])}",
                _ => null
            };
            return name == null ? null : new(entry.Type, entry.Target, char.ToUpperInvariant(name[0]) + name[1..], null);
        }

        internal static SettingStatusSnapshot ReadSettingStatus()
        {
            var store = SystemChangeJournal.Load();
            bool tracked = store.LatestSettings.Count > 0 || store.RestartBaselines.Count > 0;
            // Older versions only saved the values needed for undo.
            RecordUntrackedSettings(store, store.Undo.SelectMany(operation => operation.Settings));
            RecordUntrackedSettings(store, store.RestartBaselines.Select(item => new SavedSetting("registry", item.Target, item.Value)));
            RecordTrackedSetting(store, new("activePlan", "", "Plan de energía activo", null), overwrite: false);

            DateTime? boot = store.RestartBaselines.Count == 0 ? null : WindowsBoot.Value;
            Guid? activePlan;
            try { activePlan = ActivePlan(); } catch (Exception) { activePlan = null; }
            var adapters = new Lazy<EthernetAdapter[]>(GetEthernetAdapters);
            var rows = new List<SettingStatusRow>(store.LatestSettings.Count);
            foreach (var trackedSetting in store.LatestSettings)
            {
                var actual = ReadLiveSetting(trackedSetting, activePlan, adapters);
                bool pending = actual != null && trackedSetting.Type == "registry" &&
                    store.RestartBaselines.FirstOrDefault(row => SameTarget(row.Target, trackedSetting.Target)) is { } baseline &&
                    IsPendingRestart(baseline, boot, actual.Raw);
                bool inactivePlan = trackedSetting.Type == "power" && activePlan.HasValue &&
                    !trackedSetting.Target.StartsWith($"{activePlan.Value}|", StringComparison.OrdinalIgnoreCase);
                rows.Add(FormatSettingStatus(trackedSetting, actual?.Display, pending, inactivePlan));
            }
            return new(DateTime.Now, rows.OrderBy(row => row.State).ThenBy(row => row.Name).ToArray(),
                store.Pending != null || store.RestoringOriginal.HasValue, tracked);
        }

        internal static SettingStatusRow FormatSettingStatus(TrackedSetting setting, string? actual, bool pending, bool inactivePlan)
        {
            bool blocked = IsBlocked(setting.Problem);
            var (state, text) = (pending, blocked, inactivePlan, actual, setting.Requested) switch
            {
                (true, true, _, _, _) => (SettingState.PendingRestart, $"Pendiente de reinicio · {setting.Problem}"),
                (true, _, _, _, _) => (SettingState.PendingRestart, "Pendiente de reinicio"),
                (_, true, _, _, _) => (SettingState.Blocked, setting.Problem!),
                (_, _, true, _, _) => (SettingState.InactivePlan, "Plan de energía inactivo"),
                (_, _, _, null, _) => (SettingState.Unavailable, setting.Problem ?? "Sin verificar"),
                (_, _, _, _, null) => (SettingState.Read, "Valor leído"),
                (_, _, _, var value, var expected) when value == expected => (SettingState.Verified, "Verificado"),
                _ => (SettingState.Different, "Distinto de lo solicitado")
            };
            string name = inactivePlan && blocked ? $"{setting.Name} · plan inactivo" : setting.Name;
            return new(name, setting.Requested ?? "—", actual ?? "No disponible", state, text, blocked);
        }

        private static SettingReading? ReadRegistryStatus(string target)
        {
            try
            {
                var parts = target.Split('|', 3);
                if (parts.Length != 3 || !CanRestoreRegistry(parts[0], parts[1], parts[2])) return null;
                var root = parts[0] == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
                using var key = root.OpenSubKey(parts[1]);
                if (key == null && parts[1].StartsWith(InterfacesPath + Sep, StringComparison.OrdinalIgnoreCase)) return null;
                string? raw = ReadRegistryValue(key, parts[2]);
                return new(FormatReportRegistry(raw), raw);
            }
            catch (Exception) { return null; }
        }

        private static SettingReading? ReadLiveSetting(TrackedSetting setting, Guid? activePlan, Lazy<EthernetAdapter[]> adapters)
        {
            if (setting.Type == "registry") return ReadRegistryStatus(setting.Target);
            var parts = setting.Target.Split('|');
            string? display = ReadReportValue(() => setting.Type switch
            {
                "activePlan" when activePlan is Guid plan => FormatReportPlan(plan),
                "powerMode" when setting.Target is "AC" or "DC" =>
                    ReadPowerMode(setting.Target == "DC") is Guid mode ? FormatReportMode(mode) : null,
                "power" when parts.Length == 3 && Guid.TryParse(parts[0], out var plan) =>
                    FormatReportPower(parts[1], ReadPower(plan, parts[1], parts[2] == "DC")),
                "service" => ReadService(setting.Target)?.State is { } state ? FormatReportService(state) : null,
                "task" when setting.Target == AutoStartTask => ReadAutoStart() == "Enabled" ? "Activado" : "Desactivado",
                "ethernet" when parts.Length == 2 && Guid.TryParse(parts[0], out var adapterId) =>
                    adapters.Value.FirstOrDefault(adapter => adapter.Id == adapterId)?.GetOption(parts[1] switch
                    {
                        "*EEE" => EthernetOption.EnergyEfficientEthernet,
                        "*InterruptModeration" => EthernetOption.InterruptModeration,
                        _ => throw new ArgumentException()
                    }) switch { true => "Activado", false => "Desactivado", _ => null },
                _ => null
            });
            return display == null ? null : new(display);
        }
    }
}
