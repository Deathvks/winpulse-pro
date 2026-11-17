using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WinPulseOptimizer
{
    internal sealed record SavedSetting(string Type, string Target, string? Value)
    {
        public bool Matches(SavedSetting other) => Type == other.Type &&
            string.Equals(Target, other.Target, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record SavedOperation(string Title, string PreviousProfile, List<SavedSetting> Settings,
        Guid Id = default, string? AutomaticProfile = null);

    internal sealed class ChangeStore
    {
        public List<SavedOperation> Undo { get; set; } = new();
        public List<SavedSetting> Original { get; set; } = new();
        public SavedOperation? Pending { get; set; }
        public bool? RestoringOriginal { get; set; }
        public Guid? AutomationOperationId { get; set; }
        public string? OriginalProfile { get; set; }
        public List<TrackedSetting> LatestSettings { get; set; } = new();
        public List<RestartBaseline> RestartBaselines { get; set; } = new();
    }

    internal static class SystemChangeJournal
    {
        private static readonly string PathOnDisk = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinPulseOptimizer", "system_changes_v1.json");

        public static ChangeStore Load() => File.Exists(PathOnDisk)
            ? JsonSerializer.Deserialize<ChangeStore>(File.ReadAllText(PathOnDisk))
                ?? throw new InvalidDataException("El registro de restauración está vacío.")
            : new ChangeStore();

        public static void Save(ChangeStore store)
        {
            var directory = Path.GetDirectoryName(PathOnDisk)!;
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, Guid.NewGuid() + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, store);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, PathOnDisk, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        public static void Remember(ChangeStore store, SavedSetting previous)
        {
            var operation = store.Pending ?? throw new InvalidOperationException("No hay una operación en curso.");
            if (operation.Settings.Any(previous.Matches)) return;
            operation.Settings.Add(previous);
            Save(store);
        }

        public static void Commit(ChangeStore store, bool replaceAutomation = false)
        {
            var operation = store.Pending!;
            var committed = new ChangeStore
            {
                Undo = new List<SavedOperation>(store.Undo),
                Original = new List<SavedSetting>(store.Original),
                OriginalProfile = store.OriginalProfile,
                AutomationOperationId = replaceAutomation ? null : store.AutomationOperationId,
                LatestSettings = store.LatestSettings,
                RestartBaselines = store.RestartBaselines
            };
            if (operation.Settings.Count != 0)
            {
                committed.OriginalProfile ??= operation.PreviousProfile;
                foreach (var saved in operation.Settings)
                    if (!committed.Original.Any(saved.Matches))
                        committed.Original.Add(saved);
            }
            if (operation.Settings.Count != 0 || operation.AutomaticProfile != null)
                committed.Undo.Add(operation);
            if (operation.AutomaticProfile != null) committed.AutomationOperationId = operation.Id;
            Save(committed);
        }
    }
}
