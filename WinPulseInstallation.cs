using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace WinPulseOptimizer
{
    internal static partial class WinPulseInstallation
    {
        private const string ExecutableName = "WinPulseOptimizer.exe";
        private const string ProductName = "WinPulse Pro";
        private static string InstallDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName);
        internal static string InstalledExecutable => Path.Combine(InstallDirectory, ExecutableName);

        internal static bool IsInstalledExecutable(string path) =>
            string.Equals(Path.GetFullPath(path), InstalledExecutable, StringComparison.OrdinalIgnoreCase);

        internal static bool NeedsUpdate(Version version) =>
            !File.Exists(InstalledExecutable) || ReadVersion(InstalledExecutable) < version;

        private static T LockInstallation<T>(Func<T> action)
        {
            using var mutex = new Mutex(false, @"Global\WinPulseOptimizer.Installation");
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(20)))
                    throw new IOException("Ya hay una instalación de WinPulse en curso.");
            }
            catch (AbandonedMutexException) { }

            try { return action(); }
            finally { mutex.ReleaseMutex(); }
        }

        internal static IReadOnlyList<string> Install(string source, Version version) =>
            LockInstallation<IReadOnlyList<string>>(() =>
            {
                string target = InstalledExecutable;
                EnsureRegularPath(InstallDirectory);
                Directory.CreateDirectory(InstallDirectory);
                EnsureRegularPath(target);
                CheckLegacyInstances();

                if (!File.Exists(target) || ReadVersion(target) < version)
                    InstallExecutable(source, target);

                var notices = new List<string>();
                try { RegisterInstalledApp(target); }
                catch (Exception ex) { notices.Add($"No se pudo registrar WinPulse en Aplicaciones instaladas. {ex.Message}"); }
                try { CreateStartMenuShortcut(target); }
                catch (Exception ex) { notices.Add($"No se pudo actualizar el acceso directo de Inicio. {ex.Message}"); }
                try { OptimizerEngine.UpdateScheduledLaunchPath(target); }
                catch (Exception ex) { notices.Add($"No se pudo verificar el inicio con Windows. {ex.Message}"); }
                return notices;
            });

        private static Version ReadVersion(string filename)
        {
            var version = FileVersionInfo.GetVersionInfo(filename);
            return new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart);
        }

        private static void EnsureRegularPath(string path)
        {
            if ((Directory.Exists(path) || File.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"La ruta de instalación está redirigida: {path}.");
        }

        private static void CheckLegacyInstances()
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName)))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    string? path;
                    try { path = process.MainModule?.FileName; }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { continue; }
                    if (path != null && !IsInstalledExecutable(path) && File.Exists(path) &&
                        FileVersionInfo.GetVersionInfo(path).ProductName == "WinPulseOptimizer" &&
                        ReadVersion(path) < new Version(1, 1, 7, 0))
                        throw new IOException("Una versión anterior de WinPulse sigue abierta. Usa Salir por completo en su icono junto al reloj.");
                }
            }
        }

        private static void InstallExecutable(string source, string target)
        {
            string temporary = Path.Combine(Path.GetDirectoryName(target)!, $"{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(source, temporary);
                if (File.Exists(target))
                {
                    var reply = InstanceCoordinator.SendAsync(InstanceRequest.PrepareUpgrade).GetAwaiter().GetResult();
                    if (reply == InstanceReply.Busy)
                        throw new IOException("WinPulse está aplicando un cambio o realizando una medición. Espera a que termine y abre de nuevo la actualización.");
                }

                var timer = Stopwatch.StartNew();
                bool terminatedOtherSessions = false;
                while (true)
                {
                    try { File.Move(temporary, target, overwrite: true); return; }
                    catch (Exception ex) when ((ex.HResult & 0xffff) is 5 or 32 or 33)
                    {
                        if (!terminatedOtherSessions && timer.Elapsed >= TimeSpan.FromSeconds(2))
                        {
                            terminatedOtherSessions = true;
                            TerminateRemainingInstalledProcesses();
                        }
                        if (timer.Elapsed >= TimeSpan.FromSeconds(8))
                        {
                            string message = HasLiveInstalledProcess()
                                ? "La copia instalada sigue en uso. Cierra WinPulse por completo y abre de nuevo la actualización."
                                : "Windows mantiene bloqueado el ejecutable anterior tras una suspensión o cierre previo (sin proceso activo). Usa «Inicio → Reiniciar» en Windows y vuelve a ejecutar la actualización.";
                            throw new IOException(message, ex);
                        }
                        Thread.Sleep(150);
                    }
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static void TerminateRemainingInstalledProcesses()
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName)))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    try
                    {
                        if (process.HasExited || process.Threads.Count == 0) continue;
                        string? path = process.MainModule?.FileName;
                        if (path != null && !IsInstalledExecutable(path)) continue;
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                    catch { }
                }
            }
        }

        internal static bool HasLiveInstalledProcess()
        {
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName)))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    try
                    {
                        if (!process.HasExited && process.Threads.Count > 0)
                        {
                            string? path = process.MainModule?.FileName;
                            if (path == null || IsInstalledExecutable(path)) return true;
                        }
                    }
                    catch { }
                }
            }
            return false;
        }

        private static void CreateStartMenuShortcut(string target)
        {
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            Directory.CreateDirectory(programs);
            string shortcutPath = Path.Combine(programs, "WinPulse Pro.lnk");
            string temporary = Path.Combine(programs, $"{Guid.NewGuid():N}.lnk");
            object? shell = null;
            object? shortcut = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!);
                shortcut = ((dynamic)shell!).CreateShortcut(temporary);
                dynamic settings = shortcut!;
                settings.TargetPath = target;
                settings.WorkingDirectory = Path.GetDirectoryName(target)!;
                settings.IconLocation = $"{target},0";
                settings.Description = "WinPulse Pro";
                settings.Save();
                File.Move(temporary, shortcutPath, overwrite: true);
            }
            finally
            {
                if (shortcut != null) Marshal.FinalReleaseComObject(shortcut);
                if (shell != null) Marshal.FinalReleaseComObject(shell);
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        internal static void LaunchInstalled(bool minimized)
        {
            var start = new ProcessStartInfo(InstalledExecutable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(InstalledExecutable)!
            };
            if (minimized) start.ArgumentList.Add("--minimized");
            using var process = Process.Start(start) ?? throw new IOException("No se pudo abrir WinPulse instalado.");
        }
    }
}
