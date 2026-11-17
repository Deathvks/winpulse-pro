using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WinPulseOptimizer
{
    internal sealed record PingMeasurement(string Target, string Host, IPAddress? Address,
        int Sent, int Received, double? AverageMs, double? VariationMs, string? Error, long? P95Ms = null)
    {
        public double? MissingPercent => Sent == 0 ? null : (Sent - Received) * 100.0 / Sent;
        public bool IsSameDestination(PingMeasurement other) =>
            Address != null && Address.Equals(other.Address) && Target == other.Target &&
            string.Equals(Host, other.Host, StringComparison.OrdinalIgnoreCase);
    }

    internal static partial class NetworkDiagnostics
    {
        private sealed record PingTarget(string Name, string Host);
        private const int ProbeCount = 10;

        public static bool IsValidTarget(string value) => value.Length == 0 ||
            IPAddress.TryParse(value, out _) || Uri.CheckHostName(value) == UriHostNameType.Dns;

        public static async Task<PingMeasurement[]> MeasureAsync(string customHost)
        {
            var targets = GetGateways().ToList();
            targets.Add(new("Cloudflare", "1.1.1.1"));
            targets.Add(new("Google", "8.8.8.8"));
            if (customHost.Length > 0) targets.Add(new("Destino personalizado", customHost));
            return await Task.WhenAll(targets.Select(MeasureTargetAsync));
        }

        private static IEnumerable<PingTarget> GetGateways()
        {
            var addresses = new HashSet<IPAddress>();
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                GatewayIPAddressInformationCollection gateways;
                try { gateways = adapter.GetIPProperties().GatewayAddresses; }
                catch (NetworkInformationException) { continue; }
                foreach (var gateway in gateways)
                {
                    IPAddress address = gateway.Address;
                    if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
                        address.Equals(IPAddress.None) || IPAddress.IsLoopback(address) || !addresses.Add(address))
                        continue;
                    yield return new($"Puerta de enlace · {adapter.Name}", address.ToString());
                }
            }
        }

        private static async Task<PingMeasurement> MeasureTargetAsync(PingTarget target)
        {
            if (!IPAddress.TryParse(target.Host, out var address))
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    address = (await Dns.GetHostAddressesAsync(target.Host, timeout.Token)).FirstOrDefault();
                }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
                {
                    return Unresolved(target);
                }
                if (address == null) return Unresolved(target);
            }

            var samples = new long?[ProbeCount];
            using var ping = new Ping();
            for (int i = 0; i < samples.Length; i++)
            {
                try
                {
                    var reply = await ping.SendPingAsync(address, 1000);
                    if (reply.Status == IPStatus.Success) samples[i] = reply.RoundtripTime;
                }
                catch (PingException) { }
                if (i + 1 < samples.Length) await Task.Delay(120);
            }
            return Summarize(target.Name, target.Host, address, samples);
        }

        private static PingMeasurement Unresolved(PingTarget target) =>
            new(target.Name, target.Host, null, 0, 0, null, null, "No se pudo resolver el nombre.");

        internal static PingMeasurement Summarize(string target, string host, IPAddress address, IReadOnlyList<long?> samples)
        {
            var replies = new List<long>();
            var differences = new List<double>();
            long? previous = null;
            foreach (long? sample in samples)
            {
                if (sample.HasValue)
                {
                    replies.Add(sample.Value);
                    if (previous.HasValue) differences.Add(Math.Abs(sample.Value - previous.Value));
                }
                previous = sample;
            }

            replies.Sort();
            long? p95 = replies.Count == 0 ? null : replies[(int)Math.Ceiling(replies.Count * 0.95) - 1];
            return new(target, host, address, samples.Count, replies.Count,
                replies.Count == 0 ? null : replies.Average(),
                differences.Count == 0 ? null : differences.Average(),
                replies.Count == 0 ? "Sin respuesta ICMP." : null, p95);
        }
    }
}
