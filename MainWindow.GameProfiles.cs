using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace WinPulseOptimizer
{
    public partial class MainWindow
    {
        private GameProfile[] _gameProfiles = [];
        private bool _profileTransferInProgress;
        private const string ProfileFileFilter = "Copia de perfiles de WinPulse (*.json)|*.json";

        private void LoadGameProfiles(List<GameProfile>? profiles, Guid? selectedId = null)
        {
            selectedId ??= (CmbGameProfiles.SelectedItem as GameProfile)?.Id;
            _gameProfiles = (profiles ?? new()).Where(profile => profile is
                { Adjustments: not null, Executables.Length: > 0,
                    PowerBase: UsageProfile.GamingMax or UsageProfile.StudyWork or UsageProfile.ChillBattery }).ToArray();
            CmbGameProfiles.ItemsSource = _gameProfiles;
            CmbGameProfiles.SelectedItem = _gameProfiles.FirstOrDefault(profile => profile.Id == selectedId)
                ?? _gameProfiles.FirstOrDefault();
            RenderGameProfileSelection();
        }

        private void CmbGameProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading) RenderGameProfileSelection();
        }

        private void RenderGameProfileSelection()
        {
            var profile = CmbGameProfiles.SelectedItem as GameProfile;
            BtnEditGameProfile.IsEnabled = BtnDeleteGameProfile.IsEnabled = profile != null;
            BtnImportGameProfiles.IsEnabled = !_profileTransferInProgress;
            BtnExportGameProfiles.IsEnabled = !_profileTransferInProgress && _gameProfiles.Length > 0;
            TxtGameProfileExecutables.Text = profile == null ? "Aún no hay perfiles guardados."
                : string.Join(", ", profile.Executables.Select(exe => exe + ".exe"));
        }

        private void BtnNewGameProfile_Click(object sender, RoutedEventArgs e) => EditGameProfile(null);

        private void BtnEditGameProfile_Click(object sender, RoutedEventArgs e)
        {
            if (CmbGameProfiles.SelectedItem is GameProfile profile) EditGameProfile(profile);
        }

        private void EditGameProfile(GameProfile? previous)
        {
            if (_systemChangeInProgress) return;
            var editor = new GameProfileEditor(previous, _gameProfiles) { Owner = this };
            if (editor.ShowDialog() != true || editor.Profile is not GameProfile profile) return;

            var store = HistoryManager.Load();
            store.GameProfiles ??= new();
            int index = store.GameProfiles.FindIndex(item => item.Id == profile.Id);
            if (index < 0) store.GameProfiles.Add(profile);
            else store.GameProfiles[index] = profile;
            if (!SaveGameProfiles(store, $"Guardaste {profile.Name}.", profile.Id)) return;
            if (_activeProfileTag == profile.Tag) RestoreSavedProfileBanner(profile.Tag);
        }

        private void BtnDeleteGameProfile_Click(object sender, RoutedEventArgs e)
        {
            if (_systemChangeInProgress || CmbGameProfiles.SelectedItem is not GameProfile profile) return;
            var store = HistoryManager.Load();
            store.GameProfiles ??= new();
            store.GameProfiles.RemoveAll(item => item.Id == profile.Id);
            SaveGameProfiles(store, $"Eliminaste {profile.Name}.");
        }

        private bool SaveGameProfiles(AuditStore store, string status, Guid? selectedId = null)
        {
            if (!HistoryManager.Save(store))
            {
                TxtGameProfilesStatus.Text = "No se pudieron guardar los perfiles.";
                return false;
            }
            LoadGameProfiles(store.GameProfiles, selectedId);
            ResetAutomationOverride();
            TxtGameProfilesStatus.Text = status;
            return true;
        }

        private async void BtnExportGameProfiles_Click(object sender, RoutedEventArgs e)
        {
            if (_profileTransferInProgress || _gameProfiles.Length == 0) return;
            var dialog = new SaveFileDialog
            {
                Title = "Exportar perfiles por juego", Filter = ProfileFileFilter,
                FileName = "WinPulse-perfiles", DefaultExt = ".json", AddExtension = true
            };
            if (dialog.ShowDialog(this) != true) return;
            string file = dialog.FileName;
            var profiles = _gameProfiles;
            _profileTransferInProgress = true;
            RenderGameProfileSelection();
            TxtGameProfilesStatus.Text = "Exportando perfiles…";
            try
            {
                await Task.Run(() => GameProfileTransfer.Export(file, profiles));
                if (!_forceExit) TxtGameProfilesStatus.Text = $"Perfiles exportados ({profiles.Length}) · {Path.GetFileName(file)}";
            }
            catch (Exception ex)
            {
                if (!_forceExit) TxtGameProfilesStatus.Text = $"No se pudieron exportar los perfiles. {ex.Message}";
            }
            finally
            {
                _profileTransferInProgress = false;
                if (!_forceExit) RenderGameProfileSelection();
            }
        }

        private async void BtnImportGameProfiles_Click(object sender, RoutedEventArgs e)
        {
            if (_profileTransferInProgress) return;
            var dialog = new OpenFileDialog
            {
                Title = "Importar perfiles por juego", Filter = ProfileFileFilter, CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;
            string file = dialog.FileName;
            _profileTransferInProgress = true;
            RenderGameProfileSelection();
            TxtGameProfilesStatus.Text = "Leyendo el archivo de perfiles…";
            try
            {
                var incoming = await Task.Run(() => GameProfileTransfer.Read(file));
                if (_forceExit) return;
                TxtGameProfilesStatus.Text = "";
                var preview = new GameProfileImportWindow(Path.GetFileName(file), _gameProfiles, incoming) { Owner = this };
                if (preview.ShowDialog() != true || _forceExit) return;
                var store = HistoryManager.Load();
                var result = GameProfileTransfer.Preview(store.GameProfiles ?? new(), incoming, preview.ReplaceNames);
                if (!result.Rows.SequenceEqual(preview.Preview.Rows))
                {
                    TxtGameProfilesStatus.Text = "Los perfiles locales cambiaron. Abre el archivo de nuevo para revisar la importación.";
                    return;
                }
                if (result.Added + result.Replaced == 0) return;
                store.GameProfiles = result.Profiles;
                if (SaveGameProfiles(store, $"Importación guardada · {result.Added} nuevos · {result.Replaced} reemplazos"))
                {
                    var active = _gameProfiles.FirstOrDefault(profile => profile.Tag == _activeProfileTag);
                    if (active != null) RestoreSavedProfileBanner(active.Tag);
                }
            }
            catch (JsonException)
            {
                if (!_forceExit) TxtGameProfilesStatus.Text = "El archivo está dañado o le faltan datos de los perfiles.";
            }
            catch (Exception ex)
            {
                if (!_forceExit) TxtGameProfilesStatus.Text = $"No se pudieron importar los perfiles. {ex.Message}";
            }
            finally
            {
                _profileTransferInProgress = false;
                if (!_forceExit) RenderGameProfileSelection();
            }
        }
    }
}
