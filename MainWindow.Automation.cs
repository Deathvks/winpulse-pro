using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace WinPulseOptimizer
{
    public partial class MainWindow
    {
        private bool _checkingAutomation;
        private string? _lastAutomationTarget;
        private string? _detectedGame;
        private bool _automationTargetKnown;
        private bool _manualAutomationOverride;
        private long _retryAutomationAt;
        private string? _lastAutomationPermissionNotice;
        private string[] _includedGames = Array.Empty<string>();
        private string[] _excludedGames = Array.Empty<string>();

        private void LoadAutomationRules(AuditStore store)
        {
            SwGameOnBattery.IsChecked = store.AutoGameOnBattery;
            _includedGames = (store.IncludedGameExecutables ?? new()).Select(HardwareMonitor.NormalizeExecutableName).ToArray();
            _excludedGames = (store.ExcludedGameExecutables ?? new()).Select(HardwareMonitor.NormalizeExecutableName).ToArray();
            TxtIncludedGames.Text = string.Join(Environment.NewLine, _includedGames.Select(game => game + ".exe"));
            TxtExcludedGames.Text = string.Join(Environment.NewLine, _excludedGames.Select(game => game + ".exe"));
            LoadGameProfiles(store.GameProfiles);
        }

        private void BtnSaveGameRules_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var store = HistoryManager.Load();
                store.IncludedGameExecutables = ReadRules(TxtIncludedGames.Text).ToList();
                store.ExcludedGameExecutables = ReadRules(TxtExcludedGames.Text).ToList();
                if (!HistoryManager.Save(store)) throw new IOException("No se pudieron guardar las reglas de detección.");
                LoadAutomationRules(store);
                ResetAutomationOverride();
                TxtGameRulesStatus.Text = "Reglas guardadas.";
            }
            catch (Exception ex) { TxtGameRulesStatus.Text = ex.Message; }
        }

        internal static string[] ReadRules(string input)
        {
            var entries = input.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(HardwareMonitor.NormalizeExecutableName).ToArray();
            if (entries.Any(name => name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new FormatException("Usa un nombre de ejecutable válido por línea.");
            return entries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private void ResetAutomationOverride()
        {
            _manualAutomationOverride = false;
            _retryAutomationAt = 0;
        }

        internal static string? ChooseAutomaticProfile(bool? onAc, bool gameDetected, bool allowGameOnBattery, bool chillOnBattery,
            GameProfile? matched = null)
        {
            if (!onAc.HasValue) return null;
            if (gameDetected && (onAc.Value || allowGameOnBattery)) return matched?.Tag ?? "JUGAR";
            return !onAc.Value && chillOnBattery ? "CHILL" : null;
        }

        private async void BtnApproveAutomation_Click(object sender, RoutedEventArgs e)
        {
            _retryAutomationAt = 0;
            await ReconcileAutomationAsync(approved: true);
        }

        private bool WaitForAutomationPermission(bool approved, string action)
        {
            if (approved || WindowsElevation.IsAdministrator) return false;
            TxtAutoStatus.Text = "Permiso de administrador pendiente";
            BtnApproveAutomation.Content = action;
            BtnApproveAutomation.Visibility = Visibility.Visible;
            if (!IsVisible && _lastAutomationPermissionNotice != action)
            {
                _trayIcon?.ShowBalloonTip(4000, "WinPulse · Perfil detectado",
                    $"{action}. Abre WinPulse para autorizar el cambio.", System.Windows.Forms.ToolTipIcon.Info);
                _lastAutomationPermissionNotice = action;
            }
            return true;
        }

        private async Task ReconcileAutomationAsync(bool approved = false)
        {
            if (SwPauseAutomationForTests.IsChecked == true)
            {
                BtnApproveAutomation.Visibility = Visibility.Collapsed;
                _lastAutomationPermissionNotice = null;
                return;
            }
            if (_checkingAutomation || _systemChangeInProgress || _benchmarkCancellation != null ||
                _networkTesting) return;
            _checkingAutomation = true;
            try
            {
                var included = _includedGames;
                var excluded = _excludedGames;
                var profiles = _gameProfiles;
                string activeTag = _activeProfileTag;
                bool detectGames = SwAutoGame.IsChecked == true;
                string priorityTag = _lastAutomationTarget ?? activeTag;
                string[] preferred = profiles.OrderByDescending(profile => profile.OwnsTag(priorityTag))
                    .SelectMany(profile => profile.Executables).ToArray();
                string? game = detectGames ? await Task.Run(() => HardwareMonitor.DetectRunningGame(included, excluded, preferred)) : null;
                if (_systemChangeInProgress || _benchmarkCancellation != null || _networkTesting ||
                    SwPauseAutomationForTests.IsChecked == true || activeTag != _activeProfileTag || profiles != _gameProfiles ||
                    detectGames != (SwAutoGame.IsChecked == true) || included != _includedGames || excluded != _excludedGames)
                    return;

                if (_detectedGame != game)
                {
                    _detectedGame = game;
                    UpdateLiveMetricsSchedule();
                }
                BtnApproveAutomation.Visibility = Visibility.Collapsed;
                bool? onAc = _hasBattery == false ? true : HardwareMonitor.GetAcState();
                if (!onAc.HasValue)
                {
                    TxtAutoStatus.Text = "Fuente de energía desconocida";
                    return;
                }
                var matched = profiles.FirstOrDefault(profile =>
                    game != null && profile.Executables.Contains(game, StringComparer.OrdinalIgnoreCase));
                string? desired = ChooseAutomaticProfile(onAc, game != null, SwGameOnBattery.IsChecked == true,
                    SwAutoBattery.IsChecked == true, matched);
                if (_automationTargetKnown && _lastAutomationTarget != desired) ResetAutomationOverride();
                _automationTargetKnown = true;
                _lastAutomationTarget = desired;
                TxtAutoStatus.Text = game == null ? "Sin juego detectado" :
                    desired != null && desired != "CHILL" ? $"Juego: {game}" : $"Juego en batería: {game}";
                if (_manualAutomationOverride)
                {
                    TxtAutoStatus.Text = "Perfil elegido manualmente";
                    return;
                }
                if (Environment.TickCount64 < _retryAutomationAt)
                {
                    TxtAutoStatus.Text = "Cambio automático pendiente de reintento";
                    return;
                }

                var saved = SystemChangeJournal.Load();
                if (saved.Pending != null || saved.RestoringOriginal.HasValue)
                {
                    TxtAutoStatus.Text = "Recuperación pendiente · Historial";
                    return;
                }
                var previous = saved.Undo.LastOrDefault(o => o.Id == saved.AutomationOperationId);
                if (previous?.AutomaticProfile == desired && previous != null)
                {
                    if (desired != null && _activeProfileTag != desired) RestoreSavedProfileBanner(desired);
                    return;
                }
                if (previous != null)
                {
                    if (WaitForAutomationPermission(approved, "Recuperar perfil anterior")) return;
                    if (!await ReturnFromAutomaticProfileAsync())
                        _retryAutomationAt = Environment.TickCount64 + 30_000;
                    return;
                }
                if (desired == null || desired == _activeProfileTag || SwPauseAutomationForTests.IsChecked == true) return;
                if (WaitForAutomationPermission(approved, desired switch
                {
                    "JUGAR" => "Aplicar Jugar",
                    "CHILL" => "Aplicar Estar Chill",
                    _ => $"Aplicar {matched!.Name}"
                })) return;
                bool applied = desired switch
                {
                    "JUGAR" => await ApplyGamingModeAsync($"Automático: {game}",
                        "Aplicó Jugar tras detectar el ejecutable.", automatic: true),
                    "CHILL" => await ApplyChillModeAsync("Automático: equipo en batería",
                        "Aplicó Chill mientras el equipo usa la batería.", automatic: true),
                    _ => await ApplyProfileAsync($"Automático: {matched!.Name}", $"Juego detectado: {game}.exe",
                        matched.PowerBase, matched.Tag, matched.Adjustments, automatic: true)
                };
                if (!applied)
                {
                    TxtAutoStatus.Text = "Falló el cambio automático · Historial";
                    _retryAutomationAt = Environment.TickCount64 + 30_000;
                }
            }
            catch (Exception ex)
            {
                if (Environment.TickCount64 >= _retryAutomationAt)
                    Log($"La automatización no pudo comprobar el perfil. {ex.Message}");
                _retryAutomationAt = Environment.TickCount64 + 30_000;
            }
            finally
            {
                _checkingAutomation = false;
                if (BtnApproveAutomation.Visibility != Visibility.Visible)
                    _lastAutomationPermissionNotice = null;
            }
        }

        private async Task<bool> ReturnFromAutomaticProfileAsync()
        {
            if (_systemChangeInProgress) return false;
            SetSystemChangeBusy(true);
            try
            {
                var restored = await RunSystemRequestAsync(new(SystemAction.RestoreAutomatic),
                    "Recuperar el perfil anterior al cambio automático.");
                RestoreSavedProfileBanner(restored.Profile ?? "");
                string status = VerifiedStatus(restored.Restart);
                if (restored.Notice.Length > 0) status += $" {restored.Notice}";
                ShowChangeCard("RESULTADO DEL CAMBIO AUTOMÁTICO", "Perfil anterior recuperado", status);
                RecordAction("Perfil anterior recuperado", "Terminó la condición que activó el perfil automático.",
                    "Barra superior: Planes de uso", 0, "CardProfiles", status);
                Log($"Perfil anterior recuperado. {status}");
                return true;
            }
            catch (OperationCanceledException ex)
            {
                ShowChangeCard("RECUPERACIÓN CANCELADA", ex.Message, "");
                Log(ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                Log($"No se pudo recuperar el perfil anterior. {ex.Message}");
                ShowChangeCard("RESULTADO DEL CAMBIO AUTOMÁTICO", "No se recuperó el perfil anterior",
                    "WinPulse volverá a intentarlo.");
                TxtAutoStatus.Text = "No se pudo recuperar el perfil anterior";
                return false;
            }
            finally
            {
                try { await RefreshVerifiedControlsAsync(); }
                finally { SetSystemChangeBusy(false); }
            }
        }
    }
}
