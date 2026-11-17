using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace WinPulseOptimizer
{
    internal enum InstanceRequest : byte { Activate = 1, PrepareUpgrade = 2 }
    internal enum InstanceReply : byte { Unavailable, Accepted, Busy }

    internal sealed class InstanceCoordinator : IDisposable
    {
        private static readonly string InstanceName = GetInstanceName();
        private readonly Mutex _mutex;
        private readonly CancellationTokenSource _stop = new();

        private InstanceCoordinator(Mutex mutex) => _mutex = mutex;

        private static string GetInstanceName()
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            return $"WinPulseOptimizer.{identity.User?.Value}.{process.SessionId}";
        }

        internal static InstanceCoordinator? TryAcquire()
        {
            var mutex = new Mutex(false, $@"Local\{InstanceName}");
            try
            {
                if (mutex.WaitOne(TimeSpan.Zero)) return new InstanceCoordinator(mutex);
            }
            catch (AbandonedMutexException) { return new InstanceCoordinator(mutex); }
            mutex.Dispose();
            return null;
        }

        public void Listen(Func<InstanceRequest, Task<InstanceReply>> handler, Action upgradeAcknowledged)
        {
            CancellationToken token = _stop.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = new NamedPipeServerStream(InstanceName, PipeDirection.InOut, 1,
                            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await pipe.WaitForConnectionAsync(token);
                        using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
                        connection.CancelAfter(TimeSpan.FromSeconds(30));
                        var pid = new byte[4];
                        BinaryPrimitives.WriteInt32LittleEndian(pid, Environment.ProcessId);
                        await pipe.WriteAsync(pid, connection.Token);
                        var input = new byte[1];
                        await pipe.ReadExactlyAsync(input, connection.Token);
                        var request = (InstanceRequest)input[0];
                        if (request is not (InstanceRequest.Activate or InstanceRequest.PrepareUpgrade)) continue;
                        var reply = await handler(request);
                        try { await pipe.WriteAsync(new[] { (byte)reply }, connection.Token); }
                        finally
                        {
                            if (request == InstanceRequest.PrepareUpgrade && reply == InstanceReply.Accepted)
                                upgradeAcknowledged();
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (IOException) when (!token.IsCancellationRequested)
                    {
                        await Task.Delay(100);
                    }
                }
            });
        }

        internal static async Task<InstanceReply> SendAsync(InstanceRequest request)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var pipe = new NamedPipeClientStream(".", InstanceName, PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(1500, timeout.Token);
                var pid = new byte[4];
                await pipe.ReadExactlyAsync(pid, timeout.Token);
                if (request == InstanceRequest.Activate)
                    AllowSetForegroundWindow(BinaryPrimitives.ReadInt32LittleEndian(pid));
                await pipe.WriteAsync(new[] { (byte)request }, timeout.Token);
                var reply = new byte[1];
                await pipe.ReadExactlyAsync(reply, timeout.Token);
                return (InstanceReply)reply[0];
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
            {
                return InstanceReply.Unavailable;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}
