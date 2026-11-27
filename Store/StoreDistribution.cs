using System;
using System.IO;
using System.Runtime.InteropServices;

namespace WinPulseOptimizer;

internal static class StoreDistribution
{
    private const int AppModelErrorNoPackage = 15700;
    private const int ErrorInsufficientBuffer = 122;
    private const string AppExecutionAliasFileName = "WinPulseOptimizer.exe";

    internal static bool IsStoreBuild { get; } = DetectStoreBuild();

    private static bool DetectStoreBuild()
    {
#if WINPULSE_STORE
        return true;
#else
        return IsRunningInMsixPackage();
#endif
    }

    internal static bool IsRunningInMsixPackage()
    {
        try
        {
            uint length = 0;
            int status = GetCurrentPackageFullName(ref length, null);
            return status != AppModelErrorNoPackage && (status == ErrorInsufficientBuffer || status == 0);
        }
        catch
        {
            return false;
        }
    }

    internal static string ResolveAutoStartExecutablePath(
        string? processPath,
        bool isStoreBuild,
        string? localAppData = null,
        Func<string, bool>? fileExists = null)
    {
        if (isStoreBuild)
        {
            string appData = localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(appData))
            {
                string aliasPath = Path.Combine(appData, "Microsoft", "WindowsApps", AppExecutionAliasFileName);
                if (fileExists?.Invoke(aliasPath) ?? File.Exists(aliasPath))
                    return aliasPath;
            }
        }

        return processPath ?? throw new IOException("No se encontró el ejecutable de WinPulse.");
    }

    internal static string ResolveAutoStartExecutablePath(string? processPath) =>
        ResolveAutoStartExecutablePath(processPath, IsStoreBuild);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
