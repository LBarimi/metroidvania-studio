using System.Reflection;
using MetroidvaniaStudio.Storage;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MetroidvaniaStudio.Editor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // A separate process runs the existing server; its work never blocks the window thread.
        if (args.FirstOrDefault() == "--server")
        {
            var entry = typeof(MetroidvaniaStudio.Server.EditorWorkspace).Assembly.EntryPoint
                ?? throw new InvalidOperationException("The bundled server entry point is missing.");
            try
            {
                object? result = entry.Invoke(null, [args.Skip(1).ToArray()]);
                if (result is Task<int> taskResult) return taskResult.GetAwaiter().GetResult();
                if (result is Task task) task.GetAwaiter().GetResult();
                return result is int code ? code : 0;
            }
            catch (TargetInvocationException error) { throw error.InnerException ?? error; }
        }
        DesktopOptions? options = null;
        try
        {
            options = DesktopOptions.Parse(args);
            if (options.Check)
            {
                options.ValidatePayload();
                string runtime = CoreWebView2Environment.GetAvailableBrowserVersionString();
                options.Report(new { ok = true, runtime, frameworkPath = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "System.Private.CoreLib.dll"), executable = Environment.ProcessPath });
                return 0;
            }
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            using var window = new StudioWindow(options);
            Application.Run(window);
            return window.ExitCode;
        }
        catch (Exception error)
        {
            options?.Report(new { ok = false, error = error.ToString() });
            if (options?.Background != true && options?.Check != true && !args.Contains("--self-test") && !args.Contains("--inspect-existing") && !args.Contains("--check"))
                MessageBox.Show(error.Message, "Metroidvania Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}

internal sealed record DesktopOptions(string Workspace, string UserData, string? Result, bool SelfTest, bool Check, bool InspectExisting)
{
    public bool Background => SelfTest || InspectExisting;
    public string Payload => Path.Combine(AppContext.BaseDirectory, "app");
    public static DesktopOptions Parse(string[] args)
    {
        string? project = null, result = null, userData = null;
        bool selfTest = false, check = false, inspectExisting = false;
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("An option value is missing.");
            switch (args[i])
            {
                case "--project": project = Value(); break;
                case "--user-data": userData = Value(); break;
                case "--result": result = Path.GetFullPath(Value()); break;
                case "--self-test": selfTest = true; break;
                case "--check": check = true; break;
                case "--inspect-existing": inspectExisting = true; break;
                default: throw new ArgumentException("Unknown option: " + args[i]);
            }
        }
        if (inspectExisting && result == null) throw new ArgumentException("Inspection requires --result.");
        if (selfTest)
        {
            if (result == null) throw new ArgumentException("Background validation requires --result.");
            // Tests always create a fresh workspace, regardless of the normal workspace settings.
            string testRoot = Path.Combine(Path.GetDirectoryName(result)!, "session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);
            project = Path.Combine(testRoot, "workspace");
            userData = Path.Combine(testRoot, "webview");
        }
        string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MetroidvaniaStudio");
        bool defaultStorage = project == null, configuredStorage = false;
        if (project == null)
        {
            string settingsFile = Path.Combine(AppContext.BaseDirectory, "desktop-settings.json");
            if (File.Exists(settingsFile))
            {
                using var settings = JsonDocument.Parse(File.ReadAllText(settingsFile));
                if (settings.RootElement.TryGetProperty("workspaceRelativePath", out var configured))
                {
                    string candidate = Path.GetFullPath(configured.GetString()!, AppContext.BaseDirectory);
                    if (Directory.Exists(candidate)) { project = candidate; configuredStorage = true; }
                }
            }
        }
        project ??= AppContext.BaseDirectory;
        if (defaultStorage && !check && !inspectExisting)
            project = PortableWorkspace.Prepare(project, configuredStorage
                ? Path.Combine(project, ".local/workspace") : PortableWorkspace.LegacyUserWorkspace);
        return new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(project)),
            Path.GetFullPath(userData ?? Path.Combine(dataRoot, "webview")), result, selfTest, check, inspectExisting);
    }
    public void ValidatePayload()
    {
        foreach (string relative in new[] { "metroidvania-studio/dist/index.html", "metroidvania-studio/dist/app.js",
            "metroidvania-studio/localization/MetroidvaniaStudioLocale.csv", "samples/catalog.json", "metroidvania-studio/cli/MetroidvaniaStudio.Cli.dll",
            "metroidvania-studio/cli/MetroidvaniaStudio.Cli.deps.json", "metroidvania-studio/cli/MetroidvaniaStudio.Cli.runtimeconfig.json" })
            if (!File.Exists(Path.Combine(Payload, relative))) throw new FileNotFoundException("Keep the app folder beside metroidvania-studio.exe: " + relative);
    }
    public void Report(object value)
    {
        if (Result == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Result)!);
        File.WriteAllText(Result, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
