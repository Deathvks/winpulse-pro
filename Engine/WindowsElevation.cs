using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace WinPulseOptimizer;

internal static class WindowsElevation
{
    internal const string WorkerFlag = "--system-change";
    private const string PipePrefix = "WinPulseOptimizer.Operation.";
    private const int MaxReply = 2 * 1024 * 1024;
    private const byte Ready = 1;

    internal static bool IsAdministrator { get; } = ReadAdministrator();

    private static bool ReadAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static Process StartElevated(string executable, string arguments)
    {
        try
        {
            return Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.SystemDirectory
            }) ?? throw new IOException("Windows no inició la operación.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Cancelaste el permiso de administrador.");
        }
    }

    internal static async Task<SystemResult> RunAsync(SystemRequest request)
    {
        if (IsAdministrator || !request.RequiresAdministrator)
            return await Task.Run(() => SystemOperations.ExecuteAsync(request));
        return await Task.Run(() => RunElevatedAsync(request));
    }

    private static async Task<SystemResult> RunElevatedAsync(SystemRequest request)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("No se pudo identificar la sesión de Windows.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
        string id = Guid.NewGuid().ToString("N");
        using var pipe = NamedPipeServerStreamAcl.Create(PipePrefix + id, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);

        string executable = Environment.ProcessPath ?? throw new IOException("No se encontró el ejecutable de WinPulse.");
        string requestText = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request));
        using var worker = StartElevated(executable, $"{WorkerFlag} {id} {Environment.ProcessId} {user.Value} {requestText}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var exit = worker.WaitForExitAsync(deadline.Token);
        while (true)
        {
            var connection = pipe.WaitForConnectionAsync(deadline.Token);
            await Task.WhenAny(connection, exit);
            if (!connection.IsCompleted)
            {
                await exit;
                throw new IOException("No se pudo abrir el componente con privilegios para esta misma cuenta de Windows.");
            }
            await connection;
            if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) && pid == worker.Id) break;
            pipe.Disconnect();
        }

        await pipe.WriteAsync(new[] { Ready }, deadline.Token);
        var header = new byte[4];
        await pipe.ReadExactlyAsync(header, deadline.Token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxReply) throw new IOException("WinPulse recibió una respuesta de administrador inválida.");
        var payload = new byte[length];
        await pipe.ReadExactlyAsync(payload, deadline.Token);
        var result = JsonSerializer.Deserialize<SystemResult>(payload) ?? throw new IOException("No se recibió el resultado de Windows.");
        if (result.Error != null)
            throw new ProfileChangeException(result.Error, new InvalidOperationException(result.Error), result.Settings ?? []);
        return result;
    }

    internal static async Task<int> RunWorkerAsync(string[] args)
    {
        if (args is not [WorkerFlag, {} id, {} parent, {} sid, {} payload] || !IsAdministrator ||
            !Guid.TryParseExact(id, "N", out _) || !uint.TryParse(parent, out uint parentId) ||
            payload.Length > 16000)
            return 1;
#if WINPULSE_INSTALLER
        if (Environment.ProcessPath is not { } source || !WinPulseInstallation.IsInstalledExecutable(source)) return 1;
#endif
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User?.Value != sid) return 1;
        using var pipe = new NamedPipeClientStream(".", PipePrefix + id, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await pipe.ConnectAsync(deadline.Token);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) || pid != parentId) return 1;
        byte[] ready = new byte[1];
        await pipe.ReadExactlyAsync(ready, deadline.Token);
        if (ready[0] != Ready) return 1;

        SystemResult result;
        try
        {
            var request = JsonSerializer.Deserialize<SystemRequest>(Convert.FromBase64String(payload));
            if (request?.RequiresAdministrator != true) return 1;
            result = await SystemOperations.ExecuteAsync(request);
        }
        catch (Exception ex) { result = new(Error: ex.Message, Settings: (ex as ProfileChangeException)?.Settings); }

        var response = JsonSerializer.SerializeToUtf8Bytes(result);
        if (response.Length > MaxReply)
            response = JsonSerializer.SerializeToUtf8Bytes(result with { Settings = null });
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, response.Length);
        await pipe.WriteAsync(header);
        await pipe.WriteAsync(response);
        return result.Error == null ? 0 : 1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
