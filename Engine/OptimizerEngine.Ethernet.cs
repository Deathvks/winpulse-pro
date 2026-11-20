using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WinPulseOptimizer
{
    internal enum EthernetOption { InterruptModeration, EnergyEfficientEthernet }

    internal sealed record EthernetAdapter(Guid Id, string Name, bool Connected, string? Moderation, string? Eee)
    {
        public string Label => Connected ? $"{Name} · conectado" : Name;

        public bool? GetOption(EthernetOption option) => (option switch
        {
            EthernetOption.InterruptModeration => Moderation,
            EthernetOption.EnergyEfficientEthernet => Eee,
            _ => null
        }) switch
        {
            "0" => false,
            "1" => true,
            _ => null
        };
    }

    public static partial class OptimizerEngine
    {
        private const string EthernetAdapterQueries = """
            $wiredIds = @([System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
                Where-Object { [string]$_.NetworkInterfaceType -in @('Ethernet','Ethernet3Megabit','FastEthernetT','FastEthernetFx','GigabitEthernet') } |
                ForEach-Object { [guid]$_.Id })
            $adapters = @()
            if ($wiredIds.Count -gt 0) {
                $adapters = @(Get-NetAdapter -Physical -IncludeHidden |
                    Where-Object { $_.InterfaceGuid -and ([guid]$_.InterfaceGuid -in $wiredIds) })
            }
            function Get-WiredProperties($adapter) {
                $name = [System.Management.Automation.WildcardPattern]::Escape($adapter.Name)
                Get-NetAdapterAdvancedProperty -Name $name -AllProperties -IncludeHidden
            }
            function Get-WiredValue($items, [string]$keyword) {
                $options = @($items | Where-Object { $_.RegistryKeyword -eq $keyword })
                if ($options.Count -ne 1) { return $null }
                $values = @($options[0].RegistryValue)
                if ($values.Count -eq 1 -and [string]$values[0] -in @('0','1')) { return [string]$values[0] }
                return $null
            }
            """;

        internal static EthernetAdapter[] GetEthernetAdapters()
        {
            string output = RunPowerShell("$WarningPreference = 'SilentlyContinue'; " + EthernetAdapterQueries + "\n" + """
                $rows = @(foreach ($adapter in $adapters) {
                    $properties = @(Get-WiredProperties $adapter)
                    [pscustomobject]@{
                        Id = ([guid]$adapter.InterfaceGuid).ToString('D')
                        Name = $adapter.Name
                        Connected = $adapter.Status -eq 'Up'
                        Moderation = Get-WiredValue $properties '*InterruptModeration'
                        Eee = Get-WiredValue $properties '*EEE'
                    }
                })
                $json = ConvertTo-Json -InputObject $rows -Compress
                [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
                """);
            var adapters = JsonSerializer.Deserialize<EthernetAdapter[]>(Convert.FromBase64String(output.Trim()))
                ?? Array.Empty<EthernetAdapter>();
            return adapters.OrderByDescending(adapter => adapter.Connected)
                .ThenBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        internal static void SetEthernetOption(Guid adapterId, EthernetOption option, bool enabled)
        {
            string keyword = option switch
            {
                EthernetOption.InterruptModeration => "*InterruptModeration",
                EthernetOption.EnergyEfficientEthernet => "*EEE",
                _ => throw new InvalidDataException("El ajuste de Ethernet no es compatible.")
            };

            var adapter = GetEthernetAdapters().FirstOrDefault(row => row.Id == adapterId);
            bool? previous = adapter?.GetOption(option);
            if (!previous.HasValue)
                throw new IOException("El controlador ya no ofrece este ajuste en el adaptador seleccionado.");
            string label = option == EthernetOption.InterruptModeration ? "Moderación de interrupciones" : "Ethernet de bajo consumo";
            TrackProfileSetting("ethernet", $"{adapterId:D}|{keyword}", $"{label} · {adapter!.Name}",
                previous.Value ? "Activado" : "Desactivado", enabled ? "Activado" : "Desactivado", null);
            if (previous == enabled) return;

            string oldValue = previous.Value ? "1" : "0";
            string newValue = enabled ? "1" : "0";
            int recordedBefore = RecordedSettingCount;
            Remember("ethernet", $"{adapterId:D}|{keyword}", oldValue);
            string output = RunPowerShell(EthernetAdapterQueries + "\n" + $$"""
                $selected = @($adapters | Where-Object { [guid]$_.InterfaceGuid -eq [guid]'{{adapterId:D}}' })
                if ($selected.Count -ne 1) { 'Conflict'; exit 0 }
                $adapter = $selected[0]
                $properties = @(Get-WiredProperties $adapter)
                if ((Get-WiredValue $properties '{{keyword}}') -ne '{{oldValue}}') { 'Conflict'; exit 0 }
                $property = $properties | Where-Object { $_.RegistryKeyword -eq '{{keyword}}' }
                Set-NetAdapterAdvancedProperty -InputObject $property -RegistryValue '{{newValue}}' -Confirm:$false
                $after = @(Get-WiredProperties $adapter)
                if ((Get-WiredValue $after '{{keyword}}') -ne '{{newValue}}') {
                    throw 'Windows no confirmó el valor solicitado para Ethernet.'
                }
                'Verified'
                """);
            string? result = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();
            if (result == "Conflict")
            {
                DiscardUnchangedRecords(recordedBefore);
                throw new IOException("El adaptador o su configuración cambió durante la operación. Vuelve a detectarlo.");
            }
            if (result != "Verified")
                throw new IOException("No se pudo verificar el cambio de Ethernet.");
        }

        private static void RestoreEthernetOption(SavedSetting saved)
        {
            var parts = saved.Target.Split('|');
            if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "D", out Guid adapterId) ||
                saved.Value is not ("0" or "1"))
                throw new InvalidDataException("El valor de Ethernet guardado no es válido.");
            var option = parts[1] switch
            {
                "*InterruptModeration" => EthernetOption.InterruptModeration,
                "*EEE" => EthernetOption.EnergyEfficientEthernet,
                _ => throw new InvalidDataException("El ajuste de Ethernet guardado no es compatible.")
            };
            SetEthernetOption(adapterId, option, saved.Value == "1");
        }
    }
}
