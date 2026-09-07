using System.Diagnostics;
using MetroidvaniaStudio.Storage;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MetroidvaniaStudio.Launcher;

internal static partial class Program
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly StringComparison Paths = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private sealed record Health(string InstanceId, string ProjectPath, string? LaunchToken, int ProcessId, string? RuntimePath);
    private sealed record Session(int Pid, DateTime ProcessStartUtc, int Port, string InstanceId, string ProjectPath, string DllPath, string? LaunchToken);
    private sealed record BuildIndex(int FormatVersion, string Folder);
    private sealed record Options(string Action, string Root, string Project, int Port, bool NoBrowser, bool Restart, bool Foreground, string? BuildDirectory, bool AutoPort)
    {
        public string? LegacyWorkspace { get; init; }
        public string Local => Path.Combine(Root, "metroidvania-studio", ".local");
        public string Record => Path.Combine(Local, $"server-{Port}.json");
        public string Url => $"http://127.0.0.1:{Port}/";
    }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            Options options = Parse(args);
            if (options.Action == "check") { Console.WriteLine("Build ready: " + ResolveBuild(options)); return 0; }
            Directory.CreateDirectory(options.Local);
            // Validate the build before changing a port assignment or running session.
            string build = options.Action == "run" ? ResolveBuild(options) : "";
            if (options.LegacyWorkspace != null)
            {
                // Only the verified legacy session owned by this launcher may be stopped.
                if (Directory.Exists(options.LegacyWorkspace))
                {
                    using (await Lock(options))
                    {
                        var old = options with { Project = options.LegacyWorkspace };
                        var record = ReadSession(old);
                        if (record != null && SamePath(record.ProjectPath, old.Project)) await Stop(old);
                    }
                }
                if (options.Action == "run") PortableWorkspace.Prepare(options.Project, options.LegacyWorkspace);
            }
            var session = await AcquireSessionLock(options);
            options = session.Options;
            if (session.Lock == null) { Console.WriteLine("No matching background session is running."); return 0; }
            Process? foreground = null;
            using (session.Lock)
            {
                if (options.Action == "stop") { await Stop(options); return 0; }
                string dll = Path.Combine(build, "metroidvania-studio/server/MetroidvaniaStudio.Server.dll");
                Health? active = await ReadHealth(options);
                if (active != null)
                {
                    AssertWorkspace(options, active);
                    Session? record = ReadSession(options);
                    record = VerifyOwner(options, record, active);
                    SaveSession(options, record);
                    if (!options.Restart && SamePath(record!.DllPath, dll))
                    {
                        Console.WriteLine("Metroidvania Studio is already running: " + options.Url);
                        OpenBrowser(options); return 0;
                    }
                    await Stop(options);
                }
                else if (File.Exists(options.Record)) await Stop(options);
                string token = Guid.NewGuid().ToString("N");
                Process process = await Spawn(options, build, dll, token);
                SaveSession(options, new Session(process.Id, process.StartTime.ToUniversalTime(), options.Port, "", options.Project, dll, token));
                var deadline = DateTime.UtcNow.AddSeconds(20);
                Health? ready = null;
                while (DateTime.UtcNow < deadline)
                {
                    if (process.HasExited) throw new InvalidOperationException("Server exited during startup. See " + Path.Combine(options.Local, $"server-{options.Port}-error.log"));
                    ready = await ReadHealth(options);
                    if (ready != null)
                    {
                        AssertWorkspace(options, ready);
                        if (ready.LaunchToken != token || ready.ProcessId != process.Id || !SamePath(ready.RuntimePath, dll))
                            throw new InvalidOperationException("The port belongs to another server. No session record was replaced.");
                        if (process.HasExited) throw new InvalidOperationException("Server exited before startup completed.");
                        break;
                    }
                    await Task.Delay(100);
                }
                if (ready == null) throw new InvalidOperationException("Startup timed out. The server was not force-stopped; inspect its log before retrying.");
                SaveSession(options, new Session(process.Id, process.StartTime.ToUniversalTime(), options.Port, ready.InstanceId, options.Project, dll, token));
                Console.WriteLine("Metroidvania Studio: " + options.Url);
                Console.WriteLine("MiniMap: " + options.Url + "?view=minimap");
                Console.WriteLine("Workspace: " + options.Project);
                if (options.Foreground) foreground = process; else process.Dispose();
                OpenBrowser(options);
            }
            if (foreground != null)
            {
                var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancelled.TrySetResult(); };
                Console.CancelKeyPress += handler;
                try
                {
                    if (await Task.WhenAny(foreground.WaitForExitAsync(), cancelled.Task) == cancelled.Task)
                        using (await Lock(options)) await Stop(options);
                }
                finally { Console.CancelKeyPress -= handler; foreground.Dispose(); }
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }

    private static Options Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("run" or "stop" or "check")) throw new ArgumentException("Usage: launcher run|stop|check --studio-root <folder> [--project <folder>] [--port <number> | --auto-port] [--no-browser] [--restart]");
        string? root = null, project = null, build = null, storageRoot = null; int port = 18765;
        bool noBrowser = false, restart = false, foreground = false, autoPort = false;
        for (int i = 1; i < args.Length; i++)
        {
            string Value() { if (++i >= args.Length) throw new ArgumentException("Missing option value."); return args[i]; }
            switch (args[i])
            {
                case "--studio-root": root = Value(); break;
                case "--project": project = Value(); break;
                case "--storage-root": storageRoot = Value(); break;
                case "--port": if (!int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port < 1024 || port > 65535) throw new ArgumentException("Choose a port between 1024 and 65535."); autoPort = false; break;
                case "--auto-port": autoPort = true; break;
                case "--build-directory": build = Value(); break;
                case "--no-browser": noBrowser = true; break;
                case "--restart": restart = true; break;
                case "--foreground": foreground = true; break;
                default: throw new ArgumentException("Unknown option: " + args[i]);
            }
        }
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("--studio-root is required.");
        root = Normalize(root);
        return new Options(args[0], root, Normalize(string.IsNullOrWhiteSpace(project) ? storageRoot ?? root : project), port, noBrowser, restart, foreground, build == null ? null : Normalize(build), autoPort)
        { LegacyWorkspace = string.IsNullOrWhiteSpace(project) ? storageRoot == null ? Path.Combine(root, ".local/workspace") : PortableWorkspace.LegacyUserWorkspace : null };
    }
    private static string Normalize(string value) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    private static bool SamePath(string? left, string? right) => left != null && right != null && string.Equals(Normalize(left), Normalize(right), Paths);
    private static Task<FileStream> Lock(Options options) => LockFile(Path.Combine(options.Local, $"server-{options.Port}.lock"));
    private static async Task<FileStream> LockFile(string file)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { if (DateTime.UtcNow >= deadline) throw new IOException("Another launcher is working on this session. Retry when it finishes."); await Task.Delay(100); }
        }
    }
    private static string ResolveBuild(Options options)
    {
        string build = options.BuildDirectory ?? options.Root;
        if (options.BuildDirectory == null && !File.Exists(Path.Combine(build, "metroidvania-studio/server/MetroidvaniaStudio.Server.dll")))
        {
            string latest = Path.Combine(options.Root, "builds/latest.json");
            if (!File.Exists(latest)) throw new InvalidOperationException("No successful build exists. Run the platform Build script first.");
            var index = JsonSerializer.Deserialize<BuildIndex>(File.ReadAllText(latest), Json);
            if (index?.FormatVersion != 1 || index.Folder == null || !Regex.IsMatch(index.Folder, @"^\d+\.\d+\.\d+-\d{8}-\d{6}-[a-f0-9]{8}$"))
                throw new InvalidOperationException("Invalid local build index. Run the platform Build script again.");
            build = Path.Combine(options.Root, "builds", index.Folder);
        }
        foreach (string file in new[] {
            "metroidvania-studio/server/MetroidvaniaStudio.Server.dll", "metroidvania-studio/server/MetroidvaniaStudio.Server.deps.json", "metroidvania-studio/server/MetroidvaniaStudio.Server.runtimeconfig.json",
            "metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.dll", "metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.deps.json", "metroidvania-studio/launcher/MetroidvaniaStudio.Launcher.runtimeconfig.json",
            "metroidvania-studio/dist/index.html", "metroidvania-studio/dist/app.js", "metroidvania-studio/dist/map-canvas.js", "metroidvania-studio/localization/MetroidvaniaStudioLocale.csv", "samples/catalog.json" })
            if (!File.Exists(Path.Combine(build, file))) throw new InvalidOperationException("Incomplete build: " + file + ". Run the platform Build script again.");
        using var runtime = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--list-runtimes") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })
            ?? throw new InvalidOperationException("Could not inspect installed runtimes.");
        string runtimes = runtime.StandardOutput.ReadToEnd(); runtime.WaitForExit();
        if (runtime.ExitCode != 0 || !Regex.IsMatch(runtimes, @"(?m)^Microsoft\.AspNetCore\.App 10\."))
            throw new InvalidOperationException("ASP.NET Core Runtime 10 is required. Install it before starting or restarting the studio.");
        return Normalize(build);
    }
    private static async Task<Health?> ReadHealth(Options options)
    {
        try
        {
            // Windows may take longer to report a refused connection than the HTTP health timeout.
            // A successful TCP connection followed by invalid/timed-out HTTP is an occupied port.
            using (var probe = new TcpClient())
            {
                try { await probe.ConnectAsync(IPAddress.Loopback, options.Port).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (SocketException socket) when (socket.SocketErrorCode == SocketError.ConnectionRefused) { return null; }
            }
            using var response = await Http.GetAsync(options.Url + "api/health");
            response.EnsureSuccessStatusCode();
            string payload = await response.Content.ReadAsStringAsync();
            if (payload.Length > 32768) throw new InvalidOperationException("The port is serving another application.");
            var health = JsonSerializer.Deserialize<Health>(payload, Json);
            if (health == null || string.IsNullOrEmpty(health.InstanceId) || string.IsNullOrEmpty(health.ProjectPath)) throw new InvalidOperationException("The port is serving another application.");
            return health;
        }
        catch (HttpRequestException error) when (error.InnerException is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionRefused) { return null; }
        catch (Exception error) { throw new InvalidOperationException("Could not verify the application on port " + options.Port + ". No running process was stopped.", error); }
    }
    private static void AssertWorkspace(Options options, Health health)
    {
        if (!SamePath(health.ProjectPath, options.Project)) throw new InvalidOperationException("The port is serving a different workspace. Choose another port.");
    }
    private static Session? ReadSession(Options options) => File.Exists(options.Record) ? JsonSerializer.Deserialize<Session>(File.ReadAllText(options.Record).TrimStart('\uFEFF'), Json) : null;
    private static Process? GetProcess(int pid)
    {
        if (pid < 1) return null;
        try { var process = Process.GetProcessById(pid); if (!process.HasExited) return process; process.Dispose(); }
        catch (ArgumentException) { }
        return null;
    }
    private static Session VerifyOwner(Options options, Session? record, Health health)
    {
        if (record == null) throw new InvalidOperationException("A server is running without a matching launcher record. Use its original launcher to stop it.");
        if (record.Port != options.Port || (record.InstanceId != health.InstanceId && !(record.InstanceId == "" && record.LaunchToken?.Length == 32 && record.LaunchToken == health.LaunchToken)) || !SamePath(record.ProjectPath, options.Project)) throw new InvalidOperationException("The server session does not match its launcher record; nothing was stopped.");
        using var process = GetProcess(record.Pid);
        // Unix Process.StartTime is derived from boot time and can drift between callers.
        // New sessions instead require the server's exact nonce, PID and runtime path together.
        bool exactSession = health.ProcessId == record.Pid && record.LaunchToken?.Length == 32 && health.LaunchToken == record.LaunchToken && SamePath(health.RuntimePath, record.DllPath);
        if (process == null || (OperatingSystem.IsWindows() || !exactSession) && process.StartTime.ToUniversalTime() != record.ProcessStartUtc.ToUniversalTime()
            || health.ProcessId != 0 && health.ProcessId != record.Pid
            || health.LaunchToken != null && health.LaunchToken != record.LaunchToken
            || health.RuntimePath != null && !SamePath(health.RuntimePath, record.DllPath))
            throw new InvalidOperationException("The recorded process or server identity has changed; nothing was stopped.");
        return record with { InstanceId = health.InstanceId };
    }
    private static async Task Stop(Options options)
    {
        Health? health = await ReadHealth(options);
        Session? record = ReadSession(options);
        if (record == null) { if (health != null) throw new InvalidOperationException("Server has no matching launcher record; use its original launcher."); return; }
        if (record.Port != options.Port || !SamePath(record.ProjectPath, options.Project)) throw new InvalidOperationException("The recorded server belongs to a different workspace; nothing was stopped.");
        using var process = GetProcess(record.Pid);
        if (process == null && health == null) { File.Delete(options.Record); return; }
        if (health == null) throw new InvalidOperationException("The server session could not be verified. It was left running to preserve edits.");
        AssertWorkspace(options, health); record = VerifyOwner(options, record, health);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Url + "api/shutdown") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Metroidvania-Studio-Instance", record.InstanceId);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        // Shutdown may take longer than health; never terminate a process on a save timeout.
        using var shutdown = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await shutdown.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Server could not save and stop: " + await response.Content.ReadAsStringAsync());
        try { await process!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException) { throw new InvalidOperationException("The server is still saving. It was not force-stopped; retry later."); }
        File.Delete(options.Record); Console.WriteLine("Metroidvania Studio saved its session and stopped.");
    }
    private static void SaveSession(Options options, Session record)
    {
        string temp = options.Record + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(record, Json)); File.Move(temp, options.Record, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static async Task<Process> Spawn(Options options, string build, string dll, string token)
    {
        string dotnet = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the current .NET runtime.");
        if (!Path.GetFileNameWithoutExtension(dotnet).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Run the launcher through dotnet.");
        string stdout = Path.Combine(options.Local, $"server-{options.Port}.log"), stderr = Path.Combine(options.Local, $"server-{options.Port}-error.log");
        string[] arguments = [dll, "--studio-root", build, "--project", options.Project, "--port", options.Port.ToString(CultureInfo.InvariantCulture), "--launch-token", token];
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (OperatingSystem.IsWindows()) return WindowsProcess.Start(dotnet, arguments, build, stdout, stderr);
        {
            start.FileName = "/bin/sh";
            start.ArgumentList.Add("-c");
            // Values are positional parameters, never interpolated into shell code.
            start.ArgumentList.Add("out=$1; err=$2; shift 2; nohup \"$@\" >\"$out\" 2>\"$err\" </dev/null & echo $!");
            start.ArgumentList.Add("studio-start"); start.ArgumentList.Add(stdout); start.ArgumentList.Add(stderr); start.ArgumentList.Add(dotnet);
            foreach (string arg in arguments) start.ArgumentList.Add(arg);
        }
        {
            using var helper = Process.Start(start) ?? throw new InvalidOperationException("Could not start the background process helper.");
            Task<string> output = helper.StandardOutput.ReadToEndAsync(), error = helper.StandardError.ReadToEndAsync();
            await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            if (helper.ExitCode != 0 || !int.TryParse((await output).Trim(), out int pid)) throw new InvalidOperationException("Could not start server: " + await error);
            return GetProcess(pid) ?? throw new InvalidOperationException("Server exited during startup. See " + stderr);
        }
    }
    private static void OpenBrowser(Options options)
    {
        if (options.NoBrowser) return;
        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows()) start = new ProcessStartInfo(options.Url) { UseShellExecute = true };
            else { start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false }; start.ArgumentList.Add(options.Url); }
            using var browser = Process.Start(start);
        }
        catch (Exception error) { Console.Error.WriteLine("Open " + options.Url + " in your browser. " + error.Message); }
    }
}
