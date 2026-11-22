using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace WinPulseOptimizer
{
    internal static partial class WinPulseInstallation
    {
        private const string RegistrationPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\WinPulsePro";
        private const string HelperPrefix = "Uninstall-";
        private const uint DelayUntilReboot = 4;
        private const string RegisteredHelper = "WinPulseUninstaller";

        private static RegistryKey OpenRegistrationRoot() =>
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

        internal static bool HasInstalledAppRegistration()
        {
            try
            {
                using var root = OpenRegistrationRoot();
                using var registration = root.OpenSubKey(RegistrationPath);
                return registration?.GetValue(RegisteredHelper) is string helper && IsUninstallerLocation(helper) &&
                    File.Exists(helper) && registration.GetValue("DisplayVersion") is string version &&
                    File.Exists(InstalledExecutable) && version == ReadVersion(InstalledExecutable).ToString(3) &&
                    registration.GetValue("UninstallString") as string == CommandForUninstall(helper);
            }
            catch (Exception) { return false; }
        }

        internal static bool IsRegisteredUninstaller(string source)
        {
            if (!IsUninstallerLocation(source)) return false;
            using var root = OpenRegistrationRoot();
            using var registration = root.OpenSubKey(RegistrationPath);
            return string.Equals(registration?.GetValue(RegisteredHelper) as string, source, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsUninstallerLocation(string path)
        {
            if (!Path.IsPathFullyQualified(path) || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), InstallDirectory,
                StringComparison.OrdinalIgnoreCase)) return false;
            string name = Path.GetFileNameWithoutExtension(path);
            return name.StartsWith(HelperPrefix, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(name[HelperPrefix.Length..], "N", out _) &&
                string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);
        }

        private static string CommandForUninstall(string helper) => $"\"{helper}\" --uninstall";

        private static void RegisterInstalledApp(string target)
        {
            var version = ReadVersion(target);
            using var root = OpenRegistrationRoot();
            using var registration = root.CreateSubKey(RegistrationPath, writable: true);
            string? oldHelper = registration.GetValue(RegisteredHelper) as string;
            bool reuse = oldHelper != null && IsUninstallerLocation(oldHelper) &&
                File.Exists(oldHelper) && ReadVersion(oldHelper) == version;
            string helper = reuse ? oldHelper! : Path.Combine(InstallDirectory, $"{HelperPrefix}{Guid.NewGuid():N}.exe");
            EnsureRegularPath(helper);
            if (!reuse) File.Copy(target, helper, overwrite: false);

            registration.SetValue("DisplayName", ProductName);
            registration.SetValue("DisplayVersion", version.ToString(3));
            registration.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            registration.SetValue("InstallLocation", InstallDirectory);
            registration.SetValue("DisplayIcon", $"\"{target}\",0");
            registration.SetValue("NoModify", 1, RegistryValueKind.DWord);
            registration.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            registration.SetValue("EstimatedSize", (int)Math.Ceiling(
                (new FileInfo(target).Length + new FileInfo(helper).Length) / 1024.0), RegistryValueKind.DWord);
            registration.SetValue("UninstallString", CommandForUninstall(helper));
            registration.SetValue(RegisteredHelper, helper);
            registration.Flush();

            if (!reuse && oldHelper != null && IsUninstallerLocation(oldHelper))
            {
                try { File.Delete(oldHelper); }
                catch (IOException) { MoveFileEx(oldHelper, null, DelayUntilReboot); }
            }
        }

        internal static bool Uninstall(string helper) => LockInstallation(() =>
        {
            if (!IsRegisteredUninstaller(helper))
                throw new IOException("Este desinstalador ya fue reemplazado. Abre Aplicaciones instaladas de nuevo.");
            EnsureRegularPath(InstallDirectory);
            EnsureRegularPath(helper);
            EnsureRegularPath(InstalledExecutable);
            var reply = InstanceCoordinator.SendAsync(InstanceRequest.PrepareUpgrade).GetAwaiter().GetResult();
            if (reply == InstanceReply.Busy)
                throw new IOException("Espera a que WinPulse termine la operación actual y vuelve a desinstalar.");

            DeleteInstalledExecutable();
            OptimizerEngine.RemoveInstalledAutoStart(InstalledExecutable);
            RemoveStartMenuShortcut();
            CleanupStaleTrayIconEntries(removeAll: true);
            using var root = OpenRegistrationRoot();
            root.DeleteSubKey(RegistrationPath, throwOnMissingSubKey: false);

            // Windows still has the current helper mapped until it exits. Its unique name cannot
            // refer to a copy installed later.
            if (!MoveFileEx(helper, null, DelayUntilReboot)) return false;
            MoveFileEx(InstallDirectory, null, DelayUntilReboot);
            return true;
        });

        private static void DeleteInstalledExecutable()
        {
            if (File.Exists(InstalledExecutable) &&
                FileVersionInfo.GetVersionInfo(InstalledExecutable).ProductName != "WinPulseOptimizer")
                throw new IOException("El ejecutable de la ruta de instalación no corresponde a WinPulse.");
            var timer = Stopwatch.StartNew();
            bool terminatedOtherSessions = false;
            while (true)
            {
                try { File.Delete(InstalledExecutable); return; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (!terminatedOtherSessions && timer.Elapsed >= TimeSpan.FromSeconds(2))
                    {
                        terminatedOtherSessions = true;
                        TerminateRemainingInstalledProcesses();
                    }
                    if (timer.Elapsed >= TimeSpan.FromSeconds(6))
                    {
                        string message = HasLiveInstalledProcess()
                            ? "Cierra WinPulse con «Salir por completo» en el icono junto al reloj y vuelve a desinstalar."
                            : "Windows mantiene bloqueado el ejecutable tras una suspensión o cierre previo (sin proceso activo). Usa «Inicio → Reiniciar» en Windows y vuelve a desinstalar.";
                        throw new IOException(message, ex);
                    }
                    Thread.Sleep(150);
                }
            }
        }

        private static void RemoveStartMenuShortcut()
        {
            RemoveStartMenuShortcut(Environment.SpecialFolder.CommonPrograms);
            RemoveStartMenuShortcut(Environment.SpecialFolder.Programs);
        }

        private static void RemoveStartMenuShortcut(Environment.SpecialFolder folder)
        {
            string path = Path.Combine(Environment.GetFolderPath(folder), "WinPulse Pro.lnk");
            if (!File.Exists(path)) return;
            object? shell = null;
            object? shortcut = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
                shortcut = ((dynamic)shell!).CreateShortcut(path);
                string target = ((dynamic)shortcut!).TargetPath;
                if (Path.IsPathFullyQualified(target) && IsInstalledExecutable(target))
                    File.Delete(path);
            }
            finally
            {
                if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null) Marshal.FinalReleaseComObject(shell);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileEx(string existingFile, string? newFile, uint flags);
    }
}
