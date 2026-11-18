using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace WinPulseOptimizer
{
    public sealed record ProfileSettingResult(string Setting, string Before, string Requested,
        string Confirmed, string Status)
    {
        internal const string PendingRestartStatus = "Guardado · requiere reinicio";
        internal const string BlockedPlanStatus = "Bloqueado por directiva del plan (1260)";
    }

    internal sealed class ProfileChangeException(string message, Exception inner, ProfileSettingResult[] settings)
        : InvalidOperationException(message, inner)
    {
        internal ProfileSettingResult[] Settings { get; } = settings;
    }

    public static partial class OptimizerEngine
    {
        private const string UnavailableValue = "No disponible";
        private const string BlockedPlan = ProfileSettingResult.BlockedPlanStatus;
        private static Dictionary<(string Type, string Target), ProfileSettingAttempt>? _profileSettings;

        private sealed class ProfileSettingAttempt(string name, string? before, Func<string?>? read, bool restart)
        {
            internal string Requested { get; set; } = "";
            internal string? Note { get; set; }
            internal bool Written { get; set; }

            internal ProfileSettingResult Result(bool failed)
            {
                string? confirmed = ReadReportValue(read);
                string status = Note ?? (confirmed == null ? "Sin verificar"
                    : failed ? (before == null ? "Sin referencia anterior" : confirmed != before ? "Valor distinto al anterior"
                        : Written ? "Revertido" : "Sin cambio tras el fallo")
                    : confirmed != Requested ? "Valor solicitado no confirmado"
                    : before == confirmed ? "Ya configurado"
                    : restart ? ProfileSettingResult.PendingRestartStatus : "Aplicado");
                return new(name, before ?? UnavailableValue, Requested, confirmed ?? UnavailableValue, status);
            }
        }

        private static string? ReadReportValue(Func<string?>? read)
        {
            try { return read?.Invoke(); }
            catch (Exception) { return null; }
        }

        private static ProfileSettingAttempt? TrackProfileSetting(string type, string target, string name,
            string? before, string requested, Func<string?>? read, string? note = null, bool restart = false)
        {
            if (_profileSettings == null) return null;
            var key = (type, target);
            if (!_profileSettings.TryGetValue(key, out var row))
                _profileSettings.Add(key, row = new(name, before, read, restart));
            row.Requested = requested;
            row.Note = note;
            return row;
        }

        private static ProfileSettingResult[] ReadProfileSettings(ChangeStore? store = null, bool failed = false)
        {
            if (_profileSettings == null) return [];
            var results = new List<ProfileSettingResult>(_profileSettings.Count);
            foreach (var (key, attempt) in _profileSettings)
            {
                var row = attempt.Result(failed);
                if (store != null && row.Status == ProfileSettingResult.PendingRestartStatus &&
                    !HasPendingRestart(store, [new SavedSetting(key.Type, key.Target, null)]))
                    row = row with { Status = "Guardado" };
                if (store != null && (!failed || IsBlocked(attempt.Note)))
                    RecordTrackedSetting(store, new(key.Type, key.Target, row.Setting, row.Requested, attempt.Note));
                results.Add(row);
            }
            return results.ToArray();
        }

        private static string? FormatReportPlan(Guid plan)
        {
            string? name = ReadPlanName(plan);
            return name == null ? plan.ToString() : $"{name} ({plan})";
        }

        private static string FormatReportMode(Guid mode) => mode.ToString() switch
        {
            "ded574b5-45a0-4f42-8737-46345c09c238" => "Mejor rendimiento",
            "961cc777-2547-4f9d-8174-7d86181b8a7a" => "Mejor eficiencia energética",
            "00000000-0000-0000-0000-000000000000" => "Equilibrado",
            _ => mode.ToString()
        };

        private static string FormatReportPower(string setting, uint value) => setting switch
        {
            ProcessorBoostSetting => FmtBoostMode(value),
            "PROCTHROTTLEMIN" or "PROCTHROTTLEMAX" or "CPMINCORES" => FmtPercent(value),
            "DISKIDLE" or "STANDBYIDLE" => value == 0 ? "Nunca" : value % 60 == 0 ? $"{value / 60} min" : $"{value} s",
            "PCIE" => value <= 2 ? FmtPcie(value) : value.ToString(CultureInfo.InvariantCulture),
            "USB" => value switch { 0 => "Desactivado", 1 => "Activado", _ => value.ToString(CultureInfo.InvariantCulture) },
            _ => value.ToString(CultureInfo.InvariantCulture)
        };

        private static string FormatReportRegistry(string? serialized)
        {
            if (serialized == null) return "Sin valor";
            var data = JsonSerializer.Deserialize<RegistryValue>(serialized)!.Data;
            return data.ValueKind == JsonValueKind.String && data.GetString()?.Length == 0
                ? "Cadena vacía" : data.ToString();
        }

        private static string ReportRegistryName(string name) => name switch
        {
            "AutoGameModeEnabled" => "Modo Juego de Windows",
            "HwSchMode" => "Aceleración de GPU (HAGS)",
            "DirectXUserGlobalSettings" => "Optimizaciones de juegos en ventana",
            "GlobalTimerResolutionRequests" => "Temporizador de alta precisión",
            "Win32PrioritySeparation" => "Prioridad de la ventana activa",
            "GameDVR_Enabled" => "Game DVR (usuario)",
            "AppCaptureEnabled" => "Captura de juegos (usuario)",
            "AllowGameDVR" => "Directiva de Game DVR",
            "SystemResponsiveness" => "MMCSS · reserva para segundo plano",
            "NetworkThrottlingIndex" => "MMCSS · límite de red",
            "GPU Priority" => "MMCSS · prioridad de GPU",
            "Priority" => "MMCSS · prioridad de juegos",
            "Scheduling Category" => "MMCSS · categoría de juegos",
            "GlobalUserDisabled" => "Restricción de apps en segundo plano",
            "MenuShowDelay" => "Retardo de menús (ms)",
            "MinAnimate" => "Animación al abrir o minimizar ventanas",
            "MouseSpeed" => "Aceleración del ratón (MouseSpeed)",
            "MouseThreshold1" => "Umbral 1 de aceleración del ratón",
            "MouseThreshold2" => "Umbral 2 de aceleración del ratón",
            "EnableTransparency" => "Transparencias de Windows",
            _ => name
        };

        private static string ReportInterfaceName(string id)
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(
                    adapter => string.Equals(adapter.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;
            }
            catch (NetworkInformationException) { return id; }
        }

        private static string ReportPowerRestriction(uint status) => status switch
        {
            1260 => "Bloqueado por directiva (1260)",
            5 => "Acceso denegado (5)",
            _ => $"Windows devolvió el código {status}"
        };

        private static string FormatReportService(string state) => state switch
        {
            "Running" => "En ejecución",
            "Stopped" => "Detenido",
            _ => state
        };

        private static ProfileSettingAttempt? TrackProfileService(string service, string? before, string desired, string? note = null) =>
            TrackProfileSetting("service", service, $"Servicio {service}", before == null ? null : FormatReportService(before),
                FormatReportService(desired), () => ReadService(service)?.State is { } state ? FormatReportService(state) : null, note);
    }
}
