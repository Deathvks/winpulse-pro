using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WinPulseOptimizer
{
    internal enum PingLoadPhase { Idle, Download, Upload }

    internal sealed record LoadedPingPhase(PingLoadPhase Phase, PingMeasurement[] Results, long Bytes, string? Notice);
    internal sealed record LoadedPingRun(string Adapter, LoadedPingPhase[] Phases);

    internal static partial class NetworkDiagnostics
    {
        private const string LoadServer = "speed.cloudflare.com";
        private const int ParallelTransfers = 4;
        private const int BytesPerTransfer = 25_000_000;
        private static readonly TimeSpan SampleDuration = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(750);
        private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan RecentTraffic = TimeSpan.FromMilliseconds(400);
        private sealed record LoadRoute(IPAddress Remote, IPAddress LoadServer, IPAddress Local);
        private sealed record ProbeDestination(string Name, string Host, IPAddress Address);

        internal static async Task<LoadedPingRun> MeasureLoadedAsync(string host, IProgress<PingLoadPhase> progress,
            CancellationToken token)
        {
            var route = await ResolveLoadRouteAsync(host, token);
            var adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(nic =>
                nic.OperationalStatus == OperationalStatus.Up &&
                nic.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(route.Local)))
                ?? throw new IOException("Windows no identificó el adaptador usado para la prueba.");
            var gateway = adapter.GetIPProperties().GatewayAddresses
                .Select(item => item.Address)
                .FirstOrDefault(address => address.AddressFamily == route.Local.AddressFamily &&
                    !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(address));

            var destinations = new List<ProbeDestination>();
            if (gateway != null) destinations.Add(new("Router", gateway.ToString(), gateway));
            destinations.Add(new("Destino externo", host.Length == 0 ? "Cloudflare" : host, route.Remote));

            using var client = CreateLoadClient(route);
            var results = new List<LoadedPingPhase>();
            foreach (var phase in new[] { PingLoadPhase.Idle, PingLoadPhase.Download, PingLoadPhase.Upload })
            {
                token.ThrowIfCancellationRequested();
                progress.Report(phase);
                if (phase == PingLoadPhase.Idle)
                {
                    using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
                    window.CancelAfter(SampleDuration);
                    results.Add(new(phase, await ProbeDestinationsAsync(destinations, window.Token), 0, null));
                }
                else
                {
                    results.Add(await MeasureLoadedPhaseAsync(client, phase, destinations, token));
                }
                token.ThrowIfCancellationRequested();
                if (!Equals(FindLocalAddress(route.Remote), route.Local) || !Equals(FindLocalAddress(route.LoadServer), route.Local))
                    throw new IOException("La ruta de red cambió durante la prueba. Vuelve a medir.");
                if (phase != PingLoadPhase.Upload) await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            return new(adapter.Name, results.ToArray());
        }

        private static async Task<LoadRoute> ResolveLoadRouteAsync(string host, CancellationToken token)
        {
            using var lookup = CancellationTokenSource.CreateLinkedTokenSource(token);
            lookup.CancelAfter(TimeSpan.FromSeconds(6));
            try
            {
                var external = host.Length == 0
                    ? new[] { IPAddress.Parse("1.1.1.1"), IPAddress.Parse("2606:4700:4700::1111") }
                    : await Dns.GetHostAddressesAsync(host, lookup.Token);
                var load = await Dns.GetHostAddressesAsync(LoadServer, lookup.Token);
                foreach (var address in external.Where(address => !IPAddress.IsLoopback(address)))
                {
                    if (FindLocalAddress(address) is not { } local) continue;
                    var server = load.FirstOrDefault(candidate => candidate.AddressFamily == address.AddressFamily &&
                        Equals(FindLocalAddress(candidate), local));
                    if (server != null) return new(address, server, local);
                }
                throw new IOException("El destino de ping y el servidor de carga necesitan una ruta por el mismo adaptador.");
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new IOException("La resolución de los destinos tardó demasiado.");
            }
        }

        private static IPAddress? FindLocalAddress(IPAddress remote)
        {
            try
            {
                using var socket = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(new IPEndPoint(remote, 443));
                return (socket.LocalEndPoint as IPEndPoint)?.Address;
            }
            catch (SocketException) { return null; }
        }

        private static HttpClient CreateLoadClient(LoadRoute route) => new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(route.Local.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    socket.Bind(new IPEndPoint(route.Local, 0));
                    await socket.ConnectAsync(new IPEndPoint(route.LoadServer, context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        }) { BaseAddress = new Uri($"https://{LoadServer}/"), Timeout = Timeout.InfiniteTimeSpan };

        private static async Task<LoadedPingPhase> MeasureLoadedPhaseAsync(HttpClient client, PingLoadPhase phase,
            List<ProbeDestination> destinations, CancellationToken token)
        {
            using var transferWindow = CancellationTokenSource.CreateLinkedTokenSource(token);
            var dataStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dataEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int activeFlows = ParallelTransfers;
            long bytes = 0;
            long lastTraffic = 0;
            void CountBytes(int count)
            {
                Interlocked.Exchange(ref lastTraffic, Stopwatch.GetTimestamp());
                if (Interlocked.Add(ref bytes, count) >= 65_536) dataStarted.TrySetResult();
            }

            var transfers = Task.WhenAll(Enumerable.Range(0, ParallelTransfers).Select(_ => TransferAsync()));
            async Task<string?> TransferAsync()
            {
                int ended = 0;
                void EndFlow()
                {
                    if (Interlocked.Exchange(ref ended, 1) == 0 && Interlocked.Decrement(ref activeFlows) == 0)
                        dataEnded.TrySetResult();
                }

                try
                {
                    if (phase == PingLoadPhase.Download)
                        await DownloadAsync(client, CountBytes, transferWindow.Token);
                    else
                    {
                        using var upload = new TestUploadContent(CountBytes, EndFlow);
                        using var request = new HttpRequestMessage(HttpMethod.Post, $"__up?bytes={BytesPerTransfer}") { Content = upload };
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, transferWindow.Token);
                        response.EnsureSuccessStatusCode();
                    }
                    return null;
                }
                catch (OperationCanceledException) when (transferWindow.IsCancellationRequested) { return null; }
                catch (Exception ex) when (ex is IOException or HttpRequestException) { return ex.Message; }
                finally { EndFlow(); }
            }

            PingMeasurement[] samples = [];
            string? notice = null;
            string?[] errors;
            try
            {
                await Task.WhenAny(dataStarted.Task, dataEnded.Task).WaitAsync(TimeSpan.FromSeconds(6), token);
                if (dataStarted.Task.IsCompleted && !dataEnded.Task.IsCompleted)
                {
                    using var probes = CancellationTokenSource.CreateLinkedTokenSource(token);
                    probes.CancelAfter(SampleDuration);
                    var measuring = ProbeDestinationsAsync(destinations, probes.Token,
                        () => Stopwatch.GetElapsedTime(Interlocked.Read(ref lastTraffic)) <= RecentTraffic);
                    await Task.WhenAny(measuring, dataEnded.Task);
                    probes.Cancel();
                    samples = await measuring;
                }
                else notice = "La transferencia terminó antes de poder medir bajo carga.";

                // Give finished uploads a short interval to receive the HTTP status.
                if (phase == PingLoadPhase.Upload && dataEnded.Task.IsCompleted)
                    await Task.WhenAny(transfers, Task.Delay(TimeSpan.FromSeconds(1), token));
            }
            catch (TimeoutException) { notice = "El servidor tardó demasiado en empezar a transferir datos."; }
            finally
            {
                transferWindow.Cancel();
                errors = await transfers;
            }
            token.ThrowIfCancellationRequested();

            int failed = errors.Count(error => error != null);
            if (failed == ParallelTransfers)
                return new(phase, [], bytes, $"No se pudo generar carga. {errors.First(error => error != null)}");
            if (failed > 0) notice = $"{notice} Fallaron {failed} de {ParallelTransfers} conexiones de prueba.".Trim();
            return new(phase, samples, bytes, notice);
        }

        private static async Task DownloadAsync(HttpClient client, Action<int> countBytes, CancellationToken token)
        {
            using var response = await client.GetAsync($"__down?bytes={BytesPerTransfer}", HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[65_536];
            int remaining = BytesPerTransfer;
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), token);
                if (read == 0) throw new IOException("La descarga de prueba terminó antes del tamaño solicitado.");
                remaining -= read;
                countBytes(read);
            }
        }

        private sealed class TestUploadContent(Action<int> countBytes, Action finished) : HttpContent
        {
            private static readonly byte[] Payload = new byte[65_536];

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
                SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
            {
                try
                {
                    for (int remaining = BytesPerTransfer; remaining > 0;)
                    {
                        int bytes = Math.Min(remaining, Payload.Length);
                        await stream.WriteAsync(Payload.AsMemory(0, bytes), token);
                        remaining -= bytes;
                        countBytes(bytes);
                    }
                }
                finally { finished(); }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = BytesPerTransfer;
                return true;
            }
        }

        private static Task<PingMeasurement[]> ProbeDestinationsAsync(List<ProbeDestination> destinations, CancellationToken token,
            Func<bool>? hasRecentTraffic = null) =>
            Task.WhenAll(destinations.Select(destination => ProbeDestinationAsync(destination, token, hasRecentTraffic)));

        private static async Task<PingMeasurement> ProbeDestinationAsync(ProbeDestination destination, CancellationToken token,
            Func<bool>? hasRecentTraffic)
        {
            var samples = new List<long?>();
            using var ping = new Ping();
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (hasRecentTraffic?.Invoke() != false)
                    {
                        var reply = await ping.SendPingAsync(destination.Address, ProbeTimeout, cancellationToken: token);
                        if (token.IsCancellationRequested) break;
                        samples.Add(reply.Status == IPStatus.Success ? reply.RoundtripTime : null);
                    }
                }
                catch (PingException) when (!token.IsCancellationRequested) { samples.Add(null); }
                catch (Exception ex) when (token.IsCancellationRequested && ex is OperationCanceledException or PingException) { break; }
                try { await Task.Delay(ProbeInterval, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            }
            return Summarize(destination.Name, destination.Host, destination.Address, samples);
        }
    }
}
