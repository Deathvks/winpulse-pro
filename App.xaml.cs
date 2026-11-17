using System;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;

namespace WinPulseOptimizer;

public partial class App : Application
{
    private InstanceCoordinator? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool minimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        bool installOnly = e.Args.Contains("--install-only", StringComparer.OrdinalIgnoreCase);

        try
        {
            if (e.Args.FirstOrDefault() == WindowsElevation.WorkerFlag)
            {
                try { Shutdown(await WindowsElevation.RunWorkerAsync(e.Args)); }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); Shutdown(1); }
                return;
            }
#if WINPULSE_INSTALLER
            string source = Environment.ProcessPath ?? throw new IOException("No se encontró el ejecutable de WinPulse.");
            var version = typeof(App).Assembly.GetName().Version!;
            if (e.Args is ["--uninstall"])
            {
                Shutdown(await UninstallAsync(source));
                return;
            }
            if (e.Args is ["--uninstall-worker", var uninstallUser])
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (!WindowsElevation.IsAdministrator || identity.User?.Value != uninstallUser)
                    throw new UnauthorizedAccessException("Autoriza la desinstalación con la misma cuenta que abrió WinPulse.");
                Shutdown(await RemoveInstalledAppAsync(source));
                return;
            }
            if (WinPulseInstallation.IsUninstallerLocation(source))
            {
                Shutdown(1);
                return;
            }
            if (e.Args is ["--install-worker", var requestedUser])
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (!WindowsElevation.IsAdministrator || identity.User?.Value != requestedUser ||
                    WinPulseInstallation.IsInstalledExecutable(source))
                {
                    Report("Autoriza la instalación con la misma cuenta de Windows que abrió WinPulse.", false, MessageBoxImage.Error);
                    Shutdown(1);
                    return;
                }
                var installNotices = await Task.Run(() => WinPulseInstallation.Install(source, version));
                if (installNotices.Count > 0)
                    Report(string.Join(Environment.NewLine + Environment.NewLine, installNotices), false, MessageBoxImage.Warning);
                Shutdown(installNotices.Count == 0 ? 0 : 2);
                return;
            }
            if (!WinPulseInstallation.IsInstalledExecutable(source))
            {
                int status = 0;
                bool alreadyInstalled = File.Exists(WinPulseInstallation.InstalledExecutable);
                bool needsUpdate = WinPulseInstallation.NeedsUpdate(version);
                if (needsUpdate || installOnly || !WinPulseInstallation.HasInstalledAppRegistration())
                {
                    if (!installOnly && new InstallationWindow(alreadyInstalled, version).ShowDialog() != true)
                    {
                        Shutdown();
                        return;
                    }

                    if (WindowsElevation.IsAdministrator)
                    {
                        var notices = await Task.Run(() => WinPulseInstallation.Install(source, version));
                        if (notices.Count > 0)
                            Report(string.Join(Environment.NewLine + Environment.NewLine, notices), installOnly, MessageBoxImage.Warning);
                        status = notices.Count == 0 ? 0 : 2;
                    }
                    else
                    {
                        if (alreadyInstalled && needsUpdate &&
                            await InstanceCoordinator.SendAsync(InstanceRequest.PrepareUpgrade) == InstanceReply.Busy)
                            throw new IOException("Espera a que termine la operación actual de WinPulse antes de actualizar.");
                        using var identity = WindowsIdentity.GetCurrent();
                        string sid = identity.User?.Value ?? throw new IOException("No se pudo identificar la cuenta de Windows.");
                        status = await Task.Run(async () =>
                        {
                            using var installer = WindowsElevation.StartElevated(source, $"--install-worker {sid}");
                            await installer.WaitForExitAsync();
                            return installer.ExitCode;
                        });
                    }
                }
                if (!installOnly && status is 0 or 2) WinPulseInstallation.LaunchInstalled(minimized);
                Shutdown(status);
                return;
            }
#endif
            if (installOnly)
            {
                Shutdown();
                return;
            }

            _instance = InstanceCoordinator.TryAcquire();
            if (_instance == null)
            {
                if (!minimized && await InstanceCoordinator.SendAsync(InstanceRequest.Activate) == InstanceReply.Unavailable)
                    Report("WinPulse ya está abierto. Puedes abrir su ventana desde el icono junto al reloj.",
                        silent: false, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            _instance.Listen(
                request => Dispatcher.InvokeAsync(() => HandleInstanceRequest(request)).Task,
                () => Dispatcher.BeginInvoke(new Action(() => (MainWindow as MainWindow)?.ExitCompletely())));
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (OperationCanceledException ex)
        {
            Report(ex.Message, installOnly || minimized, MessageBoxImage.Information);
            Shutdown(1);
        }
        catch (Exception ex)
        {
            Report(ex.Message, installOnly || minimized, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

#if WINPULSE_INSTALLER
    private static async Task<int> UninstallAsync(string source)
    {
        if (!WinPulseInstallation.IsRegisteredUninstaller(source))
            throw new IOException("Abre Aplicaciones instaladas y elige Desinstalar en WinPulse Pro.");
        if (MessageBox.Show(
                "Se quitarán WinPulse Pro, su acceso de Inicio y su tarea de inicio con Windows. Se conservarán los perfiles, el historial y los ajustes aplicados.\n\nSi quieres recuperar la configuración anterior de Windows, hazlo primero desde Historial en WinPulse.",
                "Desinstalar WinPulse Pro", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel)
            != MessageBoxResult.OK) return 0;

        if (WindowsElevation.IsAdministrator)
            return await RemoveInstalledAppAsync(source);
        if (await InstanceCoordinator.SendAsync(InstanceRequest.PrepareUpgrade) == InstanceReply.Busy)
            throw new IOException("Espera a que termine la operación actual de WinPulse y vuelve a desinstalar.");
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new IOException("No se pudo identificar la cuenta de Windows.");
        return await Task.Run(async () =>
        {
            using var worker = WindowsElevation.StartElevated(source, $"--uninstall-worker {sid}");
            await worker.WaitForExitAsync();
            return worker.ExitCode;
        });
    }

    private static async Task<int> RemoveInstalledAppAsync(string source)
    {
        bool cleanupScheduled = await Task.Run(() => WinPulseInstallation.Uninstall(source));
        Report(cleanupScheduled
                ? "WinPulse Pro se quitó. Windows eliminará el desinstalador al reiniciar."
                : "WinPulse Pro se quitó. Windows no pudo programar la limpieza del desinstalador.",
            false, cleanupScheduled ? MessageBoxImage.Information : MessageBoxImage.Warning);
        return cleanupScheduled ? 0 : 2;
    }
#endif

    private InstanceReply HandleInstanceRequest(InstanceRequest request)
    {
        if (MainWindow is not MainWindow window) return InstanceReply.Busy;
        if (request == InstanceRequest.PrepareUpgrade)
            return window.PrepareForUpgrade() ? InstanceReply.Accepted : InstanceReply.Busy;

        window.RestoreFromTray();
        return InstanceReply.Accepted;
    }

    private static void Report(string message, bool silent, MessageBoxImage icon)
    {
        if (silent) Console.Error.WriteLine(message);
        else MessageBox.Show(message, "WinPulse Pro", MessageBoxButton.OK, icon);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
