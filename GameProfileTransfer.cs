using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace WinPulseOptimizer;

internal sealed record PortableGameProfile(
    [property: JsonRequired] string Name,
    [property: JsonRequired] UsageProfile PowerBase,
    [property: JsonRequired] TweakState Adjustments,
    [property: JsonRequired] string[] Executables);

internal sealed record GameProfileImportRow(string Name, string Executables, string Result);

internal sealed record GameProfileImportResult(List<GameProfile> Profiles, GameProfileImportRow[] Rows, int Added, int Replaced);

internal static class GameProfileTransfer
{
    private const string FileFormat = "WinPulse.GameProfiles";
    private const int Version = 1;
    private static readonly StringComparer Compare = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private sealed record ProfileFile([property: JsonRequired] string Format, [property: JsonRequired] int Version,
        [property: JsonRequired] PortableGameProfile[] Profiles);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type == typeof(TweakState))
                foreach (var property in type.Properties) property.IsRequired = true;
        });
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = resolver, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter<UsageProfile>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    internal static void Export(string path, IEnumerable<GameProfile> profiles)
    {
        var data = new ProfileFile(FileFormat, Version, profiles.Select(profile => new PortableGameProfile(
            profile.Name, profile.PowerBase, profile.Adjustments, profile.Executables.Select(exe => exe + ".exe").ToArray())).ToArray());
        string destination = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, data, Json);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static PortableGameProfile[] Read(string path)
    {
        using var stream = File.OpenRead(path);
        var data = JsonSerializer.Deserialize<ProfileFile>(stream, Json);
        if (data?.Format != FileFormat) throw new FormatException("El archivo no contiene una copia de perfiles de WinPulse.");
        if (data.Version != Version) throw new FormatException("Esta versión de WinPulse no admite el formato del archivo.");
        if (data.Profiles is not { Length: > 0 }) throw new FormatException("El archivo no contiene perfiles.");

        var names = new HashSet<string>(Compare);
        var executables = new HashSet<string>(Compare);
        return data.Profiles.Select(profile => ValidateProfile(profile, names, executables)).ToArray();
    }

    private static PortableGameProfile ValidateProfile(PortableGameProfile? profile, ISet<string> names, ISet<string> executables)
    {
        string? name = profile?.Name?.Trim();
        if (name is not { Length: > 0 and <= 48 } || name.Any(char.IsControl))
            throw new FormatException("El archivo contiene un perfil con un nombre no válido.");
        if (!names.Add(name)) throw new FormatException($"El archivo repite el perfil «{name}».");
        if (profile is not { Adjustments: not null, Executables.Length: > 0,
                PowerBase: UsageProfile.GamingMax or UsageProfile.StudyWork or UsageProfile.ChillBattery })
            throw new FormatException($"El perfil «{name}» tiene ajustes incompletos o un plan base no compatible.");

        var normalized = new List<string>();
        foreach (string? input in profile.Executables)
        {
            string? filename = input?.Trim();
            if (string.IsNullOrEmpty(filename) || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new FormatException($"El perfil «{name}» contiene un nombre de ejecutable no válido.");
            string exe = HardwareMonitor.NormalizeExecutableName(filename);
            if (string.IsNullOrWhiteSpace(exe) || exe is "." or "..")
                throw new FormatException($"El perfil «{name}» contiene un nombre de ejecutable vacío.");
            if (normalized.Contains(exe, Compare)) continue;
            if (!executables.Add(exe))
                throw new FormatException($"El archivo asigna «{exe}.exe» a más de un perfil.");
            normalized.Add(exe);
        }
        return profile with { Name = name, Executables = normalized.ToArray() };
    }

    internal static GameProfileImportResult Preview(IEnumerable<GameProfile> existing, IEnumerable<PortableGameProfile> incoming,
        bool replaceNames)
    {
        var merged = existing.ToList();
        var localByName = merged.ToDictionary(profile => profile.Name, Compare);
        var ownerByExecutable = merged.SelectMany(profile => profile.Executables.Select(exe => (exe, profile.Name)))
            .ToDictionary(assignment => assignment.exe, assignment => assignment.Name, Compare);
        var source = incoming.ToArray();
        bool HasSameSettings(PortableGameProfile imported, GameProfile local) =>
            local.PowerBase == imported.PowerBase && local.Adjustments == imported.Adjustments;
        var identical = source.Where(profile => localByName.TryGetValue(profile.Name, out var local) &&
            HasSameSettings(profile, local) && local.Executables.ToHashSet(Compare).SetEquals(profile.Executables))
            .Select(profile => profile.Name).ToHashSet(Compare);
        var candidates = source.Where(profile => !identical.Contains(profile.Name) &&
            (replaceNames || !localByName.ContainsKey(profile.Name))).ToDictionary(profile => profile.Name, Compare);
        var conflicts = new Dictionary<string, string>(Compare);
        bool removed;
        do
        {
            removed = false;
            foreach (var candidate in candidates.Values.ToArray())
            {
                foreach (string exe in candidate.Executables)
                {
                    if (!ownerByExecutable.TryGetValue(exe, out string? owner) || Compare.Equals(owner, candidate.Name) ||
                        candidates.ContainsKey(owner)) continue;
                    conflicts[candidate.Name] = $"{exe}.exe ya pertenece a «{owner}»";
                    candidates.Remove(candidate.Name);
                    removed = true;
                    break;
                }
            }
        } while (removed);

        var rows = new List<GameProfileImportRow>();
        int added = 0, replaced = 0;
        foreach (var profile in source)
        {
            localByName.TryGetValue(profile.Name, out var current);
            string result;
            if (identical.Contains(profile.Name))
                result = "Ya existe con los mismos ajustes";
            else if (current != null && !replaceNames)
                result = "Nombre en uso · se conserva el perfil local";
            else if (conflicts.TryGetValue(profile.Name, out string? conflict))
                result = conflict;
            else
            {
                var imported = new GameProfile(current?.Id ?? Guid.NewGuid(),
                    current != null && HasSameSettings(profile, current) ? current.Revision : Guid.NewGuid(),
                    profile.Name, profile.PowerBase, profile.Adjustments, profile.Executables);
                if (current == null)
                {
                    merged.Add(imported);
                    added++;
                    result = "Se añadirá";
                }
                else
                {
                    merged[merged.FindIndex(profile => profile.Id == current.Id)] = imported;
                    replaced++;
                    result = "Sustituirá al perfil local del mismo nombre";
                }
            }
            rows.Add(new(profile.Name, string.Join(", ", profile.Executables.Select(executable => executable + ".exe")), result));
        }
        return new(merged, rows.ToArray(), added, replaced);
    }
}
