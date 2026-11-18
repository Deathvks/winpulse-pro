using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace WinPulseOptimizer;

public sealed record DedicatedGpuGameItem(string ExecutablePath, string FileName);

public static partial class OptimizerEngine
{
    private static readonly string UserGpuPreferencesPath = P("Software", "Microsoft", "DirectX", "UserGpuPreferences");

    public static List<DedicatedGpuGameItem> GetDedicatedGpuGames()
    {
        var list = new List<DedicatedGpuGameItem>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UserGpuPreferencesPath);
            if (key == null) return list;

            foreach (string valueName in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName) ||
                    string.Equals(valueName, "DirectXUserGlobalSettings", StringComparison.OrdinalIgnoreCase) ||
                    !Path.IsPathFullyQualified(valueName) ||
                    !string.Equals(Path.GetExtension(valueName), ".exe", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string raw = key.GetValue(valueName)?.ToString() ?? "";
                if (HasGpuPreferenceToken(raw, "2"))
                    list.Add(new DedicatedGpuGameItem(valueName, Path.GetFileName(valueName)));
            }
        }
        catch { }

        return list
            .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool SetDedicatedGpuForExecutable(string executablePath, bool enable)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath) ||
            !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Selecciona un archivo ejecutable (.exe) con ruta completa.");
        }

        string normalized = Path.GetFullPath(executablePath);
        if (enable && !File.Exists(normalized))
            throw new FileNotFoundException("No se encontró el ejecutable seleccionado.", normalized);

        using var key = Registry.CurrentUser.CreateSubKey(UserGpuPreferencesPath, writable: true)
            ?? throw new IOException("No se pudo abrir la clave de preferencias gráficas de Windows.");

        string targetName = key.GetValueNames()
            .FirstOrDefault(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase))
            ?? normalized;

        string currentRaw = key.GetValue(targetName)?.ToString() ?? "";
        var remainingTokens = SplitGpuPreferenceTokens(currentRaw)
            .Where(token => !token.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (enable)
        {
            if (HasGpuPreferenceToken(currentRaw, "2")) return false;
            remainingTokens.Insert(0, "GpuPreference=2");
            string updated = string.Join(";", remainingTokens) + ";";
            key.SetValue(targetName, updated, RegistryValueKind.String);
            return true;
        }

        if (key.GetValue(targetName) == null) return false;
        if (remainingTokens.Count == 0)
            key.DeleteValue(targetName, throwOnMissingValue: false);
        else
            key.SetValue(targetName, string.Join(";", remainingTokens) + ";", RegistryValueKind.String);
        return true;
    }

    internal static string? TryGetProcessExecutablePath(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            string? path = proc.MainModule?.FileName;
            return !string.IsNullOrEmpty(path) &&
                string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
                ? path : null;
        }
        catch { return null; }
    }

    private static IEnumerable<string> SplitGpuPreferenceTokens(string raw) =>
        raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool HasGpuPreferenceToken(string raw, string value) =>
        SplitGpuPreferenceTokens(raw).Any(token =>
            string.Equals(token, $"GpuPreference={value}", StringComparison.OrdinalIgnoreCase));
}
