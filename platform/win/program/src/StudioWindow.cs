using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MetroidvaniaStudio.Editor;

internal sealed class StudioWindow : Form
{
    private readonly DesktopOptions options;
    private readonly ServerSession session;
    private readonly bool child;
    private readonly Uri? initialUri;
    private readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(24, 24, 24) };
    private readonly Label message = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.Gainsboro, BackColor = Color.FromArgb(24, 24, 24), Text = "Metroidvania Studio…" };
    private readonly List<StudioWindow> children = [];
    private readonly TaskCompletionSource initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? startTask;
    private bool allowClose, closing, started;
    private JsonElement? testResult;
    public int ExitCode { get; private set; }
    protected override bool ShowWithoutActivation => options?.Background == true;
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; if (options?.Background == true) value.ExStyle |= 0x08000000 | 0x00000080; return value; }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public StudioWindow(DesktopOptions options, ServerSession? shared = null, Uri? uri = null)
    {
        this.options = options; child = shared != null; session = shared ?? new ServerSession(options); initialUri = uri;
        Text = child ? uri?.AbsolutePath.StartsWith("/docs", StringComparison.Ordinal) == true ? "Metroidvania Studio · Documentation" : "Metroidvania Studio · MiniMap" : "Metroidvania Studio";
        BackColor = Color.FromArgb(24, 24, 24); ClientSize = new Size(1440, 900); MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        if (options.Background)
        {
            Opacity = 0; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
        }
        else if (!child) WindowState = FormWindowState.Maximized;
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        Controls.Add(web); Controls.Add(message);
        Shown += (_, _) => { startTask = StartWindow(); };
        FormClosing += OnClosing;
        FormClosed += (_, _) => { if (!child) session.Dispose(); };
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1; _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }
    private async Task StartWindow()
    {
        if (started) return; started = true;
        try
        {
            if (!child) await session.Start();
            var environmentOptions = new CoreWebView2EnvironmentOptions();
            if (options.Background) environmentOptions.AdditionalBrowserArguments = "--disable-background-timer-throttling --disable-renderer-backgrounding";
            var environment = await CoreWebView2Environment.CreateAsync(null, options.UserData, environmentOptions);
            await web.EnsureCoreWebView2Async(environment);
            // Use packaged UI modules even when sharing an already-running browser server.
            // API and asset requests keep using the verified workspace server.
            web.CoreWebView2.AddWebResourceRequestedFilter(session.Url + "*", CoreWebView2WebResourceContext.All);
            web.CoreWebView2.WebResourceRequested += (_, e) =>
            {
                var uri = new Uri(e.Request.Uri);
                string name = uri.AbsolutePath == "/" ? "index.html" : uri.AbsolutePath.TrimStart('/');
                bool documentation = name == "docs" || name.StartsWith("docs/", StringComparison.Ordinal);
                if (name is "docs" or "docs/") name = "docs/index.html";
                string leaf = documentation ? name[5..] : name;
                if (!LocalPage(e.Request.Uri) || leaf.Contains('/') || leaf.Contains('\\') || leaf.Contains("..")) return;
                string? mime = Path.GetExtension(name) switch
                {
                    ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css",
                    ".svg" => "image/svg+xml", ".json" when name == "build-info.json" || documentation => "application/json", ".lua" when documentation => "text/plain", _ => null
                };
                string file = Path.Combine(options.Payload, "metroidvania-studio/dist", name);
                if (mime == null || !File.Exists(file)) return;
                e.Response = environment.CreateWebResourceResponse(new MemoryStream(File.ReadAllBytes(file)), 200, "OK",
                    "Content-Type: " + mime + "; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n" +
                    "Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'none\r\n");
            };
            var settings = web.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            web.ZoomFactor = 1;
            web.CoreWebView2.NavigationStarting += (_, e) => { if (!LocalPage(e.Uri)) e.Cancel = true; };
            web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (!LocalPage(e.Uri)) return;
                var mini = new StudioWindow(options, session, new Uri(e.Uri));
                children.Add(mini); mini.FormClosed += (_, _) => children.Remove(mini);
                mini.Show(this);
            };
            web.CoreWebView2.ProcessFailed += (_, e) =>
            {
                if (allowClose) return;
                ExitCode = 1; message.Text = "The editor display stopped. Close and reopen the program.\n" + e.ProcessFailedKind;
                message.BringToFront();
            };
            var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess) navigation.TrySetResult();
                else navigation.TrySetException(new IOException("The editor page could not load: " + e.WebErrorStatus));
            };
            web.CoreWebView2.Navigate((initialUri ?? new Uri(session.Url, options.Background ? "/?desktop-test=1" : "/")).ToString());
            await navigation.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (initialUri?.AbsolutePath.StartsWith("/docs", StringComparison.Ordinal) != true) await Evaluate("""
                const deadline = Date.now() + 20000;
                while (!window.metroidvaniaDesktop) {
                  if (Date.now() > deadline) throw new Error('Editor initialization timed out.');
                  await new Promise(resolve => setTimeout(resolve, 50));
                }
                return true;
                """);
            message.Hide(); initialized.TrySetResult();
            if (options.Background && !child) await ValidateWindow();
        }
        catch (Exception error)
        {
            initialized.TrySetException(error);
            ExitCode = 1; message.Text = error.Message; message.BringToFront();
            if (options.Background)
            {
                options.Report(new { ok = false, error = error.ToString() });
                try { await session.Stop(); } catch { }
                CloseImmediately();
            }
        }
    }
    private bool LocalPage(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == session.Url.Scheme && uri.Host == session.Url.Host && uri.Port == session.Url.Port;

    private async Task<JsonElement> Evaluate(string body, int seconds = 45)
    {
        string result = await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
            JsonSerializer.Serialize(new { expression = "(async () => {\n" + body + "\n})()", awaitPromise = true, returnByValue = true, userGesture = true }))
            .WaitAsync(TimeSpan.FromSeconds(seconds));
        using var parsed = JsonDocument.Parse(result);
        if (parsed.RootElement.TryGetProperty("exceptionDetails", out var exception)) throw new InvalidOperationException(exception.ToString());
        var value = parsed.RootElement.GetProperty("result");
        return value.TryGetProperty("value", out var returned) ? returned.Clone() : JsonSerializer.SerializeToElement<object?>(null);
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true; if (closing) return; closing = true;
        try
        {
            if (startTask != null && !startTask.IsCompleted) await startTask;
            if (initialized.Task.IsCompletedSuccessfully && !child)
                await Evaluate("return await window.metroidvaniaDesktop.prepareClose();", 60);
            if (!child) await session.Stop();
            foreach (var mini in children.ToArray()) mini.CloseImmediately();
            if (options.Background && !child)
                options.Report(new { ok = true, webViewRuntime = web.CoreWebView2.Environment.BrowserVersionString,
                    workspace = options.Workspace, ownedServer = session.Owned, serverPid = session.ProcessId,
                    serverStopped = session.Stopped, checks = testResult });
            CloseImmediately();
        }
        catch (Exception error)
        {
            closing = false;
            if (web.CoreWebView2 != null)
                try { await Evaluate("window.metroidvaniaDesktop?.resume(); return true;"); } catch { }
            if (options.Background)
            {
                ExitCode = 1; options.Report(new { ok = false, error = error.ToString() });
                try { await session.Stop(); } catch { }
                CloseImmediately();
            }
            else MessageBox.Show(this, error.Message + "\n\nThe window remains open so your work can be saved.",
                "Metroidvania Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private void CloseImmediately()
    {
        allowClose = true;
        foreach (var mini in children.ToArray()) mini.CloseImmediately();
        web.Dispose(); Close();
    }

    private async Task ValidateWindow()
    {
        string script = options.InspectExisting
            ? "const t = window.metroidvaniaDesktop.test; return { readOnly: true, roomCount: t.state.document.rooms.length, canvasWidth: document.querySelector('#map-canvas').width, file: t.state.file };"
            : File.ReadAllText(Path.Combine(options.Payload, "desktop-self-test.js"));
        testResult = await Evaluate(script, 90);
        string screenshot = Path.ChangeExtension(options.Result!, ".png");
        using (var stream = File.Create(screenshot))
            await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        await Evaluate("window.open('/?view=minimap', '_blank', 'noopener'); return true;");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (children.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (children.Count != 1) throw new InvalidOperationException("The minimap did not open in an internal window.");
        await children[0].initialized.Task.WaitAsync(TimeSpan.FromSeconds(25));
        var miniVisible = await children[0].Evaluate("return document.querySelector('#mini-canvas').getBoundingClientRect().width > 0;");
        if (!miniVisible.GetBoolean()) throw new InvalidOperationException("The minimap canvas has no size.");
        children[0].CloseImmediately();
        Close();
    }
}
