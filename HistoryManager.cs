using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinPulseOptimizer
{
    public sealed record GameProfile(Guid Id, Guid Revision, string Name, UsageProfile PowerBase, TweakState Adjustments,
        string[] Executables, bool PreferDedicatedGpu = false)
    {
        internal string Tag => $"GAME:{Id:N}:{Revision:N}";
        internal bool OwnsTag(string tag) => tag.StartsWith($"GAME:{Id:N}:", StringComparison.Ordinal);
    }

    public record InitialBaseline(
        DateTime CapturedAt,
        string PowerPlan,
        string PowerOverlay,
        bool GameMode,
        bool Hags,
        bool WindowedOpt,
        bool HighResTimer,
        bool DisableGameDvr,
        bool MmcssGaming,
        bool ForegroundPriority,
        bool DisableCoreParking,
        bool SuspendHeavyServices,
        bool DisableUsbPciePowerSave,
        bool DisableNagle,
        bool LimitBackgroundApps,
        double UsedRamGb,
        double TotalRamGb
    );

    public record HistoryEntry(
        string Timestamp,
        string Action,
        string Explanation,
        string SectionName,
        int TabIndex,
        string ControlName,
        string ResultDelta,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ProfileSettingResult[]? ProfileSettings = null
    )
    {
        [JsonIgnore]
        public bool HasProfileSettings => ProfileSettings is { Length: > 0 };
    }

    public class AuditStore
    {
        public string ActiveProfileTag { get; set; } = "";
        public bool AutoGameSwitch { get; set; } = true;
        public bool AutoBatteryChill { get; set; } = true;
        public bool AutoGameOnBattery { get; set; }
        public bool AutoForceDedicatedGpu { get; set; }
        public bool SmartGameScheduling { get; set; }
        public bool AdaptiveMemoryEnabled { get; set; }
        public List<string> AdaptiveMemoryApps { get; set; } = new();
        public bool? AdaptiveMemoryAutomatic { get; set; }
        public DateTimeOffset LastUpdateCheckUtc { get; set; }
        public List<string> IncludedGameExecutables { get; set; } = new();
        public List<string> ExcludedGameExecutables { get; set; } = new();
        public List<string> ExcludedBackgroundExecutables { get; set; } = new();
        public List<GameProfile> GameProfiles { get; set; } = new();
        public BenchmarkResults Benchmarks { get; set; } = new();
        public bool PauseAutomationForBenchmarks { get; set; }
        public InitialBaseline? Baseline { get; set; }
        public List<HistoryEntry> Entries { get; set; } = new();
    }

    public static class HistoryManager
    {
        private static readonly string BaseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPulseOptimizer");

        private static readonly string StorePath = Path.Combine(BaseDir, "audit_history_v3.json");
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static AuditStore Load()
        {
            try
            {
                if (File.Exists(StorePath))
                {
                    string json = File.ReadAllText(StorePath);
                    return JsonSerializer.Deserialize<AuditStore>(json, JsonOpts) ?? new AuditStore();
                }
            }
            catch { }
            return new AuditStore();
        }

        public static bool Save(AuditStore store)
        {
            string temporary = Path.Combine(BaseDir, Guid.NewGuid() + ".tmp");
            try
            {
                Directory.CreateDirectory(BaseDir);
                File.WriteAllText(temporary, JsonSerializer.Serialize(store, JsonOpts));
                File.Move(temporary, StorePath, overwrite: true);
                return true;
            }
            catch
            {
                try { File.Delete(temporary); } catch { }
                return false;
            }
        }

        public static bool SaveActiveProfileTag(string tag)
        {
            var store = Load();
            store.ActiveProfileTag = tag;
            return Save(store);
        }

        internal static void EnsureInitialBaseline(AuditStore store, TweakState state, PowerPlanDetails power, SystemSnapshot snap)
        {
            if (store.Baseline == null)
            {
                store.Baseline = new InitialBaseline(
                    DateTime.Now,
                    power.ActivePlanName,
                    power.WindowsPowerOverlay,
                    state.GameMode,
                    state.Hags,
                    state.WindowedOptimizations,
                    state.HighResTimer,
                    state.DisableGameDvr,
                    state.MmcssGaming,
                    state.ForegroundPriority,
                    state.DisableCoreParking,
                    state.SuspendHeavyServices,
                    state.DisableUsbPciePowerSave,
                    state.DisableNagle,
                    state.LimitBackgroundApps,
                    snap.UsedRamGb,
                    snap.TotalRamGb
                );
                store.Entries.Insert(0, new HistoryEntry(
                    DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
                    "Foto inicial de tu portátil guardada",
                    "La app guardó cómo venía configurado tu ordenador antes de hacer ningún cambio.",
                    "Pestaña: Energía, Gráfica y Planes",
                    0,
                    "",
                    $"RAM inicial: {snap.UsedRamGb:0.00} GB ocupados"
                ));
                Save(store);
            }
        }

        public static List<HistoryEntry> AddEntry(string action, string explanation, string sectionName, int tabIndex,
            string controlName, string resultDelta, out bool saved, ProfileSettingResult[]? profileSettings = null)
        {
            var store = Load();
            store.Entries.Insert(0, new HistoryEntry(
                DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
                action,
                explanation,
                sectionName,
                tabIndex,
                controlName,
                resultDelta,
                profileSettings
            ));
            if (store.Entries.Count > 150) store.Entries.RemoveRange(150, store.Entries.Count - 150);
            saved = Save(store);
            return store.Entries;
        }

        public static bool ClearHistoryKeepBaseline()
        {
            var store = Load();
            store.Entries.Clear();
            return Save(store);
        }
    }
}
