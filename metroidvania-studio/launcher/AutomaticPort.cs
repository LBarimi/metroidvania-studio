using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MetroidvaniaStudio.Launcher;

internal static partial class Program
{
    private sealed record PortAssignment(string ProjectPath, int Port);

    private static string AssignmentPath(Options options)
    {
        string project = OperatingSystem.IsWindows() ? options.Project.ToUpperInvariant() : options.Project;
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(project))).ToLowerInvariant();
        return Path.Combine(options.Local, $"workspace-{key}.json");
    }

    private static async Task<(Options Options, FileStream? Lock)> AcquireSessionLock(Options options)
    {
        if (!options.AutoPort) return (options, await Lock(options));
        string assignmentPath = AssignmentPath(options);
        // Serialize port selection for this workspace before taking its per-port lock.
        using var workspaceLock = await LockFile(assignmentPath + ".lock");
        if (File.Exists(assignmentPath))
        {
            var assignment = JsonSerializer.Deserialize<PortAssignment>(File.ReadAllText(assignmentPath), Json);
            if (assignment == null || !SamePath(assignment.ProjectPath, options.Project) || assignment.Port is < 1024 or > 65535)
                throw new InvalidOperationException("Invalid saved workspace port. No running process was changed.");
            options = options with { Port = assignment.Port };
        }
        for (int attempt = 0; attempt < 16; attempt++)
        {
            var portLock = await Lock(options);
            bool retained = false;
            try
            {
                Session? record = ReadSession(options);
                bool owned = record != null && SamePath(record.ProjectPath, options.Project);
                if (options.Action == "stop" && !owned) return (options, null);
                // Existing records still pass every health, nonce and process identity check
                // in Main/Stop. An unresponsive owned session must never spawn a second writer.
                if (owned || record == null && CanBind(options.Port))
                {
                    SaveAssignment(assignmentPath, new PortAssignment(options.Project, options.Port));
                    retained = true;
                    return (options, portLock);
                }
            }
            finally { if (!retained) portLock.Dispose(); }
            options = options with { Port = AvailablePort() };
        }
        throw new InvalidOperationException("Could not find an available local port. No running process was changed.");
    }

    private static bool CanBind(int port)
    {
        using var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        try { listener.Start(); return true; }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied) { return false; }
    }

    private static int AvailablePort()
    {
        // Let the OS select an unused loopback port; never bind to external interfaces.
        using var listener = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void SaveAssignment(string path, PortAssignment assignment)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(assignment, Json)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
