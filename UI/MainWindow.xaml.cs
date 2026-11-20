using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using DrawingIcon = System.Drawing.Icon;
using WinForms = System.Windows.Forms;

namespace WinPulseOptimizer
{
    public partial class MainWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        private readonly uint _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        private record TweakMeta(string Title, string PlainDesc, string SectionName, int TabIndex, string RowName);

        private static readonly Dictionary<string, TweakMeta> TweakInfoMap = new()
        {
            ["SwGameMode"] = new("Modo Juego de Windows", "Hace que Windows mande el juego a los núcleos más rápidos de tu procesador.", "Pestaña 1: FPS, GPU y CPU", 1, "RowSwGameMode"),
            ["SwHags"] = new("Programación de GPU acelerada por hardware (HAGS)", "Delega parte de la programación de la GPU al hardware cuando el controlador lo admite.", "Pestaña 1: FPS, GPU y CPU", 1, "RowSwHags"),
            ["SwWindowedOpt"] = new("Optimización para Juegos en Ventana", "Quita el retardo de imagen cuando juegas en modo ventana sin bordes.", "Pestaña 1: FPS, GPU y CPU", 1, "RowSwWindowedOpt"),
            ["SwHighResTimer"] = new("Temporizador de Alta Precisión", "Hace que Windows responda cada 0,5 ms para que los FPS sean más estables sin tirones.", "Pestaña 1: FPS, GPU y CPU", 1, "RowSwHighResTimer"),
            ["SwCoreParking"] = new("Mantener todos los núcleos despiertos", "Evita que el procesador duerma núcleos mientras estás enchufado a la corriente.", "Pestaña 1: FPS, GPU y CPU", 1, "RowSwCoreParking"),
            ["SwMmcss"] = new("Prioridad Alta para Gráfica y Procesador", "Da preferencia al juego frente al resto de programas abiertos.", "Pestaña 1: FPS, GPU y CPU", 1, "RowSwMmcss"),
            ["SwSuspendServices"] = new("Pausar Servicios Pesados de Windows", "Detiene temporalmente el indexado de archivos y telemetría para liberar procesador.", "Pestaña 2: Procesos y RAM", 2, "RowSwSuspendServices"),
            ["SwForegroundPrio"] = new("Dar más potencia a la ventana activa", "Prioriza la ventana que tienes abierta en pantalla frente a las minimizadas.", "Pestaña 2: Procesos y RAM", 2, "RowSwForegroundPrio"),
            ["SwGameDvr"] = new("Apagar Grabación Oculta de Pantalla", "Evita que Windows esté grabando vídeo en segundo plano gastando memoria gráfica.", "Pestaña 2: Procesos y RAM", 2, "RowSwGameDvr"),
            ["SwBgApps"] = new("Bloquear Apps en Segundo Plano", "Impide que aplicaciones cerradas sigan gastando memoria RAM oculta.", "Pestaña 2: Procesos y RAM", 2, "RowSwBgApps"),
            ["SwFastUi"] = new("Apertura instantánea de ventanas y menús", "Reduce el retardo de menús de 400 ms a 20 ms y desactiva la animación lenta al abrir o minimizar ventanas.", "Pestaña 2: Procesos y RAM", 2, "RowSwFastUi"),
            ["SwTransparency"] = new("Desactivar transparencias de Windows", "Desactiva efectos Acrylic/Mica (EnableTransparency = 0) para aligerar el compositor DWM.", "Pestaña 2: Procesos y RAM", 2, "RowSwTransparency"),
            ["SwNagle"] = new("Ajuste de latencia TCP", "Cambia los parámetros de Nagle y confirmaciones TCP en adaptadores con una IP configurada.", "Pestaña 3: Red y Ratón", 3, "RowSwNagle"),
            ["SwUsbPcie"] = new("Evitar Cortes de Energía en USB y Gráfica", "Impide que Windows ahorre energía en los puertos del ratón o en la gráfica.", "Pestaña 3: Red y Ratón", 3, "RowSwUsbPcie"),
            ["SwMouseAccel"] = new("Desactivar aceleración del ratón", "Desactiva «Mejorar la precisión del puntero» en Windows para obtener una respuesta lineal 1:1.", "Pestaña 3: Red y ratón", 3, "RowSwMouseAccel")
        };

        private bool _loading = true;
        private bool _startingUp = true;
        private bool _systemChangeInProgress;
        private string _activeProfileTag = "";
        private bool _savedProfileNeedsReapply;
        private bool _forceExit = false;
        private bool? _lastAcState;
        private bool? _hasBattery;
        private SystemSnapshot? _beforeSnapshot;
        private bool _manualComparisonReference;
        private HistoryEntry? _latestProfileReport;
        private SettingStatusWindow? _settingStatusWindow;
        private bool _readingLiveMetrics;
        private bool _refreshingTopProcesses;
        private long _nextGpuReadingAt;
        private long _lastLiveSampleOrder;
        private string? _lastNvidiaName;
        private string _detectedGpuSummary = "GPU";
        private readonly DispatcherTimer _liveMetricsTimer = new(DispatcherPriority.Background);
        private readonly DispatcherTimer _automationTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4.5) };
        private WinForms.NotifyIcon? _trayIcon;
        private DrawingIcon? _trayIconImage;

        public MainWindow()
        {
            InitializeComponent();
            var version = typeof(MainWindow).Assembly.GetName().Version;
            Title = $"WinPulse Pro {version?.ToString(3)}";
            InitializeUpdateButton(version);
            SourceInitialized += (_, _) =>
            {
                ApplyDarkTitleBar();
                RegisterTaskbarHook();
            };
            SetupSystemTray();

            CardProfiles.IsEnabled = MainTabs.IsEnabled = false;
            ShowChangeCard("ESTADO AL ABRIR", "Leyendo la configuración de Windows…", "");
            SystemEvents.PowerModeChanged += SystemPowerModeChanged;
            _liveMetricsTimer.Tick += (_, _) => UpdateLiveMetrics();
            _automationTimer.Tick += (_, _) => OnAutomationTick();
            IsVisibleChanged += (_, _) => UpdateLiveMetricsSchedule();
            StateChanged += (_, _) => UpdateLiveMetricsSchedule();
            Activated += (_, _) => UpdateLiveMetricsSchedule();
            Activated += async (_, _) => await ShowForegroundNoticesAsync();
            Deactivated += (_, _) => UpdateLiveMetricsSchedule();
            Deactivated += (_, _) => _usageUpload?.Cancel();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= MainWindow_Loaded;
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
            {
                HideToTray(false);
                _ = EnsureTrayIconAfterExplorerAsync();
            }

            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            if (_forceExit) return;
            InitialState? initialized = null;
            try
            {
                var stateReading = Task.Run(ReadInitialState);
                var autoStartReading = Task.Run(OptimizerEngine.IsAutoStartEnabled);
                await Task.WhenAll(stateReading, autoStartReading);
                if (_forceExit) return;
                var startup = await stateReading;
                RenderSystemInfo(startup.Info);
                RenderPowerDiagnostics(startup.Power, startup.PowerRestricted, startup.PowerPlans);
                SyncSwitchesFromSystem(startup.State);
                LstTopProcesses.ItemsSource = startup.Processes;
                LstStartupApps.ItemsSource = startup.StartupApps;

                var saved = startup.Saved;
                _lastUpdateCheckUtc = saved.LastUpdateCheckUtc;
                SwAutoGame.IsChecked = saved.AutoGameSwitch;
                SwAutoBattery.IsChecked = saved.AutoBatteryChill;
                SwStartWithWindows.IsChecked = await autoStartReading;
                _lastAcState = HardwareMonitor.GetAcState();
                LoadAutomationRules(saved);
                LoadAdaptiveMemorySettings(saved);
                LoadBenchmarks(saved);
                HistoryManager.EnsureInitialBaseline(saved, startup.State, startup.Power, startup.Snapshot);
                if (saved.Baseline is { } baseline) RenderInitialBaseline(baseline);
                ShowHistory(saved.Entries);
                RestoreSavedProfileBanner(saved.ActiveProfileTag ?? "", startup.ProfileVerification);
                CaptureBeforeSnapshot(startup.Snapshot);
                RenderLiveMetrics(startup.Snapshot);
                ShowStartupSnapshot(startup.Snapshot);
                Log($"{Title} listo. Ejecutable: {Environment.ProcessPath ?? AppContext.BaseDirectory}");
                initialized = startup;
            }
            catch (Exception ex)
            {
                if (_forceExit) return;
                ShowChangeCard("ESTADO AL ABRIR", "No se pudo consultar la configuración de Windows", "Consulta el registro.");
                Log($"No se pudo completar la lectura inicial. {ex.Message}");
            }
            finally
            {
                _loading = _startingUp = false;
                if (!_forceExit) SetSystemChangeBusy(false);
            }

            if (_forceExit) return;
            if (initialized != null) await ReapplySavedProfileOnStartupAsync(initialized);
            if (_forceExit) return;
            _automationTimer.Start();
            UpdateLiveMetricsSchedule();
            if (initialized != null) await ReconcileAutomationAsync();
            await ShowForegroundNoticesAsync();
        }

        private sealed record InitialState(AuditStore Saved, SystemInfo Info, PowerPlanDetails Power, bool PowerRestricted,
            WindowsPowerPlanItem[] PowerPlans, TweakState State, SystemSnapshot Snapshot, List<ProcessItem> Processes,
            List<StartupAppItem> StartupApps, ProfileVerificationResult? ProfileVerification);

        private static InitialState ReadInitialState()
        {
            var saved = HistoryManager.Load();
            var info = HardwareMonitor.GetSystemInfo();
            var power = OptimizerEngine.GetPowerPlanDetails();
            var powerPlans = OptimizerEngine.GetPowerSchemes();
            var state = OptimizerEngine.ReadCurrentState();
            var snapshot = HardwareMonitor.CaptureSnapshot(power.EffectiveModeLabel, includeGpu: false);
            var processes = HardwareMonitor.GetTopMemoryProcesses(12);
            var startupApps = OptimizerEngine.GetStartupApps();
            var profileVerification = TryParseUsageProfile(saved.ActiveProfileTag ?? "", out var savedProfile)
                ? OptimizerEngine.VerifyProfileState(savedProfile, DefaultProfileAdjustments(savedProfile))
                : null;
            return new(saved, info, power, OptimizerEngine.ReadEnforcedPowerPlan().HasValue, powerPlans, state, snapshot,
                processes, startupApps, profileVerification);
        }

        private void SetupSystemTray()
        {
            var iconResource = new Uri("/WinPulseOptimizer;component/Assets/WinPulse.ico", UriKind.Relative);
            using var stream = System.Windows.Application.GetResourceStream(iconResource).Stream;
            var frames = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames;
            ImgAppLogo.Source = frames.MinBy(frame => Math.Abs(frame.PixelWidth - 64));
            stream.Position = 0;
            using var icon = new DrawingIcon(stream);
            _trayIconImage = (DrawingIcon)icon.Clone();

            void QueueProfile(Func<Task> apply) => Dispatcher.BeginInvoke(new Action(async () => await apply()));
            const string fromTray = "Activado desde el icono junto al reloj.";
            var menu = new TrayMenu(_trayIconImage, RestoreFromTray,
                () => QueueProfile(() => ApplyGamingModeAsync("Bandeja: Activaste Jugar", fromTray)),
                () => QueueProfile(() => ApplyStudyModeAsync("Bandeja: Activaste Estudiar / Trabajar", fromTray)),
                () => QueueProfile(() => ApplyChillModeAsync("Bandeja: Activaste Estar Chill", fromTray)),
                () => Dispatcher.BeginInvoke(new Action(() => BtnPurgeRam_Click(this, new RoutedEventArgs()))),
                () => Dispatcher.BeginInvoke(new Action(ExitCompletely)));
            menu.Opening += (_, _) =>
            {
                string title = _activeProfileTag switch
                {
                    "JUGAR" => "Jugar (Máximo rendimiento)",
                    "ESTUDIAR" => "Estudiar / Trabajar",
                    "CHILL" => "Estar Chill",
                    "FABRICA" => "De fábrica",
                    "CUSTOM" => "Configuración personalizada",
                    "ORIGINAL" => "Configuración original",
                    "" => "Plan de Windows",
                    _ => TxtActiveModeTitle.Text
                };
                bool busy = _systemChangeInProgress || _benchmarkCancellation != null;
                menu.UpdateState(title, _activeProfileTag, _startingUp || busy, !busy && _loadedPingCancellation == null,
                    _savedProfileNeedsReapply);
            };

            _trayIcon = new WinForms.NotifyIcon
            {
                Icon = _trayIconImage,
                Text = "WinPulse Pro — Optimizador activo",
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLiveMetricsSchedule();

        private static void AnimateSlideFadeIn(FrameworkElement element, double offsetX = 0, double offsetY = 12, int durationMs = 220)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(durationMs);

            element.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
            element.BeginAnimation(OpacityProperty, fade);

            if (offsetX == 0 && offsetY == 0) return;

            if (element.RenderTransform is not TranslateTransform slide)
            {
                slide = new TranslateTransform();
                element.RenderTransform = slide;
            }
            if (offsetX != 0)
            {
                slide.X = offsetX;
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offsetX, 0, duration) { EasingFunction = ease });
            }
            if (offsetY != 0)
            {
                slide.Y = offsetY;
                slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, duration) { EasingFunction = ease });
            }
        }

        private void HideToTray(bool showBalloon = true)
        {
            Hide();
            if (showBalloon)
            {
                _trayIcon?.ShowBalloonTip(1800, "WinPulse Pro sigue activo", "Haz doble clic en el icono junto al reloj para abrir la ventana.", WinForms.ToolTipIcon.Info);
            }
        }

        internal void RestoreFromTray()
        {
            RefreshTrayIcon();
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            bool wasTopmost = Topmost;
            Topmost = true;
            Topmost = wasTopmost;
            Activate();
        }

        private void RefreshTrayIcon()
        {
            if (_trayIcon == null || _forceExit) return;
            _trayIcon.Visible = false;
            _trayIcon.Visible = true;
        }

        private void RegisterTaskbarHook()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WindowHook);
        }

        private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
                RefreshTrayIcon();
            return IntPtr.Zero;
        }

        private async Task EnsureTrayIconAfterExplorerAsync()
        {
            // Al iniciar sesión justo antes que explorer.exe, re-registrar el icono en cuanto exista la barra de tareas.
            for (int i = 0; i < 30 && !_forceExit; i++)
            {
                if (FindWindow("Shell_TrayWnd", null) != IntPtr.Zero)
                {
                    await Task.Delay(1000);
                    if (!_forceExit) RefreshTrayIcon();
                    return;
                }
                await Task.Delay(500);
            }
        }

        internal bool PrepareForUpgrade()
        {
            if (_startingUp || _systemChangeInProgress || _benchmarkCancellation != null || _networkTesting || _checkingAutomation)
                return false;

            IsEnabled = false;
            _processMeasurement?.Cancel();
            _automationTimer.Stop();
            _liveMetricsTimer.Stop();
            if (_trayIcon?.ContextMenuStrip is { } menu) menu.Enabled = false;
            return true;
        }

        internal void ExitCompletely()
        {
            if (_systemChangeInProgress || _benchmarkCancellation != null || _loadedPingCancellation != null)
            {
                Log("Espera a que termine la operación o cancela la medición antes de salir.");
                return;
            }
            _forceExit = true;
            _usageUpload?.Cancel();
            _usageStatistics.Flush();
            _processMeasurement?.Cancel();
            ProcessResourceMonitor.RestoreSmartGameScheduling();
            _automationTimer.Stop();
            _liveMetricsTimer.Stop();
            SystemEvents.PowerModeChanged -= SystemPowerModeChanged;
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                var menu = _trayIcon.ContextMenuStrip;
                _trayIcon.Dispose();
                menu?.Dispose();
            }
            _trayIconImage?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        private void BtnExitApp_Click(object sender, RoutedEventArgs e) => ExitCompletely();

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_forceExit)
            {
                e.Cancel = true;
                HideToTray(true);
                return;
            }
            base.OnClosing(e);
        }

        private async Task ReapplySavedProfileOnStartupAsync(InitialState startup)
        {
            try
            {
                string selectedProfile = _activeProfileTag;
                if (SwPauseAutomationForTests.IsChecked == true)
                {
                    ShowStartupSnapshot(startup.Snapshot, "Automatización pausada durante las Pruebas A/B.");
                    return;
                }
                var journal = SystemChangeJournal.Load();
                if (journal.Pending != null || journal.RestoringOriginal.HasValue)
                {
                    ShowStartupSnapshot(startup.Snapshot, "Recuperación pendiente. Complétala desde Historial.");
                    return;
                }
                if (journal.AutomationOperationId != null)
                {
                    ShowStartupSnapshot(startup.Snapshot, "WinPulse comprobará si el perfil automático debe volver al perfil previo.");
                    return;
                }
                if (selectedProfile.StartsWith("GAME:", StringComparison.Ordinal))
                {
                    RestoreSavedProfileBanner("CUSTOM");
                    selectedProfile = "CUSTOM";
                }

                if (selectedProfile == "") return;
                if (selectedProfile is "CUSTOM" or "ORIGINAL")
                {
                    ShowStartupSnapshot(startup.Snapshot, "WinPulse usa la configuración actual de Windows.");
                    return;
                }
                if (!TryParseUsageProfile(selectedProfile, out var profile)) return;

                var verification = startup.ProfileVerification ??
                    await Task.Run(() => OptimizerEngine.VerifyProfileState(profile, DefaultProfileAdjustments(profile)));
                string displayName = GetProfileDisplayName(selectedProfile);

                if (verification.IsFullyActive)
                {
                    RestoreSavedProfileBanner(selectedProfile, verification);
                    string status = $"Confirmado: todos los ajustes de «{displayName}» siguen activos en Windows.";
                    if (verification.PendingRestart)
                        status += " Algunos cambios previos surtirán efecto al reiniciar Windows.";
                    ShowStartupSnapshot(startup.Snapshot, status, "PLAN ACTIVO AL ABRIR", "SuccessBrush");
                    Log($"Al abrir WinPulse: «{displayName}» ya está activo y verificado en Windows.");
                    return;
                }

                if (!WindowsElevation.IsAdministrator)
                {
                    RestoreSavedProfileBanner(selectedProfile, verification);
                    string status = $"Faltan ajustes de «{displayName}» ({string.Join(", ", verification.Differences)}). " +
                        $"Pulsa «{GetProfileButtonName(selectedProfile)}» para aplicarlo con permiso de administrador.";
                    ShowStartupSnapshot(startup.Snapshot, status, "ESTADO AL ABRIR · REQUIERE REAPLICAR", "WarningBrush");
                    Log($"Al abrir WinPulse: «{displayName}» requiere reaplicarse ({string.Join(", ", verification.Differences)}).");
                    return;
                }

                const string explanation = "Volvió a aplicar el perfil seleccionado al abrir WinPulse.";
                switch (selectedProfile)
                {
                    case "JUGAR":
                        await ApplyGamingModeAsync("Al abrir WinPulse: Jugar", explanation, skipVerification: true);
                        break;
                    case "CHILL":
                        await ApplyChillModeAsync("Al abrir WinPulse: Estar Chill", explanation, skipVerification: true);
                        break;
                    case "ESTUDIAR":
                        await ApplyStudyModeAsync("Al abrir WinPulse: Estudiar / Trabajar", explanation, skipVerification: true);
                        break;
                    case "FABRICA":
                        await ApplyFactoryProfileAsync("Al abrir WinPulse: De fábrica", explanation, skipVerification: true);
                        break;
                }
            }
            catch (Exception ex)
            {
                ShowStartupSnapshot(startup.Snapshot, "No se pudo comprobar el perfil guardado. Consulta el registro.");
                Log($"No se pudo recuperar el perfil al abrir WinPulse. {ex.Message}");
            }
            finally { ResetAutomationOverride(); }
        }

        private async void StartWithWindows_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool enable = SwStartWithWindows.IsChecked == true;
            bool? current = await Task.Run(OptimizerEngine.IsAutoStartEnabled);
            if (current == enable)
            {
                ShowChangeCard("AJUSTE YA APLICADO",
                    enable ? "El inicio con Windows ya estaba activado" : "El inicio con Windows ya estaba desactivado",
                    "No hizo falta realizar cambios.", "SuccessBrush");
                Log($"El inicio con Windows ya estaba {(enable ? "activado" : "desactivado")}; no se realizaron cambios.");
                return;
            }
            await ApplySystemChangeAsync(
                enable ? "Activaste el inicio silencioso con Windows" : "Desactivaste el inicio silencioso con Windows",
                "Guardar este cambio en la tarea de inicio de Windows requiere permiso de administrador.",
                "Pestaña 0: Automatización inteligente", 0, "", new(SystemAction.AutoStart, Enabled: enable));
        }

        private void ApplyDarkTitleBar()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int useDarkMode = 1;
                DwmSetWindowAttribute(hwnd, 20, ref useDarkMode, sizeof(int));
            }
            catch { }
        }

        private async void BtnGoToSection_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is HistoryEntry entry)
            {
                MainTabs.SelectedIndex = entry.TabIndex;
                if (!string.IsNullOrEmpty(entry.ControlName) && FindName(entry.ControlName) is Border targetBorder)
                {
                    targetBorder.BringIntoView();
                    var origBg = targetBorder.Background;
                    var origBrush = targetBorder.BorderBrush;
                    var origThick = targetBorder.BorderThickness;

                    targetBorder.Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush");
                    targetBorder.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
                    targetBorder.BorderThickness = new Thickness(1.5);

                    await Task.Delay(1600);
                    targetBorder.Background = origBg;
                    targetBorder.BorderBrush = origBrush;
                    targetBorder.BorderThickness = origThick;
                }
            }
        }

        private void RenderInitialBaseline(InitialBaseline b)
        {
            string cleanPlan = CleanPlanName(b.PowerPlan);
            TxtBaselineDate.Text = $"Guardado el {b.CapturedAt:dd/MM/yyyy} a las {b.CapturedAt:HH:mm}";

            string deviceLabel = _hasBattery == false ? "Tu PC de sobremesa" : "Tu portátil";
            TxtOrigLine1.Text = $"1. Energía y Memoria: {deviceLabel} venía con el plan '{cleanPlan}' y tenía {b.UsedRamGb:0.00} GB de RAM ocupados (de {b.TotalRamGb:0.0} GB totales).";
            TxtOrigLine2.Text = $"2. Gráfica y Juegos: La aceleración gráfica (HAGS) venía {(b.Hags ? "activada" : "desactivada")}, el Modo Juego {(b.GameMode ? "activado" : "desactivado")} y la mejora para juegos en ventana {(b.WindowedOpt ? "activada" : "desactivada")}.";
            TxtOrigLine3.Text = $"3. Potencia de Procesador: Los núcleos estaban {(b.DisableCoreParking ? "siempre despiertos" : "en modo ahorro (dormidos en reposo)")}, el temporizador rápido de FPS estaba {(b.HighResTimer ? "activado" : "desactivado")} y la prioridad alta para juegos {(b.MmcssGaming ? "activada" : "desactivada")}.";
            TxtOrigLine4.Text = $"4. Red y Puertos USB: La respuesta rápida de red (menor ping) estaba {(b.DisableNagle ? "activada" : "desactivada")} y el ahorro de energía en USB/gráfica estaba {(b.DisableUsbPciePowerSave ? "desactivado" : "activado")}.";
            TxtOrigLine5.Text = $"5. Segundo Plano: La grabación oculta de pantalla estaba {(b.DisableGameDvr ? "apagada" : "encendida")}, las apps en segundo plano {(b.LimitBackgroundApps ? "bloqueadas" : "permitidas")} y los servicios pesados {(b.SuspendHeavyServices ? "pausados" : "funcionando")}.";
        }

        private static string CleanPlanName(string raw)
        {
            if (raw.Contains("ximo", StringComparison.OrdinalIgnoreCase) || raw.Contains("Ultimate", StringComparison.OrdinalIgnoreCase)) return "Máximo rendimiento";
            if (raw.Contains("Alto", StringComparison.OrdinalIgnoreCase) || raw.Contains("High", StringComparison.OrdinalIgnoreCase)) return "Alto rendimiento";
            if (raw.Contains("Equilibrado", StringComparison.OrdinalIgnoreCase) || raw.Contains("Balanced", StringComparison.OrdinalIgnoreCase)) return "Equilibrado";
            return raw;
        }

        private void RecordAction(string action, string explanation, string sectionName, int tabIndex, string controlName,
            string resultDelta, ProfileSettingResult[]? profileSettings = null)
        {
            var updated = HistoryManager.AddEntry(action, explanation, sectionName, tabIndex, controlName, resultDelta,
                out bool saved, profileSettings);
            ShowHistory(updated);
            if (!saved) Log("No se pudo guardar esta entrada en el historial.");
        }

        private void ShowHistory(List<HistoryEntry> entries)
        {
            LstHistory.ItemsSource = entries;
            _latestProfileReport = entries.FirstOrDefault(entry => entry.HasProfileSettings);
            BtnLatestProfileReport.IsEnabled = _latestProfileReport != null;
        }

        private void BtnShowProfileReport_Click(object sender, RoutedEventArgs e)
        {
            var entry = (sender as System.Windows.Controls.Button)?.Tag as HistoryEntry ?? _latestProfileReport;
            if (entry is not { HasProfileSettings: true }) return;
            new ProfileReportWindow(entry) { Owner = this }.ShowDialog();
        }

        private async void BtnShowSettingStatus_Click(object sender, RoutedEventArgs e)
        {
            if (_settingStatusWindow == null)
            {
                var window = new SettingStatusWindow { Owner = this };
                _settingStatusWindow = window;
                window.Closed += (_, _) => _settingStatusWindow = null;
                window.Show();
            }
            _settingStatusWindow.Activate();
            await _settingStatusWindow.UpdateAsync(TxtActiveModeTitle.Text, _startingUp || _systemChangeInProgress);
        }

        private static bool TryParseUsageProfile(string tag, out UsageProfile profile)
        {
            switch (tag)
            {
                case "JUGAR": profile = UsageProfile.GamingMax; return true;
                case "ESTUDIAR": profile = UsageProfile.StudyWork; return true;
                case "CHILL": profile = UsageProfile.ChillBattery; return true;
                case "FABRICA": profile = UsageProfile.FactoryDefault; return true;
                default: profile = default; return false;
            }
        }

        private static string GetProfileDisplayName(string tag) => tag switch
        {
            "JUGAR" => "Jugar (Máximo rendimiento)",
            "ESTUDIAR" => "Estudiar / Trabajar",
            "CHILL" => "Estar Chill",
            "FABRICA" => "De fábrica",
            _ => tag
        };

        private static string GetProfileButtonName(string tag) => tag switch
        {
            "JUGAR" => "Jugar (Máximo)",
            "ESTUDIAR" => "Estudiar / Trabajar",
            "CHILL" => "Estar Chill",
            "FABRICA" => "De fábrica",
            _ => tag
        };

        private static string FormatProfileBannerDescription(UsageProfile profile, string tag, ProfileVerificationResult? verification)
        {
            if (verification is { IsFullyActive: false })
                return $"No coincide con Windows ahora ({string.Join(", ", verification.Differences.Take(3))}). Pulsa «{GetProfileButtonName(tag)}» para aplicarlo.";
            string summary = OptimizerEngine.GetProfilePowerSummary(profile);
            if (verification is { IsFullyActive: true })
                return $"Confirmado en Windows{(verification.PendingRestart ? " (reinicio pendiente)" : "")} · {summary}";
            return summary;
        }

        private void HighlightActiveProfileButton(System.Windows.Controls.Button? activeBtn, string title, string desc,
            string profileTag, ProfileVerificationResult? verification = null)
        {
            bool needsReapply = verification is { IsFullyActive: false };
            _savedProfileNeedsReapply = needsReapply;

            System.Windows.Controls.Button[] all = { BtnProfileJugar, BtnProfileEstudiar, BtnProfileChill, BtnProfileFabrica };
            foreach (var b in all)
                b.Tag = b == activeBtn ? (needsReapply ? "PendingProfile" : "SelectedProfile") : null;

            DotActiveProfile.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
                needsReapply ? "WarningBrush" : "SuccessBrush");
            TxtActiveModeLabel.Text = needsReapply ? "PLAN GUARDADO (SIN APLICAR):" : "PLAN ACTIVO:";
            TxtActiveModeLabel.SetResourceReference(TextBlock.ForegroundProperty,
                needsReapply ? "WarningBrush" : (activeBtn != null ? "SuccessBrush" : "QuietBrush"));

            TxtActiveModeTitle.Text = title;
            TxtActiveModeDesc.Text = desc;
            if (_trayIcon != null)
            {
                string trayText = needsReapply ? $"WinPulse Pro — {title} (sin aplicar)" : $"WinPulse Pro — {title}";
                _trayIcon.Text = trayText.Length <= 63 ? trayText : trayText[..63];
            }
            bool tagChanged = _activeProfileTag != profileTag;
            _activeProfileTag = profileTag;
            if (!_startingUp && tagChanged && !HistoryManager.SaveActiveProfileTag(profileTag))
                Log("No se pudo guardar el perfil seleccionado para el próximo inicio.");
            UpdatePlanRecommendationCard();
        }

        private void UpdatePlanRecommendationCard()
        {
            if (CardPlanRecommendation == null) return;

            bool fastUi = SwFastUi?.IsChecked == true;
            bool mouse = SwMouseAccel?.IsChecked == true;
            bool transp = SwTransparency?.IsChecked == true;
            bool adaptive = SwAdaptiveMemory?.IsChecked == true;

            if (_activeProfileTag == "JUGAR" || _activeProfileTag.StartsWith("GAME:", StringComparison.Ordinal))
            {
                TxtPlanRecommendationKicker.Text = "RECOMENDADO PARA TU PLAN ELEGIDO · JUGAR";
                var missing = new List<string>();
                if (!mouse) missing.Add("desactivar aceleración del ratón (respuesta 1:1)");
                if (!transp) missing.Add("desactivar transparencias (menos carga DWM)");

                if (missing.Count == 0)
                {
                    TxtPlanRecommendationBody.Text = "Ya tienes activos el reparto inteligente de CPU, dGPU automática, ratón 1:1 y sin transparencias.";
                    BtnApplyPlanRecommendation.Visibility = Visibility.Collapsed;
                }
                else
                {
                    TxtPlanRecommendationBody.Text = $"Con Jugar ya tienes reparto inteligente de CPU y dGPU al detectar juegos. Opcional sin UAC: {string.Join(" y ", missing)}.";
                    BtnApplyPlanRecommendation.Content = missing.Count == 2 ? "Aplicar extras gamer" : "Aplicar extra pendiente";
                    BtnApplyPlanRecommendation.Visibility = Visibility.Visible;
                }

                BtnRevertPlanRecommendation.Content = "Revertir extras";
                BtnRevertPlanRecommendation.Visibility = (mouse || transp) ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            if (_activeProfileTag == "ESTUDIAR")
            {
                TxtPlanRecommendationKicker.Text = "RECOMENDADO PARA TU PLAN ELEGIDO · ESTUDIAR / TRABAJAR";
                var missing = new List<string>();
                if (!fastUi) missing.Add("apertura instantánea de ventanas y menús");
                if (!adaptive) missing.Add("optimización adaptativa de RAM");

                if (missing.Count == 0)
                {
                    TxtPlanRecommendationBody.Text = transp
                        ? "Ya tienes apertura instantánea y RAM adaptativa activas. Las transparencias de Windows están desactivadas; puedes reactivarlas con un clic."
                        : "Ya tienes activos la apertura instantánea y el ajuste adaptativo de RAM para multitarea fluida.";
                    BtnApplyPlanRecommendation.Visibility = Visibility.Collapsed;
                }
                else
                {
                    string extraNote = transp ? " Además puedes reactivar las transparencias de Windows." : "";
                    TxtPlanRecommendationBody.Text = $"Para moverte más rápido entre apps sin cerrar programas: activar {string.Join(" y ", missing)} (sin UAC).{extraNote}";
                    BtnApplyPlanRecommendation.Content = "Aplicar para Estudiar";
                    BtnApplyPlanRecommendation.Visibility = Visibility.Visible;
                }

                if (fastUi || adaptive || transp)
                {
                    BtnRevertPlanRecommendation.Content = !fastUi && !adaptive && transp
                        ? "Activar transparencias"
                        : transp ? "Revertir extras y transparencias" : "Revertir extras";
                    BtnRevertPlanRecommendation.Visibility = Visibility.Visible;
                }
                else
                    BtnRevertPlanRecommendation.Visibility = Visibility.Collapsed;
                return;
            }

            if (_activeProfileTag == "CHILL")
            {
                TxtPlanRecommendationKicker.Text = "RECOMENDADO PARA TU PLAN ELEGIDO · ESTAR CHILL";
                var missing = new List<string>();
                if (!transp) missing.Add("desactivar transparencias de Windows");
                if (!adaptive) missing.Add("optimización adaptativa de RAM");

                if (missing.Count == 0)
                {
                    TxtPlanRecommendationBody.Text = "Ya tienes activos el ahorro visual sin transparencias y el ajuste adaptativo de RAM.";
                    BtnApplyPlanRecommendation.Visibility = Visibility.Collapsed;
                }
                else
                {
                    TxtPlanRecommendationBody.Text = _hasBattery == false
                        ? $"Para mantener el PC más fresco, silencioso y ligero: {string.Join(" y ", missing)} (sin UAC)."
                        : $"Para ahorrar más batería y carga de GPU integrada: {string.Join(" y ", missing)} (sin UAC).";
                    BtnApplyPlanRecommendation.Content = _hasBattery == false ? "Aplicar modo Chill" : "Aplicar ahorro Chill";
                    BtnApplyPlanRecommendation.Visibility = Visibility.Visible;
                }

                BtnRevertPlanRecommendation.Content = "Revertir extras";
                BtnRevertPlanRecommendation.Visibility = (transp || adaptive) ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            bool isDesktop = _hasBattery == false;
            TxtPlanRecommendationKicker.Text = isDesktop
                ? "EQUIPO DETECTADO · PC DE SOBREMESA"
                : "EQUIPO DETECTADO · PORTÁTIL";
            TxtPlanRecommendationBody.Text = isDesktop
                ? "Detectamos un PC de sobremesa: te recomendamos empezar con «Estudiar / Trabajar» para uso diario y dejar activa la detección automática para pasar a «Jugar» al abrir un juego."
                : "Detectamos un portátil: te recomendamos empezar con «Estudiar / Trabajar» con gestión enchufado/batería y detección automática al abrir juegos.";
            BtnApplyPlanRecommendation.Content = isDesktop ? "Empezar optimizado (Sobremesa)" : "Empezar optimizado (Portátil)";
            BtnApplyPlanRecommendation.Visibility = Visibility.Visible;
            BtnRevertPlanRecommendation.Content = "Revertir extras";
            BtnRevertPlanRecommendation.Visibility = (fastUi || mouse || transp || adaptive) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BtnApplyPlanRecommendation_Click(object sender, RoutedEventArgs e)
        {
            if (_startingUp || _systemChangeInProgress || _benchmarkCancellation != null) return;

            if (_activeProfileTag == "JUGAR" || _activeProfileTag.StartsWith("GAME:", StringComparison.Ordinal))
            {
                if (SwMouseAccel.IsChecked != true) await ApplyRecommendedTweakAsync("SwMouseAccel", true);
                if (SwTransparency.IsChecked != true) await ApplyRecommendedTweakAsync("SwTransparency", true);
            }
            else if (_activeProfileTag == "ESTUDIAR")
            {
                if (SwFastUi.IsChecked != true) await ApplyRecommendedTweakAsync("SwFastUi", true);
                if (SwAdaptiveMemory.IsChecked != true) SetRecommendedAdaptiveMemory(true);
            }
            else if (_activeProfileTag == "CHILL")
            {
                if (SwTransparency.IsChecked != true) await ApplyRecommendedTweakAsync("SwTransparency", true);
                if (SwAdaptiveMemory.IsChecked != true) SetRecommendedAdaptiveMemory(true);
            }
            else
            {
                if (await ApplyStudyModeAsync("Inicio optimizado: Estudiar / Trabajar",
                    "Detectó el tipo de equipo y aplicó el perfil recomendado de inicio."))
                {
                    EnableRecommendedAutomation();
                }
            }

            UpdatePlanRecommendationCard();
        }

        private async void BtnRevertPlanRecommendation_Click(object sender, RoutedEventArgs e)
        {
            if (_startingUp || _systemChangeInProgress || _benchmarkCancellation != null) return;

            if (_activeProfileTag == "JUGAR" || _activeProfileTag.StartsWith("GAME:", StringComparison.Ordinal))
            {
                if (SwMouseAccel.IsChecked == true) await ApplyRecommendedTweakAsync("SwMouseAccel", false);
                if (SwTransparency.IsChecked == true) await ApplyRecommendedTweakAsync("SwTransparency", false);
            }
            else if (_activeProfileTag == "ESTUDIAR")
            {
                if (SwFastUi.IsChecked == true) await ApplyRecommendedTweakAsync("SwFastUi", false);
                if (SwAdaptiveMemory.IsChecked == true) SetRecommendedAdaptiveMemory(false);
                if (SwTransparency.IsChecked == true) await ApplyRecommendedTweakAsync("SwTransparency", false);
            }
            else if (_activeProfileTag == "CHILL")
            {
                if (SwTransparency.IsChecked == true) await ApplyRecommendedTweakAsync("SwTransparency", false);
                if (SwAdaptiveMemory.IsChecked == true) SetRecommendedAdaptiveMemory(false);
            }
            else
            {
                if (SwFastUi.IsChecked == true) await ApplyRecommendedTweakAsync("SwFastUi", false);
                if (SwMouseAccel.IsChecked == true) await ApplyRecommendedTweakAsync("SwMouseAccel", false);
                if (SwTransparency.IsChecked == true) await ApplyRecommendedTweakAsync("SwTransparency", false);
                if (SwAdaptiveMemory.IsChecked == true) SetRecommendedAdaptiveMemory(false);
            }

            UpdatePlanRecommendationCard();
        }

        private async Task ApplyRecommendedTweakAsync(string tweakName, bool enable)
        {
            var m = TweakInfoMap[tweakName];
            string verb = enable ? "Activaste" : "Desactivaste";
            await ApplySystemChangeAsync($"{verb}: {m.Title}", m.PlainDesc, m.SectionName, m.TabIndex, m.RowName,
                new(SystemAction.Tweak, tweakName, enable), null);
        }

        private void SetRecommendedAdaptiveMemory(bool enable)
        {
            var store = HistoryManager.Load();
            store.AdaptiveMemoryAutomatic = enable;
            if (HistoryManager.Save(store))
            {
                LoadAdaptiveMemorySettings(store);
                Log(enable
                    ? "Optimización adaptativa de memoria activada como ajuste recomendado del plan."
                    : "Optimización adaptativa de memoria desactivada al revertir recomendaciones.");
            }
        }

        private void EnableRecommendedAutomation()
        {
            var store = HistoryManager.Load();
            store.AutoGameSwitch = true;
            if (_hasBattery != false) store.AutoBatteryChill = true;
            if (!HistoryManager.Save(store)) return;

            _loading = true;
            SwAutoGame.IsChecked = store.AutoGameSwitch;
            SwAutoBattery.IsChecked = store.AutoBatteryChill;
            _loading = false;
            ResetAutomationOverride();
            bool batteryAutomation = _hasBattery != false && store.AutoBatteryChill;
            TxtAutoStatus.Text = (store.AutoGameSwitch || batteryAutomation) ? "Vigilante activo" : "Vigilante pausado";
            Log("Detección automática activada junto con el inicio optimizado.");
        }

        private void BtnToggleProfileGuide_Click(object sender, RoutedEventArgs e)
        {
            bool open = ProfileGuidePanel.Visibility != Visibility.Visible;
            ProfileGuidePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            BtnToggleProfileGuide.Content = open ? "Ocultar detalle de perfiles" : "¿Qué cambia cada perfil?";
            if (open) AnimateSlideFadeIn(ProfileGuidePanel, 0, 8, 190);
        }

        private void RenderPowerDiagnostics(PowerPlanDetails pd, bool powerRestricted, WindowsPowerPlanItem[]? plans = null)
        {
            string baseClean = CleanPlanName(pd.ActivePlanName);

            string restriction = powerRestricted ? " · Fijado por una directiva de Windows" : "";
            TxtDetailPlanHeader.Text = $"Plan activo: {baseClean}{restriction}";
            TxtPwrDisk.Text = pd.DiskTimeout;
            TxtPwrBg.Text = pd.DesktopSlideShow;
            TxtPwrSleep.Text = pd.SleepTimeout;
            TxtPwrPcie.Text = pd.PcieAspm;
            TxtPwrProc.Text = pd.ProcessorPower;
            TxtPwrBoost.Text = pd.ProcessorBoost;
            TxtPwrDisplay.Text = pd.DisplayTimeout;
            TxtPwrBattery.Text = pd.BatteryAction;
            TxtPwrOverlay.Text = pd.WindowsPowerOverlay;

            if (plans != null)
            {
                CmbPowerPlans.ItemsSource = plans;
                CmbPowerPlans.SelectedItem = plans.FirstOrDefault(p => p.IsActive) ?? plans.FirstOrDefault();
                CmbPowerPlans.IsEnabled = !powerRestricted && plans.Length > 0;
                BtnApplyPowerPlan.IsEnabled = !powerRestricted && plans.Length > 1;
                TxtPowerPlanRestriction.Visibility = powerRestricted ? Visibility.Visible : Visibility.Collapsed;
                if (powerRestricted)
                {
                    TxtPowerPlanRestriction.Text =
                        $"Windows fija el plan «{baseClean}» mediante una directiva del sistema (ActivePowerScheme). Mientras exista esa directiva, Windows bloquea el cambio de plan.";
                }
            }
        }

        private void RestoreSavedProfileBanner(string? tag = null, ProfileVerificationResult? verification = null)
        {
            string savedTag = tag ?? HistoryManager.Load().ActiveProfileTag ?? "";
            switch (savedTag)
            {
                case "CUSTOM":
                    HighlightActiveProfileButton(null, "CONFIGURACIÓN PERSONALIZADA", "Ajustes personalizados del sistema.", "CUSTOM");
                    return;
                case "ORIGINAL":
                    HighlightActiveProfileButton(null, "CONFIGURACIÓN ORIGINAL", "Se recuperaron los valores previos a los cambios registrados.", "ORIGINAL");
                    return;
                case "JUGAR":
                    HighlightActiveProfileButton(BtnProfileJugar, "JUGAR (MÁXIMO RENDIMIENTO)",
                        FormatProfileBannerDescription(UsageProfile.GamingMax, "JUGAR", verification), "JUGAR", verification);
                    return;
                case "ESTUDIAR":
                    HighlightActiveProfileButton(BtnProfileEstudiar, "ESTUDIAR / TRABAJAR",
                        FormatProfileBannerDescription(UsageProfile.StudyWork, "ESTUDIAR", verification), "ESTUDIAR", verification);
                    return;
                case "CHILL":
                    HighlightActiveProfileButton(BtnProfileChill, "ESTAR CHILL",
                        FormatProfileBannerDescription(UsageProfile.ChillBattery, "CHILL", verification), "CHILL", verification);
                    return;
                case "FABRICA":
                    HighlightActiveProfileButton(BtnProfileFabrica, "DE FÁBRICA",
                        FormatProfileBannerDescription(UsageProfile.FactoryDefault, "FABRICA", verification), "FABRICA", verification);
                    return;
            }

            if (savedTag.StartsWith("GAME:", StringComparison.Ordinal))
            {
                var gameProfile = _gameProfiles.FirstOrDefault(profile => profile.OwnsTag(savedTag));
                string description = gameProfile?.Tag == savedTag
                    ? $"Perfil por juego · {OptimizerEngine.GetProfilePowerSummary(gameProfile.PowerBase)}"
                    : "Configuración de la sesión de juego.";
                HighlightActiveProfileButton(null, gameProfile?.Name ?? "PERFIL DE JUEGO", description, savedTag);
                return;
            }
            HighlightActiveProfileButton(null, "PLAN DE WINDOWS", OptimizerEngine.GetActivePowerSchemeName(), "");
        }

        private void RenderSystemInfo(SystemInfo info)
        {
            TxtOsBadge.Text = info.OsBuild;
            TxtHardware.Text = $"{info.Cpu}  •  {info.GpuSummary}  •  {info.Ram}";
            TxtDetailGpu.Text = $"Gráficas detectadas: {info.GpuSummary}";
            _detectedGpuSummary = info.GpuSummary;
            TxtLiveGpu.Text = _detectedGpuSummary;
            _hasBattery = HardwareMonitor.HasSystemBattery();
            var batteryVisibility = _hasBattery == false ? Visibility.Collapsed : Visibility.Visible;
            BatteryAutomationOptions.Visibility = batteryVisibility;
            BatteryGameOptions.Visibility = batteryVisibility;
            BatteryPowerDetails.Visibility = batteryVisibility;
            BatteryBenchmarkCard.Visibility = batteryVisibility;
            if (_hasBattery == false)
            {
                BtnProfileJugar.ToolTip = "Máximo rendimiento (sin suspensión), CPU 5–100 % con turbo agresivo, núcleos al 100 %, Modo Juego, HAGS, pausa servicios pesados y USB/PCIe sin ahorro.";
                BtnProfileEstudiar.ToolTip = "Equilibrado (suspensión 60 min), CPU 5–100 % y 50 % núcleos activos, reanuda servicios pausados, mantiene Modo Juego, HAGS, prioridad activa y ajuste TCP.";
                BtnProfileChill.ToolTip = "Modo fresco y silencioso: plan Equilibrado, CPU máx. 85 %, 10 % núcleos activos, ahorro PCIe moderado, bloquea apps en segundo plano y reanuda servicios.";
                TxtGuideJugar.Text = "Sobremesa: plan Máximo rendimiento, CPU 5–100 %, turbo agresivo, núcleos al 100 %, Modo Juego, HAGS, prioridad MMCSS/red/USB y pausa servicios pesados.";
                TxtGuideEstudiar.Text = "Sobremesa: plan Equilibrado (suspensión 60 min), CPU 5–100 % con 50 % núcleos activos, reanuda servicios y mantiene Modo Juego, HAGS, prioridad activa y TCP.";
                TxtGuideChill.Text = "Sobremesa silencioso: plan Equilibrado, CPU máx. 85 %, ahorro PCIe moderado, bloquea apps en segundo plano y reanuda servicios.";
            }
            UpdatePowerBadge();
        }

        private void AutoSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var store = HistoryManager.Load();
            store.AutoGameSwitch = SwAutoGame.IsChecked == true;
            store.AutoBatteryChill = SwAutoBattery.IsChecked == true;
            store.AutoGameOnBattery = SwGameOnBattery.IsChecked == true;
            if (!HistoryManager.Save(store))
            {
                var previous = HistoryManager.Load();
                _loading = true;
                SwAutoGame.IsChecked = previous.AutoGameSwitch;
                SwAutoBattery.IsChecked = previous.AutoBatteryChill;
                SwGameOnBattery.IsChecked = previous.AutoGameOnBattery;
                _loading = false;
                Log("No se pudo guardar la configuración de la automatización.");
                return;
            }

            ResetAutomationOverride();
            bool batteryAutomation = _hasBattery != false && store.AutoBatteryChill;
            TxtAutoStatus.Text = (store.AutoGameSwitch || batteryAutomation) ? "Vigilante activo" : "Vigilante pausado";
            string batteryStatus = _hasBattery == false ? "" : $", Batería={(batteryAutomation ? "ON" : "OFF")}";
            Log($"Automatización actualizada: Juegos={(store.AutoGameSwitch ? "ON" : "OFF")}{batteryStatus}.");
        }

        private void SystemPowerModeChanged(object sender, PowerModeChangedEventArgs e) =>
            Dispatcher.BeginInvoke(new Action(OnPowerLineChanged));

        private async void OnPowerLineChanged()
        {
            if (_forceExit) return;
            CheckBenchmarkPower();
            if (UpdatePowerLineState() && _automationTimer.IsEnabled)
                await ReconcileAutomationAsync();
        }

        private async void OnAutomationTick()
        {
            if (_benchmarkCancellation != null)
            {
                CheckBenchmarkPower();
                return;
            }
            if (_systemChangeInProgress) return;
            UpdatePowerLineState();
            _ = CheckAdaptiveMemoryAsync();
            if (IsVisible && WindowState != WindowState.Minimized && ReferenceEquals(MainTabs.SelectedItem, TabProcesses))
                _ = RefreshTopProcessesAsync();
            await ReconcileAutomationAsync();
        }

        private bool UpdatePowerLineState()
        {
            bool? onAc = HardwareMonitor.GetAcState();
            if (onAc == _lastAcState) return false;
            _lastAcState = onAc;
            UpdatePowerBadge();
            return true;
        }

        private bool CanReadLiveMetrics => _automationTimer.IsEnabled && _benchmarkCancellation == null &&
            IsVisible && DashboardMetrics.IsVisible && WindowState != WindowState.Minimized && (IsActive || _detectedGame == null);

        private void UpdateLiveMetricsSchedule()
        {
            if (!CanReadLiveMetrics)
            {
                _liveMetricsTimer.Stop();
                return;
            }

            var interval = IsActive ? TimeSpan.FromMilliseconds(1500) : TimeSpan.FromSeconds(6);
            if (_liveMetricsTimer.Interval != interval) _liveMetricsTimer.Interval = interval;
            if (_liveMetricsTimer.IsEnabled) return;
            _nextGpuReadingAt = 0;
            _liveMetricsTimer.Start();
            UpdateLiveMetrics();
        }

        private async void UpdateLiveMetrics()
        {
            if (!CanReadLiveMetrics || _readingLiveMetrics) return;
            _readingLiveMetrics = true;
            try
            {
                long now = Environment.TickCount64;
                bool includeGpu = now >= _nextGpuReadingAt;
                if (includeGpu) _nextGpuReadingAt = now + 6_000;
                var snap = await Task.Run(() => HardwareMonitor.CaptureSnapshot(OptimizerEngine.GetActivePowerSchemeName(), includeGpu));
                if (CanReadLiveMetrics) RenderLiveMetrics(snap, includesGpu: includeGpu);
            }
            catch (Exception)
            {
                if (CanReadLiveMetrics) TxtLiveProcs.Text = "Lectura en vivo no disponible.";
            }
            finally { _readingLiveMetrics = false; }
        }

        private void RenderLiveMetrics(SystemSnapshot snap, bool includesGpu = false)
        {
            if (includesGpu)
            {
                _lastNvidiaName = snap.Gpu.Name ?? _lastNvidiaName;
                TxtLiveGpu.Text = _lastNvidiaName ?? _detectedGpuSummary;
                TxtLiveGpuStats.Text = FormatNvidiaMetrics(snap.Gpu);
            }

            if (snap.SampleOrder < _lastLiveSampleOrder) return;
            _lastLiveSampleOrder = snap.SampleOrder;
            TxtLiveCpu.Text = $"CPU: {snap.CpuPercent:0.0}% ({snap.CpuGhz:0.00} GHz)";
            TxtLiveRam.Text = $"RAM: {snap.UsedRamGb:0.00}/{snap.TotalRamGb:0.0} GB";
            TxtLiveProcs.Text = $"Procesos en vivo: {snap.ProcessCount} · {snap.Timestamp:HH:mm:ss} · {snap.PowerScheme}";
        }

        internal static string FormatNvidiaMetrics(GpuTelemetry gpu)
        {
            var metrics = new List<string>();
            if (gpu.TempC is double temperature) metrics.Add($"{temperature:0.#} °C");
            if (gpu.LoadPercent is double load) metrics.Add($"Carga {load:0.#} %");
            if (gpu.ClockMhz is double clock) metrics.Add($"{clock:0} MHz");
            if (gpu.UsedVramGb.HasValue || gpu.TotalVramGb.HasValue)
            {
                string used = gpu.UsedVramGb?.ToString("0.0") ?? "—";
                string total = gpu.TotalVramGb?.ToString("0.0") ?? "—";
                metrics.Add($"VRAM {used}/{total} GB");
            }
            return metrics.Count > 0 ? string.Join(" · ", metrics) : "Lectura no disponible";
        }

        private void RefreshTopProcesses() => LstTopProcesses.ItemsSource = HardwareMonitor.GetTopMemoryProcesses(12);

        private async Task RefreshTopProcessesAsync()
        {
            if (_refreshingTopProcesses || _forceExit) return;
            _refreshingTopProcesses = true;
            try
            {
                var processes = await Task.Run(() => HardwareMonitor.GetTopMemoryProcesses(12));
                if (!_forceExit) LstTopProcesses.ItemsSource = processes;
            }
            finally { _refreshingTopProcesses = false; }
        }
        private void RefreshStartupApps() => LstStartupApps.ItemsSource = OptimizerEngine.GetStartupApps();

        private void BtnRefreshStartup_Click(object sender, RoutedEventArgs e)
        {
            RefreshStartupApps();
            Log("Lista de programas de inicio actualizada.");
        }

        private async void StartupToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.CheckBox cb || cb.Tag is not StartupAppItem item) return;
            bool enable = cb.IsChecked == true;
            await ApplySystemChangeAsync(
                $"{(enable ? "Activaste" : "Desactivaste")} inicio automático: {item.Name}",
                $"Cambia si Windows inicia '{item.Name}' al iniciar sesión.",
                "Pestaña 2: Procesos y RAM", 2, "", new(SystemAction.StartupApp, item.Name, enable, UserScope: item.IsUserScope));
        }

        private SystemSnapshot CaptureBeforeSnapshot(SystemSnapshot? snapshot = null)
        {
            _beforeSnapshot = snapshot ?? CaptureComparisonSnapshot();
            TxtBeforeMain.Text = $"RAM usada: {_beforeSnapshot.UsedRamGb:0.00} GB • CPU: {_beforeSnapshot.CpuPercent:0.0}%";
            TxtBeforeSub.Text = $"Guardado a las {_beforeSnapshot.Timestamp:HH:mm:ss} • {_beforeSnapshot.PowerScheme}";
            return _beforeSnapshot;
        }

        private void ShowChangeCard(string heading, string summary, string detail, string titleBrush = "AccentBrush")
        {
            TxtAfterTitle.SetResourceReference(TextBlock.ForegroundProperty, titleBrush);
            TxtAfterTitle.Text = heading;
            TxtAfterMain.Text = summary;
            TxtAfterSub.Text = detail;
        }

        private void ShowStartupSnapshot(SystemSnapshot snapshot, string? status = null,
            string heading = "ESTADO AL ABRIR", string titleBrush = "AccentBrush")
        {
            string detail = $"Procesos en la captura: {snapshot.ProcessCount} · {snapshot.Timestamp:HH:mm:ss} · {snapshot.PowerScheme}";
            if (status != null) detail = $"{status}\n{detail}";
            ShowChangeCard(heading, $"RAM: {snapshot.UsedRamGb:0.00} GB\nCPU: {snapshot.CpuPercent:0.0}%", detail, titleBrush);
        }

        private async Task CaptureAfterAndCompareAsync(string actionTitle, string explanation, string sectionName,
            int tabIdx, string ctrlName, string? verifiedStatus = null, ProfileSettingResult[]? profileSettings = null)
        {
            await Task.Delay(600);
            string resultDelta = await RenderAfterComparisonAsync();
            RecordAction(actionTitle, explanation, sectionName, tabIdx, ctrlName,
                verifiedStatus == null ? resultDelta : $"{verifiedStatus} {resultDelta}", profileSettings);
            Log($"{actionTitle}. {verifiedStatus ?? ""} {resultDelta}");
        }

        private static SystemSnapshot CaptureComparisonSnapshot() =>
            HardwareMonitor.CaptureSnapshot(OptimizerEngine.GetActivePowerSchemeName(), includeGpu: false);

        private async Task<string> RenderAfterComparisonAsync()
        {
            var before = _beforeSnapshot;
            var after = await Task.Run(CaptureComparisonSnapshot);
            RenderLiveMetrics(after);
            if (before == null)
            {
                ShowChangeCard("DESPUÉS DEL CAMBIO", after.PowerScheme, "Sin medición de referencia.");
                return "Se obtuvo el estado posterior.";
            }

            double ramDiffMb = Math.Round((before.UsedRamGb - after.UsedRamGb) * 1024, 0);
            string ramDeltaText = ramDiffMb switch
            {
                > 0 => $"{ramDiffMb} MB menos en uso",
                < 0 => $"{Math.Abs(ramDiffMb)} MB más en uso",
                _ => "sin cambio"
            };
            double cpuDiff = Math.Round((double)after.CpuPercent - before.CpuPercent, 1);
            string cpuDeltaText = $"{cpuDiff:+0.0;-0.0;0.0} pp";
            string processDelta = FormatProcessDelta(before.ProcessCount, after.ProcessCount);

            ShowChangeCard("DESPUÉS DEL CAMBIO",
                $"RAM: {after.UsedRamGb:0.00} GB ({ramDeltaText})\nCPU: {after.CpuPercent:0.0}% ({cpuDeltaText})",
                $"Procesos en la captura: {after.ProcessCount} ({processDelta}) · {after.Timestamp:HH:mm:ss} · {after.PowerScheme}");

            return $"Resultado: {after.PowerScheme} • RAM pasó de {before.UsedRamGb:0.00} GB a {after.UsedRamGb:0.00} GB ({ramDeltaText}). Procesos: {before.ProcessCount} → {after.ProcessCount}.";
        }

        internal static string FormatProcessDelta(int before, int after) => (after - before) switch
        {
            > 0 => $"{after - before} más",
            < 0 => $"{before - after} menos",
            _ => "sin cambio"
        };

        private void BtnCaptureBefore_Click(object sender, RoutedEventArgs e)
        {
            if (_startingUp || _systemChangeInProgress) return;
            var baseline = CaptureBeforeSnapshot();
            _manualComparisonReference = true;
            ShowChangeCard("REFERENCIA ACTUALIZADA", baseline.PowerScheme,
                "Los próximos resultados se compararán con esta medición.");
            Log("Foto del estado actual guardada.");
        }

        private async void BtnRefreshPower_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var reading = await Task.Run(() => (Power: OptimizerEngine.GetPowerPlanDetails(),
                    Restricted: OptimizerEngine.ReadEnforcedPowerPlan().HasValue,
                    Plans: OptimizerEngine.GetPowerSchemes()));
                if (_forceExit || _systemChangeInProgress) return;
                RenderPowerDiagnostics(reading.Power, reading.Restricted, reading.Plans);
                Log("Planes y opciones avanzadas de energía actualizados.");
            }
            catch (Exception ex) { if (!_forceExit) Log($"No se pudo leer el plan de energía. {ex.Message}"); }
        }

        private async void BtnApplyPowerPlan_Click(object sender, RoutedEventArgs e)
        {
            if (_startingUp || _systemChangeInProgress || _benchmarkCancellation != null) return;
            if (CmbPowerPlans.SelectedItem is not WindowsPowerPlanItem selected) return;

            if (selected.IsActive)
            {
                ShowChangeCard("AJUSTE YA APLICADO", $"El plan «{selected.Name}» ya está activo",
                    "Windows ya está usando este plan de energía. No hizo falta realizar cambios.", "SuccessBrush");
                Log($"El plan de energía «{selected.Name}» ya estaba activo; no se realizaron cambios.");
                return;
            }

            await ApplySystemChangeAsync(
                $"Cambiaste el plan de energía a {selected.Name}",
                "Cambia el plan de energía activo en Windows.",
                "Pestaña 0: Energía y perfiles", 0, "CardPowerPlans",
                new(SystemAction.PowerScheme, Adapter: selected.Id), "CUSTOM");
        }

        private void UpdatePowerBadge()
        {
            if (_hasBattery == false)
            {
                TxtPowerState.Text = "PC de sobremesa · corriente";
                TxtPowerState.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
                return;
            }
            bool? ac = HardwareMonitor.GetAcState();
            TxtPowerState.Text = ac switch
            {
                true => "Conectado a la corriente",
                false => "Usando batería",
                _ => "Fuente de energía desconocida"
            };
            TxtPowerState.SetResourceReference(TextBlock.ForegroundProperty, ac == true ? "SuccessBrush" : "WarningBrush");
        }

        private void SyncSwitchesFromSystem(TweakState? state = null)
        {
            var s = state ?? OptimizerEngine.ReadCurrentState();
            SwGameMode.IsChecked = s.GameMode;
            SwHags.IsChecked = s.Hags;
            SwWindowedOpt.IsChecked = s.WindowedOptimizations;
            SwHighResTimer.IsChecked = s.HighResTimer;
            SwGameDvr.IsChecked = s.DisableGameDvr;
            SwMmcss.IsChecked = s.MmcssGaming;
            SwForegroundPrio.IsChecked = s.ForegroundPriority;
            SwCoreParking.IsChecked = s.DisableCoreParking;
            SwSuspendServices.IsChecked = s.SuspendHeavyServices;
            SwUsbPcie.IsChecked = s.DisableUsbPciePowerSave;
            SwNagle.IsChecked = s.DisableNagle;
            SwBgApps.IsChecked = s.LimitBackgroundApps;
            SwFastUi.IsChecked = OptimizerEngine.IsFastUiResponseEnabled();
            SwMouseAccel.IsChecked = OptimizerEngine.IsMouseAccelerationDisabled();
            SwTransparency.IsChecked = OptimizerEngine.IsTransparencyDisabled();
            UpdatePlanRecommendationCard();
        }

        private async void Switch_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not System.Windows.Controls.CheckBox cb ||
                !TweakInfoMap.TryGetValue(cb.Name, out var meta)) return;
            if (_startingUp || _systemChangeInProgress || _benchmarkCancellation != null) return;

            bool isOn = cb.IsChecked == true;
            bool alreadyApplied;
            SetSystemChangeBusy(true, "Comprobando ajuste en Windows…");
            try
            {
                alreadyApplied = await Task.Run(() => OptimizerEngine.IsTweakInState(cb.Name, isOn));
            }
            catch { alreadyApplied = false; }
            finally { SetSystemChangeBusy(false); }

            if (alreadyApplied)
            {
                string stateWord = isOn ? "activado" : "desactivado";
                ShowChangeCard("AJUSTE YA APLICADO", $"{meta.Title} ya estaba {stateWord}",
                    "Windows ya tenía este ajuste configurado. No hizo falta realizar cambios.", "SuccessBrush");
                Log($"{meta.Title} ya estaba {stateWord}; no se realizaron cambios.");
                await RefreshVerifiedControlsAsync();
                return;
            }

            string? profileTag = cb.Name is "SwFastUi" or "SwMouseAccel" or "SwTransparency" ? null : "CUSTOM";
            await ApplySystemChangeAsync(
                $"{(isOn ? "Activaste" : "Desactivaste")}: {meta.Title}",
                meta.PlainDesc, meta.SectionName, meta.TabIndex, meta.RowName, new(SystemAction.Tweak, cb.Name, isOn), profileTag);
        }

        private async void BtnJugar_Click(object sender, RoutedEventArgs e) =>
            await ApplyGamingModeAsync("Activaste el plan Jugar (Máximo Rendimiento)", "Ajusta la energía y las opciones de juego de WinPulse.");

        private Task<bool> ApplyGamingModeAsync(string title, string explanation, bool automatic = false, bool skipVerification = false) =>
            ApplyProfileAsync(title, explanation, UsageProfile.GamingMax, "JUGAR",
                DefaultProfileAdjustments(UsageProfile.GamingMax), automatic, skipVerification);

        private async void BtnEstudiar_Click(object sender, RoutedEventArgs e) =>
            await ApplyStudyModeAsync("Activaste el plan Estudiar / Trabajar", "Aplica los ajustes de energía para uso diario.");

        private Task<bool> ApplyStudyModeAsync(string title, string explanation, bool skipVerification = false) =>
            ApplyProfileAsync(title, explanation, UsageProfile.StudyWork, "ESTUDIAR",
                DefaultProfileAdjustments(UsageProfile.StudyWork), skipVerification: skipVerification);

        private async void BtnChill_Click(object sender, RoutedEventArgs e) =>
            await ApplyChillModeAsync("Activaste el plan Estar Chill", "Aplica los ajustes de ahorro de energía.");

        private Task<bool> ApplyChillModeAsync(string title, string explanation, bool automatic = false, bool skipVerification = false) =>
            ApplyProfileAsync(title, explanation, UsageProfile.ChillBattery, "CHILL",
                DefaultProfileAdjustments(UsageProfile.ChillBattery), automatic, skipVerification);

        internal static TweakState DefaultProfileAdjustments(UsageProfile profile) =>
            OptimizerEngine.DefaultProfileAdjustments(profile);

        private async Task<bool> ApplyProfileAsync(string title, string explanation, UsageProfile profile, string tag,
            TweakState values, bool automatic = false, bool skipVerification = false)
        {
            var action = profile == UsageProfile.FactoryDefault ? SystemAction.FactoryProfile : SystemAction.Profile;
            bool trimMemory = !automatic && profile != UsageProfile.FactoryDefault;
            if (automatic || skipVerification)
            {
                bool appliedAuto = await ApplySystemChangeAsync(title, explanation, "Barra superior: Planes de uso", 0, "CardProfiles",
                    new(action, Power: profile, Values: values),
                    tag, compareMetrics: true, trimMemory: trimMemory, automatic: automatic);
                if (appliedAuto) SyncSmartGamingOptionsForProfile(profile);
                return appliedAuto;
            }

            if (_startingUp || _systemChangeInProgress || _benchmarkCancellation != null) return false;

            ProfileVerificationResult? verification = null;
            SetSystemChangeBusy(true, "Comprobando perfil activo…");
            try
            {
                verification = await Task.Run(() => OptimizerEngine.VerifyProfileState(profile, values));
            }
            catch (Exception ex)
            {
                Log($"No se pudo comprobar el estado previo del plan. {ex.Message}");
            }
            finally
            {
                SetSystemChangeBusy(false);
            }

            if (verification is { IsFullyActive: true })
            {
                string displayName = GetProfileDisplayName(tag);
                bool sameBanner = _activeProfileTag == tag && !_savedProfileNeedsReapply;
                RestoreSavedProfileBanner(tag, verification);
                _manualAutomationOverride = true;

                string mainText = sameBanner ? $"«{displayName}» ya está activo" : $"«{displayName}» activado sin cambios";
                string detailText = sameBanner
                    ? "Todos sus ajustes ya coinciden con la configuración actual de Windows. No hace falta volver a aplicarlo."
                    : "Windows ya tenía todos los ajustes de este plan aplicados. No hizo falta modificar nada.";
                if (verification.PendingRestart)
                    detailText += " Algunos ajustes previos surtirán efecto al reiniciar Windows.";

                ShowChangeCard("PLAN YA AJUSTADO", mainText, detailText, "SuccessBrush");
                Log(sameBanner
                    ? $"El plan «{displayName}» ya estaba ajustado en Windows; no se realizaron cambios."
                    : $"Activaste «{displayName}». Windows ya tenía todos sus ajustes aplicados.");
                if (!IsVisible)
                    _trayIcon?.ShowBalloonTip(2500, "WinPulse Pro", $"El plan «{displayName}» ya está aplicado.", WinForms.ToolTipIcon.Info);

                SyncSmartGamingOptionsForProfile(profile);
                await RefreshVerifiedControlsAsync();
                return true;
            }

            bool applied = await ApplySystemChangeAsync(title, explanation, "Barra superior: Planes de uso", 0, "CardProfiles",
                new(action, Power: profile, Values: values),
                tag, compareMetrics: true, trimMemory: trimMemory, automatic: false);
            if (applied) SyncSmartGamingOptionsForProfile(profile);
            return applied;
        }

        private void SyncSmartGamingOptionsForProfile(UsageProfile profile)
        {
            if (profile is not (UsageProfile.GamingMax or UsageProfile.FactoryDefault)) return;
            bool enable = profile == UsageProfile.GamingMax;
            var store = HistoryManager.Load();
            if (store.SmartGameScheduling != enable || store.AutoForceDedicatedGpu != enable)
            {
                store.SmartGameScheduling = enable;
                store.AutoForceDedicatedGpu = enable;
                if (!HistoryManager.Save(store)) return;
            }

            _loading = true;
            SwSmartGamePriority.IsChecked = enable;
            SwAutoForceDgpu.IsChecked = enable;
            _loading = false;
            if (!enable) ProcessResourceMonitor.RestoreSmartGameScheduling();
            TxtSmartGamePriorityStatus.Text = enable ? "Activo · esperando juego" : "Desactivado";
            TxtSmartGamePriorityStatus.SetResourceReference(TextBlock.ForegroundProperty, enable ? "AccentBrush" : "QuietBrush");
            UpdatePlanRecommendationCard();
        }

        private async void BtnPurgeRam_Click(object sender, RoutedEventArgs e)
        {
            if (_startingUp || _systemChangeInProgress || _benchmarkCancellation != null) return;
            SetSystemChangeBusy(true, "Liberando memoria RAM…");
            ShowChangeCard("AJUSTE DE RAM EN CURSO", "Ajustando la memoria de los procesos…", "");
            try
            {
                var result = await Task.Run(OptimizerEngine.MeasureMemoryTrim);
                string measurement = DescribeMemoryTrim(result);
                string details = DescribeMemoryTrimProcesses(result);
                ShowChangeCard("RAM TRAS EL AJUSTE", measurement, details);
                TxtQuickCleanupHint.Text = $"Último ajuste: {measurement.Replace(Environment.NewLine, " · ")}";
                TxtQuickCleanupHint.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                RecordAction("Ajuste de memoria de procesos", details, "Pestaña 2: Procesos y RAM", 2, "", measurement);
                Log($"{measurement.Replace(Environment.NewLine, ". ")}. {details}");
                RefreshTopProcesses();
            }
            catch (Exception ex)
            {
                ShowChangeCard("AJUSTE DE RAM", "No se pudo completar el ajuste", "Consulta el registro.");
                Log($"No se pudo ajustar la memoria. {ex.Message}");
            }
            finally { SetSystemChangeBusy(false); }
        }

        internal static string DescribeMemoryTrim(MemoryTrimResult result)
        {
            if (result.BeforeBytes is not ulong before || result.AfterBytes is not ulong after)
                return "RAM usada antes y después no disponible.";

            double difference = Math.Round(((double)after - before) / 1_048_576);
            string change = difference switch
            {
                < 0 => $"{-difference:N0} MB menos en uso",
                > 0 => $"{difference:N0} MB más en uso",
                _ => "Sin variación visible"
            };
            return $"RAM usada: {before / 1_073_741_824.0:0.00} → {after / 1_073_741_824.0:0.00} GB{Environment.NewLine}{change}";
        }

        private static string DescribeMemoryTrimProcesses(MemoryTrimResult result) =>
            $"Procesos con ajuste confirmado: {result.Adjusted} · Omitidos: {result.Skipped}. Las apps activas recargan su caché al usarlas; cierra las innecesarias para liberar más RAM.";

        private async void BtnReset_Click(object sender, RoutedEventArgs e) =>
            await ApplyFactoryProfileAsync("Aplicaste los valores predefinidos de WinPulse",
                "Selecciona los valores predefinidos del botón De fábrica.");

        private Task<bool> ApplyFactoryProfileAsync(string title, string explanation, bool skipVerification = false) =>
            ApplyProfileAsync(title, explanation, UsageProfile.FactoryDefault, "FABRICA",
                DefaultProfileAdjustments(UsageProfile.FactoryDefault), skipVerification: skipVerification);

        private void BtnRefreshProcs_Click(object sender, RoutedEventArgs e)
        {
            RefreshTopProcesses();
            Log("Lista de programas actualizada.");
        }

        private async void BtnKillProc_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is int pid)
            {
                if (OptimizerEngine.KillProcessSafely(pid, out string msg))
                {
                    Log(msg);
                    RefreshTopProcesses();
                    await CaptureAfterAndCompareAsync(
                        "Cerraste un programa pesado",
                        msg,
                        "Pestaña 2: Procesos y RAM",
                        2,
                        ""
                    );
                }
                else Log(msg);
            }
        }

        private async void BtnMaintenance_Click(object sender, RoutedEventArgs e)
        {
            if (_startingUp || _systemChangeInProgress || _benchmarkCancellation != null) return;
            SetSystemChangeBusy(true, "Limpiando temporales y cachés…");
            ShowChangeCard("MANTENIMIENTO EN CURSO", "Limpiando archivos temporales y cachés…",
                "Eliminando temporales de usuario y cachés de shaders.");
            TxtQuickCleanupHint.Text = "Limpiando archivos temporales y cachés…";
            TxtQuickCleanupHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            Log("Iniciando limpieza de archivos temporales y cachés.");
            string? measurement = null;
            string? measurementDetail = null;
            try
            {
                int filesDeleted = await OptimizerEngine.CleanTemporaryFilesAsync();
                int systemFilesDeleted = 0;
                bool elevatedCompleted = false;

                TxtBusyStatus.Text = "Limpiando Windows\\Temp y DNS…";
                ShowChangeCard("MANTENIMIENTO EN CURSO", "Limpiando Windows\\Temp y caché DNS…",
                    $"Temporales de usuario eliminados: {filesDeleted}. Autoriza administrador si deseas limpiar también Windows\\Temp y DNS.");
                try
                {
                    var diskResult = await RunSystemRequestAsync(new(SystemAction.DiskMaintenance),
                        "Limpiar Windows\\Temp y vaciar la caché DNS.");
                    systemFilesDeleted = int.TryParse(diskResult.Notice, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int sysDeleted) ? sysDeleted : 0;
                    elevatedCompleted = true;
                }
                catch (OperationCanceledException)
                {
                    Log("Se omitió la limpieza elevada (Windows\\Temp y DNS) al cancelar el permiso de administrador.");
                }

                TxtBusyStatus.Text = "Liberando memoria RAM…";
                ShowChangeCard("MANTENIMIENTO EN CURSO", "Liberando memoria RAM…",
                    "Se mide la RAM justo antes y después de ajustar los procesos.");
                var trim = await Task.Run(OptimizerEngine.MeasureMemoryTrim);
                int totalDeleted = filesDeleted + systemFilesDeleted;
                measurement = DescribeMemoryTrim(trim);
                string elevatedSummary = elevatedCompleted
                    ? $"Archivos eliminados: {totalDeleted} (usuario/caché GPU: {filesDeleted}, Windows\\Temp: {systemFilesDeleted}). Windows confirmó el vaciado de DNS."
                    : $"Archivos de usuario eliminados: {filesDeleted} (se omitió Windows\\Temp y DNS).";
                string fullCleanup = $"{elevatedSummary} {DescribeMemoryTrimProcesses(trim)}";
                measurementDetail = fullCleanup;
                ShowChangeCard("RAM TRAS LA LIMPIEZA", measurement, fullCleanup);
                RefreshTopProcesses();
                TxtQuickCleanupHint.Text = $"Último ajuste: {measurement.Replace(Environment.NewLine, " · ")} · {totalDeleted} archivos eliminados.";
                TxtQuickCleanupHint.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
                RecordAction("Mantenimiento", fullCleanup, "Pestaña 0: Opciones de Energía y Equivalencias", 0,
                    "RowMaintenance", measurement);
                Log($"Mantenimiento completado. {fullCleanup} {measurement}");
            }
            catch (Exception ex)
            {
                if (measurementDetail != null)
                    TxtAfterSub.Text = $"{measurementDetail} • Mantenimiento incompleto. Consulta Historial.";
                else
                    ShowChangeCard("MANTENIMIENTO", "Mantenimiento incompleto", "Consulta el detalle en Historial.");
                RecordAction("MANTENIMIENTO INCOMPLETO", ex.Message,
                    "Pestaña 0: Opciones de Energía y Equivalencias", 0, "RowMaintenance",
                    measurement ?? "Falló una parte del mantenimiento.");
                Log($"Mantenimiento incompleto: {ex.Message}");
            }
            finally { SetSystemChangeBusy(false); }
        }

        private async void BtnBackup_Click(object sender, RoutedEventArgs e)
        {
            if (_systemChangeInProgress) return;
            SetSystemChangeBusy(true, "Creando punto de restauración…");
            Log("Creando y comprobando el punto de restauración.");
            try
            {
                int exported = await Task.Run(OptimizerEngine.BackupRegistry);
                string id = (await RunSystemRequestAsync(new(SystemAction.RestorePoint), "Crear un punto de restauración de Windows.")).Notice;
                string status = $"Punto de restauración confirmado por Windows (n.º {id}). Exportaciones de registro: {exported}.";
                RecordAction("Creaste un punto de restauración", status,
                    "Pestaña 0: Opciones de Energía y Equivalencias", 0, "RowBackup", "Verificado.");
                Log(status);
            }
            catch (Exception ex)
            {
                RecordAction("No se confirmó la copia de seguridad", ex.Message,
                    "Pestaña 0: Opciones de Energía y Equivalencias", 0, "RowBackup", "Falló la verificación.");
                Log($"Copia de seguridad incompleta: {ex.Message}");
            }
            finally { SetSystemChangeBusy(false); }
        }

        private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_systemChangeInProgress) return;
            if (!HistoryManager.ClearHistoryKeepBaseline())
            {
                Log("No se pudo borrar la lista de acciones.");
                return;
            }
            ShowHistory(new());
            Log("Lista de acciones borrada. Los datos para deshacer se conservan.");
        }

        private async Task<bool> ApplySystemChangeAsync(string title, string explanation, string section,
            int tabIndex, string control, SystemRequest request, string? profile = null, bool compareMetrics = false,
            bool trimMemory = false, bool automatic = false)
        {
            if (_startingUp || _systemChangeInProgress || _benchmarkCancellation != null) return false;
            SetSystemChangeBusy(true, $"Aplicando: {title}…");
            string previousProfile = _activeProfileTag;
            bool resultRecorded = false;
            Log("Aplicando y verificando el cambio.");
            ShowChangeCard("CAMBIO EN CURSO", "Verificando con Windows…", title);
            try
            {
                if (compareMetrics && !_manualComparisonReference)
                {
                    try { CaptureBeforeSnapshot(); }
                    catch (Exception ex) { Log($"Se conserva la medición de referencia anterior. {ex.Message}"); }
                }
                var result = await RunSystemRequestAsync(request with
                {
                    Title = title, PreviousProfile = previousProfile, AutomaticProfile = automatic ? profile : null,
                    ReplaceAutomation = !automatic && profile != null, ReportProfile = control == "CardProfiles"
                }, explanation);
                var verifiedSettings = result.Settings ?? [];
                if (_usageStatistics.IsEnabled)
                    _usageStatistics.RecordChange(profile, result.Changed,
                        verifiedSettings.Count(setting => setting.Status == ProfileSettingResult.BlockedPlanStatus));
                resultRecorded = true;
                if (!automatic && profile != null) _manualAutomationOverride = true;
                string restartNotice = result.Restart ? DescribePendingRestart(verifiedSettings) : "";
                string status = result.Changed
                    ? $"Configuración guardada y verificada. {restartNotice}".TrimEnd()
                    : "Verificado. Los valores ya estaban configurados.";
                if (result.Notice.Length > 0)
                {
                    string summary = result.Changed
                        ? "Ajustes disponibles verificados."
                        : "Windows conserva los valores actuales.";
                    if (result.Restart) summary += $" {restartNotice}";
                    status = $"{summary} {result.Notice}";
                }
                if (profile != null) RestoreSavedProfileBanner(profile);
                if (trimMemory)
                {
                    try
                    {
                        var (trimmed, _) = await Task.Run(OptimizerEngine.TrimMemoryWorkingSets);
                        status += $" Memoria ajustada en {trimmed} procesos.";
                    }
                    catch (Exception ex) { Log($"No se pudo ajustar la memoria de los procesos. {ex.Message}"); }
                }
                if (compareMetrics)
                {
                    string comparisonNotice = !result.Changed && result.Notice.Length == 0
                        ? "Los valores del perfil ya estaban configurados."
                        : result.Notice;
                    if (result.Restart) comparisonNotice = $"{comparisonNotice} {restartNotice}".Trim();
                    await RecordVerifiedComparisonAsync(title, explanation, section, tabIndex, control, status, comparisonNotice,
                        verifiedSettings.Length > 0 ? verifiedSettings : null);
                }
                else
                {
                    ShowChangeCard("RESULTADO DEL CAMBIO",
                        result.Changed ? "Configuración verificada" : "La configuración ya estaba aplicada",
                        result.Restart ? restartNotice : title);
                    RecordAction(title, explanation, section, tabIndex, control, status);
                    Log($"{title}. {status}");
                }
                return true;
            }
            catch (OperationCanceledException ex)
            {
                ShowChangeCard("CAMBIO CANCELADO", ex.Message, title);
                Log(ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                var settings = (ex as ProfileChangeException)?.Settings;
                if (!resultRecorded && _usageStatistics.IsEnabled)
                    _usageStatistics.RecordChange(profile, null,
                        settings?.Count(setting => setting.Status == ProfileSettingResult.BlockedPlanStatus) ?? 0);
                ShowChangeCard("RESULTADO DEL CAMBIO", "No se completó el cambio", "Consulta el detalle en Historial.");
                RecordAction("No se completó el cambio", title, section, tabIndex, control, ex.Message,
                    settings is { Length: > 0 } ? settings : null);
                Log($"No se completó el cambio. {ex.Message}");
                return false;
            }
            finally
            {
                try { await RefreshVerifiedControlsAsync(); }
                finally { SetSystemChangeBusy(false); }
            }
        }

        private Task<SystemResult> RunSystemRequestAsync(SystemRequest request, string reason)
        {
            if (request.RequiresAdministrator && !WindowsElevation.IsAdministrator)
            {
                if (request.Action is SystemAction.Profile or SystemAction.FactoryProfile)
                    reason = "Aplicar los ajustes de energía, juegos, servicios y red de este perfil.";
                ShowChangeCard("PERMISO DE ADMINISTRADOR", "Windows pedirá autorización", reason);
                Log($"Windows pedirá autorización de administrador. {reason}");
            }
            return WindowsElevation.RunAsync(request);
        }

        private async Task RecordVerifiedComparisonAsync(string title, string explanation, string section,
            int tabIndex, string control, string status, string notice, ProfileSettingResult[]? profileSettings)
        {
            try
            {
                await CaptureAfterAndCompareAsync(title, explanation, section, tabIndex, control, status, profileSettings);
                if (notice.Length > 0) TxtAfterSub.Text += $" {notice}";
            }
            catch (Exception ex)
            {
                ShowChangeCard("RESULTADO DEL CAMBIO", "Configuración verificada",
                    "No se pudo obtener la medición posterior. Consulta Historial.");
                RecordAction(title, explanation, section, tabIndex, control, status, profileSettings);
                Log($"{title}. {status} No se pudo obtener la medición posterior. {ex.Message}");
            }
        }

        private static string VerifiedStatus(bool restart) => restart
            ? $"Configuración guardada y verificada. {DescribePendingRestart(null)}"
            : "Configuración guardada y verificada.";

        internal static string DescribePendingRestart(ProfileSettingResult[]? settings)
        {
            var pending = settings?.Where(setting => setting.Status == ProfileSettingResult.PendingRestartStatus)
                .Select(setting => setting.Setting).Distinct().ToArray() ?? [];
            const string prefix = "Puedes seguir usando el equipo. ";
            return pending.Length == 0
                ? prefix + "Algunos ajustes guardados surtirán efecto en el próximo reinicio de Windows."
                : $"{prefix}Pendientes hasta el próximo reinicio de Windows: {string.Join(", ", pending)}.";
        }

        private void SetSystemChangeBusy(bool busy, string? statusText = null)
        {
            if (busy)
            {
                _systemChangeSequence++;
                _loadedPingCancellation?.Cancel();
                TxtBusyStatus.Text = statusText ?? "Aplicando cambios…";
                BusyStatusPill.Visibility = Visibility.Visible;
            }
            else
            {
                BusyStatusPill.Visibility = Visibility.Collapsed;
            }
            _systemChangeInProgress = busy;
            CardProfiles.IsEnabled = !busy;
            MainTabs.IsEnabled = !busy;
            UpdateRestoreButtons();
            if (_settingStatusWindow is { } status && !_forceExit)
                _ = status.UpdateAsync(TxtActiveModeTitle.Text, busy);
        }

        private async Task RefreshVerifiedControlsAsync()
        {
            try
            {
                var refreshed = await Task.Run(() => (State: OptimizerEngine.ReadCurrentState(),
                    Apps: OptimizerEngine.GetStartupApps(), AutoStart: OptimizerEngine.IsAutoStartEnabled(),
                    Power: OptimizerEngine.GetPowerPlanDetails(), Restricted: OptimizerEngine.ReadEnforcedPowerPlan().HasValue,
                    Plans: OptimizerEngine.GetPowerSchemes()));
                _loading = true;
                SyncSwitchesFromSystem(refreshed.State);
                LstStartupApps.ItemsSource = refreshed.Apps;
                SwStartWithWindows.IsChecked = refreshed.AutoStart;
                RenderPowerDiagnostics(refreshed.Power, refreshed.Restricted, refreshed.Plans);
            }
            catch (Exception ex) { Log($"No se pudo actualizar la lectura del sistema. {ex.Message}"); }
            finally { _loading = false; }
        }

        private void UpdateRestoreButtons()
        {
            BtnUndoChange.IsEnabled = false;
            BtnRestoreOriginal.IsEnabled = false;
            TxtRestoreStatus.Text = "Solo se pueden recuperar los cambios registrados por esta versión de WinPulse.";
            if (_systemChangeInProgress)
            {
                TxtRestoreStatus.Text = "Comprobando el cambio con Windows…";
                return;
            }
            try
            {
                var store = SystemChangeJournal.Load();
                BtnUndoChange.Content = store.Pending != null || store.RestoringOriginal == false
                    ? "Recuperar cambio pendiente" : "Deshacer último cambio";
                BtnRestoreOriginal.Content = store.RestoringOriginal == true
                    ? "Continuar restauración original" : "Restaurar mi configuración original";
                BtnUndoChange.IsEnabled = store.RestoringOriginal != true && (store.Pending != null || store.Undo.Count != 0);
                BtnRestoreOriginal.IsEnabled = store.RestoringOriginal != false && store.Pending == null && store.Original.Count != 0;
                if (store.RestoringOriginal == true)
                    TxtRestoreStatus.Text = "La restauración de la configuración original está pendiente de completar.";
                else if (store.Pending != null)
                    TxtRestoreStatus.Text = $"Hay una operación pendiente de recuperar: {store.Pending.Title}.";
                else if (store.Undo.Count != 0)
                    TxtRestoreStatus.Text = $"Último cambio reversible: {store.Undo[^1].Title}. {TxtRestoreStatus.Text}";
            }
            catch (Exception ex) { TxtRestoreStatus.Text = $"No se pudo leer el registro de restauración. {ex.Message}"; }
        }

        private async void BtnUndoChange_Click(object sender, RoutedEventArgs e) => await RestoreSystemChangeAsync(false);

        private async void BtnRestoreOriginal_Click(object sender, RoutedEventArgs e)
        {
            var choice = System.Windows.MessageBox.Show(this,
                "Se recuperarán los valores guardados antes de que WinPulse cambiara cada ajuste registrado.",
                "Restaurar mi configuración original", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (choice == MessageBoxResult.Yes) await RestoreSystemChangeAsync(true);
        }

        private async Task RestoreSystemChangeAsync(bool original)
        {
            if (_systemChangeInProgress) return;
            SetSystemChangeBusy(true, "Restaurando configuración…");
            try
            {
                var restored = await RunSystemRequestAsync(new(SystemAction.Restore, Enabled: original), "Recuperar los valores de Windows guardados por WinPulse.");
                _lastAcState = HardwareMonitor.GetAcState();
                _manualAutomationOverride = true;
                UpdatePowerBadge();
                RestoreSavedProfileBanner(original ? "ORIGINAL" : restored.Profile ?? "");
                string status = VerifiedStatus(restored.Restart);
                if (restored.Notice.Length > 0) status += $" {restored.Notice}";
                ShowChangeCard("RESULTADO DE LA RESTAURACIÓN", restored.Title ?? "Configuración recuperada", status);
                RecordAction(restored.Title ?? "Configuración recuperada", "Se recuperaron los valores guardados.", "Pestaña 4: Historial", 4, "", status);
                Log($"{restored.Title}. {status}");
            }
            catch (Exception ex)
            {
                ShowChangeCard("RESULTADO DE LA RESTAURACIÓN", "Restauración incompleta", "Consulta el detalle en Historial.");
                RecordAction("Restauración incompleta", ex.Message, "Pestaña 4: Historial", 4, "", "Puedes volver a intentar la restauración desde aquí.");
                Log($"Restauración incompleta. {ex.Message}");
            }
            finally
            {
                try { await RefreshVerifiedControlsAsync(); }
                finally { SetSystemChangeBusy(false); }
            }
        }

        private void Log(string msg)
        {
            TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
            if (TxtLog.IsVisible) TxtLog.ScrollToEnd();
        }
    }
}
