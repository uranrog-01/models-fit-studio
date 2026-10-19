using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using ModelFitStudio.Core;

namespace ModelFitStudio;

public partial class MainWindow : Window
{
    private HardwareInfo? _hw;
    private readonly List<DownloadProgress> _downloads = [];

    // ---- system tray ------------------------------------------------------
    // Closing the window can leave the app in the tray so downloads keep running; it
    // then quits from the tray menu. The icon is read straight from the app .ico.
    private NotifyIcon? _tray;
    // the NVIDIA engine is a long download: it can be stopped
    private CancellationTokenSource? _nvidiaCts;
    private bool _quitRequested;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        IsVisibleChanged += (_, _) => UpdateWebVisibility();
        StateChanged += (_, _) => UpdateWebVisibility();
        InitTray();

        ModelStore.HfTokenProvider = () => AppSettings.Get().HfToken;
        ApplyTheme(AppSettings.Get().Theme);
        LocalRuns.Changed += () => Push("running", RunningList());
        AgentServer.FitRequests = AppSettings.Get().AgentFit;
        AgentServer.Thinking = AppSettings.Get().AgentThinking;
        AgentServer.Changed += () => Push("agent", AgentStatus());
    }

    /// <summary>The window's own chrome in the colours of the chosen theme, so the title bar and the page are one surface.</summary>
    private void ApplyTheme(string? theme)
    {
        // bar and its edge, and what shows behind the page before it paints
        var (bar, edge, page) = theme switch
        {
            "ember" => ("#0B0B0B", "#1A1A1A", "#0B0B0B"),
            "solar" => ("#232323", "#2E2E2E", "#E9E8DF"),
            "glass" => ("#161D25", "#2A333D", "#1A222C"),
            _ => ("#0C0E12", "#181B24", "#0C0E12"),
        };
        try
        {
            var back = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(bar);
            Background = new System.Windows.Media.SolidColorBrush(back);
            TitleBar.Background = new System.Windows.Media.SolidColorBrush(back);
            TitleBar.BorderBrush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(edge));
            var behind = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(page);
            Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(behind.R, behind.G, behind.B);
        }
        catch { }
    }

    // ---- maximise -----------------------------------------------------------
    // A window that draws its own title bar is maximised over the whole monitor by
    // default, taskbar included, which hid the app's footer behind it. Windows asks
    // how large "maximised" is; the answer is the monitor's work area.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle) is { } src)
            src.AddHook(WindowHook);
    }

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
        var monitor = MonitorFromWindow(hwnd, 2 /* nearest */);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        // the minimum size stays what the window asks for, in device pixels
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        mmi.ptMinTrackSize.X = (int)(MinWidth * dpi.DpiScaleX);
        mmi.ptMinTrackSize.Y = (int)(MinHeight * dpi.DpiScaleY);
        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    private async void UpdateWebVisibility()
    {
        var core = Web?.CoreWebView2;
        if (core is null) return;
        var visible = IsVisible && WindowState != WindowState.Minimized;
        try
        {
            core.MemoryUsageTargetLevel = visible ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
            if (visible) core.Resume();
            Push("uiVisibility", new { visible });
            if (!visible) await core.TrySuspendAsync();
        }
        catch { /* older WebView2 runtimes still use the JS polling guard */ }
    }

    private void InitTray()
    {
        try
        {
            var ico = TryLoadIcon()
                      ?? System.Drawing.SystemIcons.Application;
            _tray = new NotifyIcon
            {
                Icon = ico,
                Visible = true,
                Text = "Models Fit Studio",
            };
            _tray.DoubleClick += (_, _) => RestoreFromTray();
            _tray.BalloonTipClicked += (_, _) => RestoreFromTray();

            var menu = new ContextMenuStrip();

            menu.Items.Add("Open Models Fit Studio", null, (_, _) => RestoreFromTray());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit", null, (_, _) => QuitFromTray());
            _tray.ContextMenuStrip = menu;
        }
        catch { /* no shell / headless — closing still works as a plain quit */ }
    }

    private static System.Drawing.Icon? TryLoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) &&
                System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } fromExe)
                return fromExe;
        }
        catch { }
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(p))
                return new System.Drawing.Icon(p, new System.Drawing.Size(32, 32));
        }
        catch { }
        return null;
    }

    internal void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        });
    }

    private void HideToTray(bool firstTime)
    {
        Hide();
        if (_tray is null) return;
        try
        {
            if (firstTime)
            {
                _tray.BalloonTipTitle = "Models Fit Studio is still running";
                _tray.BalloonTipText = "Downloads keep running. Right-click the tray icon to open it or quit.";
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(4000);
            }
        }
        catch { }
    }

    private void QuitFromTray()
    {
        _quitRequested = true;
        Close();
        System.Windows.Application.Current?.Shutdown();
    }

    // The page's own storage (the saved battle, choices it remembers) lives with the app's data.
    // WebView2's default is a "<exe name>.WebView2" folder beside the exe, so a renamed or moved
    // exe started empty, and a copy in Program Files could not write at all. The folder the exe
    // had while it was called ModelFitStudio.exe is carried over once.
    private static string BrowserDataFolder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelFitStudio", "WebView2");
        var old = Path.Combine(AppContext.BaseDirectory, "ModelFitStudio.exe.WebView2");
        if (!Directory.Exists(dir) && Directory.Exists(old)) CopyFolder(old, dir);
        return dir;
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            try { File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true); } catch { }
        foreach (var d in Directory.GetDirectories(from))
            CopyFolder(d, Path.Combine(to, Path.GetFileName(d)));
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var webRoot = Path.Combine(AppContext.BaseDirectory, "Web");
            if (!Directory.Exists(webRoot))
                webRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Web"));

            await Web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(null, BrowserDataFolder()));
            // pick up downloads that were mid-flight when the app last closed
            RestoreQueue();
            Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Web.CoreWebView2.Settings.IsZoomControlEnabled = false;
            Web.CoreWebView2.Settings.AreDevToolsEnabled = true;
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "modelfit.local", Path.GetFullPath(webRoot), CoreWebView2HostResourceAccessKind.Allow);
            Web.CoreWebView2.WebMessageReceived += OnWebMessage;
            Web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            var start = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
            if (start?.StartsWith("--", StringComparison.Ordinal) == true) start = null;
            var suffix = !string.IsNullOrWhiteSpace(start) ? $"#model={Uri.EscapeDataString(start)}" : "";
            // query string is only cache-busting: the UI is served locally via a
            // virtual host mapping, and WebView2 would otherwise keep an old bundle
            Web.CoreWebView2.Navigate($"https://modelfit.local/index.html?v={Guid.NewGuid():N}&theme={AppSettings.Get().Theme}&lang={AppSettings.Get().Language}&ver={typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}" + suffix);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                "Could not start the WebView2 browser engine.\n\n" + ex.Message +
                "\n\nInstall the Microsoft Edge WebView2 Runtime (Evergreen) and start Models Fit Studio again.",
                "Models Fit Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess) return;
        UpdateWebVisibility();
        if (!Environment.GetCommandLineArgs().Contains("--diagnostics")) return;
        // headless smoke test: the UI writes its own state to %TEMP%\modelfit-debug.log
        var probe = @"(function(){
  var q = function(s){ return document.querySelectorAll(s).length; };
  var t = function(id){ var e = document.getElementById(id); return e ? (e.textContent||'').replace(/\s+/g,' ').slice(0,200) : '<missing>'; };
  return JSON.stringify({
    tabs: q('.tab'), panes: q('.tabpane'), cards: q('.mcard'), rows: q('.lib-row'),
    verdict: t('verdictStatus'), list: t('hfHint'), libHint: t('libHint'),
    hw: t('hwBody').slice(0,120),
    body: document.body.innerText.replace(/\s+/g,' ').slice(0,600)
  });
})()";

        int[] waits = [3000, 5000, 8000, 12000, 18000];
        var waited = 0;
        foreach (var wait in waits)
        {
            await Task.Delay(wait);
            waited += wait;
            try
            {
                var raw = await Web.ExecuteScriptAsync(probe);
                var text = JsonDocument.Parse(raw).RootElement.GetString() ?? "";
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "modelfit-debug.log"),
                    $"\n===== UI at {waited / 1000}s =====\n{text}\n");
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "modelfit-debug.log"),
                    $"\n===== UI at {waited / 1000}s: {ex.Message} =====\n");
            }
        }
    }

    // ---- JS bridge ---------------------------------------------------------
    private sealed class Msg
    {
        public int Id { get; set; }
        public string Cmd { get; set; } = "";
        public JsonElement Args { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    // downloads run one at a time so the pipe to Hugging Face is not shared
    private readonly SemaphoreSlim _downloadPump = new(1);

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        Msg? msg = null;
        try { msg = JsonSerializer.Deserialize<Msg>(e.WebMessageAsJson, JsonOpts); } catch { }
        if (msg is null) return;

        try
        {
            var data = await DispatchAsync(msg);
            Reply(msg.Id, data, null);
        }
        catch (Exception ex)
        {
            Reply(msg.Id, null, ex.Message);
        }
    }

    private void Reply(int id, object? data, string? error)
    {
        var payload = JsonSerializer.Serialize(new { id, ok = error is null, data, error }, JsonOpts);
        Dispatcher.BeginInvoke(() =>
        {
            try { Web?.CoreWebView2?.PostWebMessageAsJson(payload); } catch { }
        });
    }

    /// <summary>Pushes a live event to the UI without a pending request (download progress).</summary>
    private void Push(string type, object? payload)
    {
        var json = JsonSerializer.Serialize(new { type, data = payload }, JsonOpts);
        Dispatcher.BeginInvoke(() =>
        {
            try { Web?.CoreWebView2?.PostWebMessageAsJson(json); } catch { }
        });
    }

    private async Task<object?> DispatchAsync(Msg msg)
    {
        var ct = CancellationToken.None;
        switch (msg.Cmd)
        {
            case "ping":
                return new
                {
                    version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "",
                    llmfit = LlmfitRunner.Available ? LlmfitRunner.ExePath : (string?)null,
                };

            case "scanHardware":
                await HardwareScanAsync(ct);
                return ToHardwareJson(_hw!);

            case "analyze":
                if (_hw is null) await HardwareScanAsync(ct);
                var input = msg.Args.TryGetProperty("input", out var inp) ? inp.GetString() ?? "" : "";
                return await Analyzer.AnalyzeAsync(input, _hw!, ct);

            case "log":
                try
                {
                    var text = msg.Args.TryGetProperty("text", out var lt) ? lt.GetString() ?? "" : "";
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "modelfit-debug.log"),
                        $"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}");
                }
                catch { }
                return new { logged = true };

            // ---------- library: what is already on this machine ----------
            case "installed":
                // folder walks can touch disk for a while — the tab must never freeze on its own refresh
                return await Task.Run(() => new
                {
                    root = ModelStore.RootDir,
                    freeGb = ModelStore.FreeDiskSpaceGb(),
                    files = ModelStore.ListInstalled().Select(f => new
                    {
                        repo = f.Repo,
                        file = f.File,
                        bytes = f.Bytes,
                        complete = f.Complete,
                        helper = RepoFiles.IsHelper(f.File, f.Bytes / 1e9),
                        shared = f.Shared,
                        source = f.Source,
                        written = f.Written.ToString("s"),
                        path = ModelStore.ResolvePath(f.Repo, f.File),
                        measuredTps = Measurements.Find(f.Repo, f.File)?.GenTps ?? 0,
                        qualityScore = Measurements.Find(f.Repo, f.File)?.QualityScore ?? 0,
                        firstTokenMs = Measurements.Find(f.Repo, f.File)?.FirstTokenMs ?? 0,
                    }),
                }, ct);

            case "pickFolder":
                var fDlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Select Model Storage Folder (e.g. D:\\AI_Models)",
                    Multiselect = false,
                };
                if (fDlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(fDlg.FolderName))
                {
                    ModelStore.RootDir = fDlg.FolderName;
                    return new { ok = true, dir = ModelStore.RootDir, freeGb = ModelStore.FreeDiskSpaceGb() };
                }
                return new { ok = false, dir = ModelStore.RootDir, freeGb = ModelStore.FreeDiskSpaceGb() };

            case "setLibDir":
                var nDir = msg.Args.TryGetProperty("dir", out var nd) ? nd.GetString() : null;
                if (!string.IsNullOrWhiteSpace(nDir)) ModelStore.RootDir = nDir;
                return new { ok = true, dir = ModelStore.RootDir, freeGb = ModelStore.FreeDiskSpaceGb() };

            case "deleteFile":
                var dRepo = msg.Args.TryGetProperty("repo", out var dr) ? dr.GetString() ?? "" : "";
                var dFile = msg.Args.TryGetProperty("file", out var df) ? df.GetString() ?? "" : "";
                var deletedOk = ModelStore.Delete(dRepo, dFile);
                return new { ok = deletedOk };

            case "openFolder":
                ModelStore.OpenFolder(msg.Args.TryGetProperty("sub", out var of) ? of.GetString() : null);
                return new { opened = true };

            case "openUrl":
                var targetUrl = msg.Args.TryGetProperty("url", out var tu) ? tu.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(targetUrl)) Open(targetUrl);
                return new { opened = true };

            // ---------- settings ----------
            case "getSettings":
                return AppSettings.Get();

            case "saveSettings":
                var es = AppSettings.Get();
                if (msg.Args.TryGetProperty("minimizeToTrayOnClose", out var mtProp)) es.MinimizeToTrayOnClose = mtProp.GetBoolean();
                if (msg.Args.TryGetProperty("startWithWindows", out var swProp)) es.StartWithWindows = swProp.GetBoolean();
                if (msg.Args.TryGetProperty("hfToken", out var hfProp))
                {
                    var tokenStr = hfProp.GetString();
                    es.HfToken = string.IsNullOrWhiteSpace(tokenStr) ? null : tokenStr.Trim();
                }
                if (msg.Args.TryGetProperty("theme", out var thProp))
                {
                    es.Theme = thProp.GetString() is "ember" or "solar" or "glass" ? thProp.GetString()! : "default";
                    ApplyTheme(es.Theme);
                }
                if (msg.Args.TryGetProperty("language", out var lgProp) &&
                    System.Text.RegularExpressions.Regex.IsMatch(lgProp.GetString() ?? "", "^[a-z]{2}(-[A-Z]{2})?$"))
                    es.Language = lgProp.GetString()!;
                AppSettings.Save(es);
                return new { ok = true, settings = es };

            case "storageBreakdown":
                var bd = ModelStore.GetStorageBreakdown();
                return new
                {
                    totalModelGb = bd.TotalModelGb,
                    incompletePartGb = bd.IncompletePartGb,
                    completedFileCount = bd.CompletedFileCount,
                    incompleteFileCount = bd.IncompleteFileCount,
                    freeDiskGb = bd.FreeDiskGb,
                    rootDir = ModelStore.RootDir
                };

            case "purgeIncomplete":
                var (purgedCount, reclaimedGb) = ModelStore.PurgeIncompleteDownloads();
                return new { count = purgedCount, reclaimedGb };

            // ---------- downloads ----------
            case "download":
                var dlRepo = msg.Args.TryGetProperty("repo", out var dlr) ? dlr.GetString() ?? "" : "";
                var dlFile = msg.Args.TryGetProperty("file", out var dlf) ? dlf.GetString() ?? "" : "";
                var size = msg.Args.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var szv) ? szv : 0;
                if (string.IsNullOrWhiteSpace(dlRepo) || string.IsNullOrWhiteSpace(dlFile))
                    return new { started = false, error = "No file was given." };
                if (ModelStore.HasFile(dlRepo, dlFile))
                    return new { started = false, already = true, path = ModelStore.ResolvePath(dlRepo, dlFile) };
                QueueDownload(dlRepo, dlFile, size);
                return new { started = true };

            case "downloads":
                return DownloadSnapshot();

            case "cancelDownload":
                var cKey = msg.Args.TryGetProperty("key", out var ck) ? ck.GetString() ?? "" : "";
                return new { ok = CancelDownload(cKey) };

            // the download manager: pause keeps the .part, resume continues it
            case "pauseDownload":
                var pdKey = msg.Args.TryGetProperty("key", out var pdk) ? pdk.GetString() ?? "" : "";
                return new { ok = PauseDownload(pdKey) };

            case "resumeDownload":
                var rdKey = msg.Args.TryGetProperty("key", out var rdk) ? rdk.GetString() ?? "" : "";
                return new { ok = ResumeDownload(rdKey) };

            case "resumeAllDownloads":
                return new { resumed = ResumeAllDownloads() };

            case "discardDownload":
                var ddKey = msg.Args.TryGetProperty("key", out var ddk) ? ddk.GetString() ?? "" : "";
                return new { ok = DiscardDownload(ddKey) };

            // ---------- the model list: Hugging Face, sized against this machine ----------
            case "hfSearch":
                if (_hw is null) await HardwareScanAsync(ct);
                var xcQ = msg.Args.TryGetProperty("q", out var xq) ? xq.GetString() ?? "" : "";
                var xcSort = msg.Args.TryGetProperty("sort", out var xs) ? xs.GetString() ?? "downloads" : "downloads";
                var xcFormat = msg.Args.TryGetProperty("format", out var xf) ? xf.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(xcFormat))
                {
                    var xcGgufOnly = !(msg.Args.TryGetProperty("ggufOnly", out var xg) && xg.ValueKind == JsonValueKind.False);
                    xcFormat = xcGgufOnly ? "gguf" : "all";
                }
                var xcRefresh = msg.Args.TryGetProperty("refresh", out var xr) && xr.ValueKind == JsonValueKind.True;
                var xcFitsOnly = msg.Args.TryGetProperty("fitsOnly", out var xfo) && xfo.ValueKind == JsonValueKind.True;
                var xcSpeed = await MachineSpeed.GbPerSecAsync(_hw!, ct);
                var (xcItems, xcErr) = await HfCatalog.SearchAsync(xcQ, xcSort, xcFormat, 48, _hw!, xcSpeed, xcFitsOnly, ct, xcRefresh);
                return new { items = xcItems, error = xcErr };

            // ---------- real speed: run a downloaded file once and keep what it did ----------
            case "engineStatus":
                if (_hw is null) await HardwareScanAsync(ct);
                var eng = Engine.Find();
                var nvEngine = Engine.FindNvidia();
                return new
                {
                    installed = eng is not null,
                    source = eng?.Source,
                    build = eng?.Build,
                    // only a build the app downloaded is the app's to remove: not the built-in one, not one on PATH
                    removable = Directory.Exists(Engine.Dir),
                    serverPath = eng?.ServerPath,
                    nvidia = new
                    {
                        card = _hw!.IsNvidia,
                        active = eng?.Nvidia == true,
                        build = nvEngine?.Build,
                        cuda = NvidiaEngine.CudaFor(_hw.GpuName, _hw.NvidiaDriver),
                        text = NvidiaEngine.Explain(_hw.GpuName, _hw.NvidiaDriver, nvEngine is not null),
                        removable = Directory.Exists(Engine.NvidiaDir),
                    },
                    measurements = Measurements.All().Count,
                    calibrated = _hw is not null && Measurements.Calibration(_hw.GpuName) > 0,
                };

            // asked before the download, so the question carries the real name and size
            case "engineLatest":
                if (_hw is null) await HardwareScanAsync(ct);
                var latest = await Engine.LatestAsync(_hw!.HasGpu, ct);
                return new { build = latest.Build, asset = latest.Asset, mb = Math.Round(latest.Bytes / 1e6) };

            case "installEngine":
                if (_hw is null) await HardwareScanAsync(ct);
                var lastPush = DateTime.MinValue;
                var installed = await Engine.InstallAsync(_hw!.HasGpu, (got, total) =>
                {
                    if ((DateTime.UtcNow - lastPush).TotalMilliseconds < 250 && got < total) return;
                    lastPush = DateTime.UtcNow;
                    Push("engine", new { received = got, total });
                }, ct);
                return new { ok = true, build = installed.Build };

            // ---------- updates: a newer app or a newer llama.cpp (asked of GitHub at most once a day) ----------
            case "updatesCheck":
                return await UpdatesCheckAsync(msg.Args.TryGetProperty("force", out var ucf) && ucf.ValueKind == JsonValueKind.True, ct);

            case "updateEngine":
                if (_hw is null) await HardwareScanAsync(ct);
                var uePush = DateTime.MinValue;
                Action<long, long> ueProgress = (got, total) =>
                {
                    if ((DateTime.UtcNow - uePush).TotalMilliseconds < 250 && got < total) return;
                    uePush = DateTime.UtcNow;
                    Push("engine", new { received = got, total, update = true });
                };
                var current = Engine.Find();
                var updated = current?.Nvidia == true && NvidiaEngine.CudaFor(_hw!.GpuName, _hw.NvidiaDriver) is { } ueCuda
                    ? await Engine.InstallNvidiaAsync(ueCuda, ueProgress, ct)
                    : await Engine.InstallAsync(_hw!.HasGpu, ueProgress, ct);
                _updates = null;   // ask again with the new build
                return new { ok = true, build = updated.Build, restartAgents = AgentServer.State is "on" or "loading" };

            case "removeEngine":
                return new { ok = Engine.Remove() };

            // ---------- NVIDIA's own build: asked for with its real size, then downloaded once ----------
            case "nvidiaLatest":
                if (_hw is null) await HardwareScanAsync(ct);
                var nvCuda = NvidiaEngine.CudaFor(_hw!.GpuName, _hw.NvidiaDriver)
                             ?? throw new InvalidOperationException(NvidiaEngine.Explain(_hw.GpuName, _hw.NvidiaDriver, false));
                var nvLatest = await Engine.NvidiaLatestAsync(nvCuda, ct);
                return new
                {
                    cuda = nvCuda,
                    build = nvLatest.Build,
                    mb = Math.Round(nvLatest.Assets.Sum(x => x.Bytes) / 1e6),
                    files = nvLatest.Assets.Select(x => x.Name).ToArray(),
                };

            case "installNvidia":
                if (_hw is null) await HardwareScanAsync(ct);
                var inCuda = NvidiaEngine.CudaFor(_hw!.GpuName, _hw.NvidiaDriver)
                             ?? throw new InvalidOperationException(NvidiaEngine.Explain(_hw.GpuName, _hw.NvidiaDriver, false));
                if (_nvidiaCts is not null) return new { ok = false, busy = true, cancelled = false, build = "" };
                var nvPush = DateTime.MinValue;
                _nvidiaCts = new CancellationTokenSource();
                try
                {
                    var nvInstalled = await Engine.InstallNvidiaAsync(inCuda, (got, total) =>
                    {
                        if ((DateTime.UtcNow - nvPush).TotalMilliseconds < 250 && got < total) return;
                        nvPush = DateTime.UtcNow;
                        Push("engine", new { received = got, total, nvidia = true });
                    }, _nvidiaCts.Token);
                    return new { ok = true, busy = false, cancelled = false, build = nvInstalled.Build };
                }
                catch (OperationCanceledException)
                {
                    return new { ok = false, busy = false, cancelled = true, build = "" };
                }
                finally
                {
                    _nvidiaCts.Dispose();
                    _nvidiaCts = null;
                }

            case "cancelNvidia":
                try { _nvidiaCts?.Cancel(); } catch (ObjectDisposedException) { }
                return new { ok = true };

            case "removeNvidia":
                return new { ok = Engine.RemoveNvidia() };

            // ---------- suggest: two or three models worth trying on this PC ----------
            case "suggest":
                if (_hw is null) await HardwareScanAsync(ct);
                var picks = await Suggestions.ForAsync(_hw!, ct);
                return new
                {
                    budgetGb = _hw!.BudgetGb,
                    picks = picks.Select(p => new
                    {
                        kind = p.Kind,
                        repo = p.Model.Repo,
                        name = p.Model.Name,
                        origin = p.Model.Origin,
                        paramsB = p.Model.ParamsB,
                        sizeGb = p.Model.SizeGb,
                        memGb = p.Model.MemGb,
                        tpsLow = p.Model.TpsLow,
                        tpsHigh = p.Model.TpsHigh,
                        vision = p.Model.Category == "Multimodal",
                    }).ToArray(),
                };

            case "measure":
                if (_hw is null) await HardwareScanAsync(ct);
                var mRepo = msg.Args.TryGetProperty("repo", out var mr) ? mr.GetString() ?? "" : "";
                var mFile = msg.Args.TryGetProperty("file", out var mf) ? mf.GetString() ?? "" : "";
                return StartMeasure(mRepo, mFile);

            // ---------- run it locally: llama.cpp in its own window, its own chat page ----------
            case "runLocal":
                var rlRepo = msg.Args.TryGetProperty("repo", out var rlr) ? rlr.GetString() ?? "" : "";
                var rlFile = msg.Args.TryGetProperty("file", out var rlf) ? rlf.GetString() ?? "" : "";
                var rlCtx = msg.Args.TryGetProperty("ctx", out var rlc) && rlc.TryGetInt32(out var rlcv) ? rlcv : 8192;
                // the page may ask for the processor only; without a word from it, the graphics chip is used when there is one
                var rlGpu = (_hw?.HasGpu ?? true) && !(msg.Args.TryGetProperty("gpu", out var rlg) && rlg.ValueKind == JsonValueKind.False);
                return await RunLocalAsync(rlRepo, rlFile, rlCtx, rlGpu);

            case "runList":
                return RunningList();

            case "runStop":
                return new
                {
                    ok = LocalRuns.Stop(
                        msg.Args.TryGetProperty("repo", out var rsr) ? rsr.GetString() ?? "" : "",
                        msg.Args.TryGetProperty("file", out var rsf) ? rsf.GetString() ?? "" : ""),
                };

            case "runOpen":
                var roRun = LocalRuns.Find(
                    msg.Args.TryGetProperty("repo", out var ror) ? ror.GetString() ?? "" : "",
                    msg.Args.TryGetProperty("file", out var rof) ? rof.GetString() ?? "" : "");
                if (roRun is { Ready: true }) Open(roRun.Url);
                return new { ok = roRun is { Ready: true } };

            // what else on this PC can run a file, and the exact command for it
            case "runTools":
                var rtRepo = msg.Args.TryGetProperty("repo", out var rtr) ? rtr.GetString() ?? "" : "";
                var rtFile = msg.Args.TryGetProperty("file", out var rtf) ? rtf.GetString() ?? "" : "";
                var rtPath = ModelStore.ResolvePath(rtRepo, rtFile);
                var rtEngine = Engine.Find();
                return new
                {
                    path = rtPath,
                    hasGpu = _hw?.HasGpu ?? true,
                    engine = rtEngine is not null,
                    serverPath = rtEngine?.ServerPath,
                    lms = LocalTools.Lms() is not null,
                    lmsMode = LocalTools.LmsImportMode(rtPath),
                    ollama = LocalTools.Ollama() is not null,
                };

            case "lmsImport":
                return await LmsImportAsync(
                    msg.Args.TryGetProperty("repo", out var lir) ? lir.GetString() ?? "" : "",
                    msg.Args.TryGetProperty("file", out var lif) ? lif.GetString() ?? "" : "", ct);

            // ---------- AI agents: one model behind an OpenAI- and Anthropic-style address ----------
            case "agentStatus":
                return AgentStatus();

            case "agentStart":
                if (_hw is null) await HardwareScanAsync(ct);
                return AgentStart(
                    msg.Args.TryGetProperty("repo", out var asr) ? asr.GetString() ?? "" : "",
                    msg.Args.TryGetProperty("file", out var asf) ? asf.GetString() ?? "" : "",
                    msg.Args.TryGetProperty("ctx", out var asc) && asc.TryGetInt32(out var ascv) ? ascv : 0,
                    !(msg.Args.TryGetProperty("gpu", out var asg) && asg.ValueKind == JsonValueKind.False));

            case "agentStop":
                AgentServer.Stop();
                return AgentStatus();

            // a new key; the old one stops working, so a running server is started again with it
            case "agentNewKey":
                AgentServer.NewKey();
                if (AgentServer.State is "on" or "loading" && AgentServer.Repo is { } nkRepo && AgentServer.File is { } nkFile)
                    AgentStart(nkRepo, nkFile, AgentServer.Asked, AgentServer.Gpu || AgentServer.OnCpuInstead);
                return AgentStatus();

            case "agentSetFit":
                var fitOn = !(msg.Args.TryGetProperty("on", out var afo) && afo.ValueKind == JsonValueKind.False);
                var fitSettings = AppSettings.Get();
                fitSettings.AgentFit = fitOn;
                AppSettings.Save(fitSettings);
                AgentServer.FitRequests = fitOn;
                return AgentStatus();

            // thinking is a llama-server setting: a running model is started again with the new choice
            case "agentSetThinking":
                var thinkOn = msg.Args.TryGetProperty("on", out var ato) && ato.ValueKind == JsonValueKind.True;
                var thinkSettings = AppSettings.Get();
                thinkSettings.AgentThinking = thinkOn;
                AppSettings.Save(thinkSettings);
                AgentServer.Thinking = thinkOn;
                if (AgentServer.State is "on" or "loading" && AgentServer.Repo is { } tkRepo && AgentServer.File is { } tkFile)
                    AgentStart(tkRepo, tkFile, AgentServer.Asked, AgentServer.Gpu || AgentServer.OnCpuInstead);
                return AgentStatus();

            case "agentTestList":
                return await Task.Run(AgentTestList, ct);

            case "agentTestRun":
                if (_hw is null) await HardwareScanAsync(ct);
                return StartAgentTest(
                    msg.Args.TryGetProperty("repo", out var atr) ? atr.GetString() ?? "" : "",
                    msg.Args.TryGetProperty("file", out var atf) ? atf.GetString() ?? "" : "");

            // the shared list: the user posts a filled-in GitHub issue in their browser; nothing is sent from here
            case "agentShare":
                if (_hw is null) await HardwareScanAsync(ct);
                var shRepo = msg.Args.TryGetProperty("repo", out var shr) ? shr.GetString() ?? "" : "";
                var shFile = msg.Args.TryGetProperty("file", out var shf) ? shf.GetString() ?? "" : "";
                if (Measurements.Find(shRepo, shFile) is not { AgentTotal: > 0 } shm)
                    return new { ok = false, error = "Test this model first." };
                var shUrl = AgentShare.IssueUrl(AgentShare.Payload(
                    typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "", _hw!.GpuName, _hw.GpuClass, _hw.TotalRamGb,
                    shRepo, shFile, shm.SizeGb, shm.AgentReadSeconds, shm.AgentWriteTps, shm.AgentCorrect, shm.AgentTotal,
                    shm.AgentPoints, shm.AgentThinking, shm.Engine));
                Open(shUrl);
                return new { ok = true };

            case "agentCommunity":
                if (_hw is null) await HardwareScanAsync(ct);
                return new { gpu = _hw!.GpuName, rows = AgentShare.ForGpu(await CommunityBoardAsync(ct), _hw.GpuName) };

            case "agentOpenLog":
                if (File.Exists(AgentServer.LogFile)) Open(AgentServer.LogFile);
                return new { ok = File.Exists(AgentServer.LogFile) };

            case "testQuality":
                if (_hw is null) await HardwareScanAsync(ct);
                var tqRepo = msg.Args.TryGetProperty("repo", out var tqr) ? tqr.GetString() ?? "" : "";
                var tqFile = msg.Args.TryGetProperty("file", out var tqf) ? tqf.GetString() ?? "" : "";
                return StartQuality(tqRepo, tqFile);

            // ---------- share: a picture of a battle ----------
            case "saveImage":
                var imgName = msg.Args.TryGetProperty("name", out var inm) ? inm.GetString() ?? "battle.png" : "battle.png";
                var imgData = msg.Args.TryGetProperty("dataUrl", out var idu) ? idu.GetString() ?? "" : "";
                return SaveImage(imgName, imgData);

            // ---------- share: speeds for the llmfit community ----------
            case "sharePrepare":
                if (_hw is null) await HardwareScanAsync(ct);
                return await SharePrepareAsync();

            case "shareSend":
                return await ShareSendAsync();

            case "shareCancel":
                try { _shareCts?.Cancel(); } catch { }
                return new { ok = true };

            case "cancelMeasure":
                try { _benchCts?.Cancel(); } catch { }
                return new { ok = true };

            case "clearMeasurements":
                return new { cleared = Measurements.Clear() };

            case "dumpDom":
                return await Web.ExecuteScriptAsync(
                    "({ body: document.body.innerText.slice(0, 4000), html: document.body.innerHTML.length })");

            default:
                throw new InvalidOperationException($"unknown command '{msg.Cmd}'");
        }
    }

    // ---- real speed --------------------------------------------------------
    private CancellationTokenSource? _benchCts;
    private string? _benchKey;

    /// <summary>
    /// Runs one downloaded file in llama.cpp and keeps what it did. One at a time:
    /// two models loaded together would measure each other. Progress is pushed as "bench".
    /// </summary>
    private object StartMeasure(string repo, string file)
    {
        if (_benchKey is not null)
            return new { started = false, error = "Another model is being measured. Wait for it to finish." };
        if (!ModelStore.HasFile(repo, file))
            return new { started = false, error = "Download this file first: a real measurement runs it." };
        var engine = Engine.Find();
        if (engine is null) return new { started = false, needEngine = true };

        var key = $"{repo}/{file}";
        var cts = new CancellationTokenSource();
        _benchKey = key;
        _benchCts = cts;
        var hw = _hw!;
        _ = Task.Run(async () =>
        {
            try
            {
                var path = ModelStore.ResolvePath(repo, file);
                var result = await Bench.RunAsync(engine.ServerPath, path, hw.HasGpu,
                    (phase, run) => Push("bench", new { key, repo, file, phase, run, runs = Bench.Runs }), cts.Token);

                var sizeGb = WholeModelBytes(path) / 1e9;
                var gbPerSec = await MachineSpeed.GbPerSecAsync(hw);
                Measurements.Save(new Measurement
                {
                    Repo = repo,
                    File = file,
                    SizeGb = Math.Round(sizeGb, 2),
                    GenTps = result.GenTps,
                    PromptTps = result.PromptTps,
                    FirstTokenMs = result.FirstTokenMs,
                    LoadSeconds = result.LoadSeconds,
                    RawTps = gbPerSec > 0 && sizeGb > 0 ? Math.Round(gbPerSec / sizeGb, 1) : 0,
                    Fallback = result.Fallback,
                    GpuName = hw.GpuName,
                    Engine = engine.Build,
                    At = DateTime.Now.ToString("s"),
                });
                Push("bench", new
                {
                    key, repo, file, phase = "done",
                    genTps = result.GenTps,
                    promptTps = result.PromptTps,
                    firstTokenMs = result.FirstTokenMs,
                    loadSeconds = result.LoadSeconds,
                    fallback = result.Fallback,
                });
            }
            catch (OperationCanceledException)
            {
                Push("bench", new { key, repo, file, phase = "cancelled" });
            }
            catch (Exception ex)
            {
                Push("bench", new { key, repo, file, phase = "failed", error = ex.Message });
            }
            finally
            {
                _benchKey = null;
                _benchCts = null;
                cts.Dispose();
            }
        });
        return new { started = true, key };
    }

    /// <summary>
    /// Runs the quality test on one downloaded file and keeps the score. It shares the
    /// one-at-a-time lock with the speed measurement; progress is pushed as "bench" with kind "quality".
    /// </summary>
    private object StartQuality(string repo, string file)
    {
        if (_benchKey is not null)
            return new { started = false, error = "Another model is being measured. Wait for it to finish." };
        if (!ModelStore.HasFile(repo, file))
            return new { started = false, error = "Download this file first: the test runs it." };
        var engine = Engine.Find();
        if (engine is null) return new { started = false, needEngine = true };

        var key = $"{repo}/{file}";
        var cts = new CancellationTokenSource();
        _benchKey = key;
        _benchCts = cts;
        var hw = _hw!;
        const string kind = "quality";
        _ = Task.Run(async () =>
        {
            try
            {
                var path = ModelStore.ResolvePath(repo, file);
                var total = QualityQuestions.All.Count;
                var result = await QualityTest.RunAsync(engine.ServerPath, path, hw.HasGpu,
                    (phase, run) => Push("bench", new { key, repo, file, kind, phase, run, runs = total }), cts.Token);
                Measurements.SaveQuality(repo, file, Math.Round(WholeModelBytes(path) / 1e9, 2), result, hw.GpuName, engine.Build);
                Push("bench", new
                {
                    key, repo, file, kind, phase = "done",
                    quality = result.Score,
                    correct = result.Correct,
                    total = result.Total,
                    parts = result.Parts,
                });
            }
            catch (OperationCanceledException)
            {
                Push("bench", new { key, repo, file, kind, phase = "cancelled" });
            }
            catch (Exception ex)
            {
                Push("bench", new { key, repo, file, kind, phase = "failed", error = ex.Message });
            }
            finally
            {
                _benchKey = null;
                _benchCts = null;
                cts.Dispose();
            }
        });
        return new { started = true, key };
    }

    // ---- the shared "Best for agents" list -------------------------------------------
    private System.Text.Json.Nodes.JsonArray? _board;
    private DateTime _boardAt;

    /// <summary>community/agents.json from the app's repository, kept for an hour; null when it cannot be read (or does not exist yet).</summary>
    private async Task<System.Text.Json.Nodes.JsonArray?> CommunityBoardAsync(CancellationToken ct)
    {
        if (_board is not null && (DateTime.UtcNow - _boardAt).TotalHours < 1) return _board;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ModelFitStudio");
            var text = await http.GetStringAsync(AgentShare.BoardUrl, ct);
            _board = System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonArray;
            _boardAt = DateTime.UtcNow;
        }
        catch { }
        return _board;
    }

    // ---- updates ------------------------------------------------------------------
    private object? _updates;
    private DateTime _updatesAt;

    /// <summary>The newest app release and llama.cpp build against what this PC has; kept for 12 hours unless forced.</summary>
    private async Task<object> UpdatesCheckAsync(bool force, CancellationToken ct)
    {
        if (!force && _updates is not null && (DateTime.UtcNow - _updatesAt).TotalHours < 12) return _updates;
        if (_hw is null) await HardwareScanAsync(ct);
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        var app = await Updates.LatestAppAsync(version, ct);

        var engine = Engine.Find();
        string? latest = null;
        long bytes = 0;
        try
        {
            if (engine?.Nvidia == true && NvidiaEngine.CudaFor(_hw!.GpuName, _hw.NvidiaDriver) is { } cuda)
            {
                var nv = await Engine.NvidiaLatestAsync(cuda, ct);
                latest = nv.Build;
                bytes = nv.Assets.Sum(a => a.Bytes);
            }
            else
            {
                var l = await Engine.LatestAsync(_hw!.HasGpu, ct);
                latest = l.Build;
                bytes = l.Bytes;
            }
        }
        catch { /* offline, or GitHub's rate limit: no answer this time */ }

        _updates = new
        {
            app = new { current = version, latest = app?.Tag, url = app?.Url, newer = app?.Newer == true },
            engine = new
            {
                current = engine?.Build ?? "",
                latest,
                mb = Math.Round(bytes / 1e6),
                newer = latest is not null && Engine.BuildNumber(latest) > Engine.BuildNumber(engine?.Build),
                nvidia = engine?.Nvidia == true,
            },
        };
        _updatesAt = DateTime.UtcNow;
        return _updates;
    }

    /// <summary>
    /// "Best for agents": runs one downloaded file as the Agents server would and keeps how it did.
    /// Shares the one-at-a-time lock with the other measurements; progress is pushed as "agentTest".
    /// </summary>
    private object StartAgentTest(string repo, string file)
    {
        if (_benchKey is not null)
            return new { started = false, error = "Another model is being measured. Wait for it to finish." };
        if (!ModelStore.HasFile(repo, file))
            return new { started = false, error = "Download this file first: the test runs it." };
        var engine = Engine.Find();
        if (engine is null) return new { started = false, needEngine = true, error = "llama.cpp is not on this PC yet." };

        var key = $"{repo}/{file}";
        var cts = new CancellationTokenSource();
        _benchKey = key;
        _benchCts = cts;
        var hw = _hw!;
        var thinking = AgentServer.Thinking;
        _ = Task.Run(async () =>
        {
            try
            {
                var path = ModelStore.ResolvePath(repo, file);
                var result = await AgentTest.RunAsync(engine.ServerPath, path, file, hw.HasGpu, hw.GpuClass == SpeedEstimate.Integrated, thinking,
                    (phase, n) => Push("agentTest", new { key, repo, file, phase, n, total = AgentCases.All.Length }), cts.Token);
                Measurements.SaveAgent(repo, file, Math.Round(WholeModelBytes(path) / 1e9, 2), result, thinking, hw.GpuName, engine.Build);
                Push("agentTest", new
                {
                    key, repo, file, phase = "done",
                    readSeconds = result.ReadSeconds,
                    writeTps = result.WriteTps,
                    correct = result.Correct,
                    total = result.Total,
                    points = result.Points,
                    fallback = result.Fallback,
                });
            }
            catch (OperationCanceledException)
            {
                Push("agentTest", new { key, repo, file, phase = "cancelled" });
            }
            catch (Exception ex)
            {
                Push("agentTest", new { key, repo, file, phase = "failed", error = ex.Message });
            }
            finally
            {
                _benchKey = null;
                _benchCts = null;
                cts.Dispose();
            }
        });
        return new { started = true, key };
    }

    /// <summary>The downloaded models that can be tested, with what each scored for agents so far.</summary>
    private static object AgentTestList() => ModelStore.ListInstalled()
        .Where(f => f.Complete && !RepoFiles.IsHelper(f.File, f.Bytes / 1e9))
        .Select(f =>
        {
            var m = Measurements.Find(f.Repo, f.File);
            return new
            {
                repo = f.Repo,
                file = f.File,
                bytes = f.Bytes,
                tested = m is { AgentTotal: > 0 },
                readSeconds = m?.AgentReadSeconds ?? 0,
                writeTps = m?.AgentWriteTps ?? 0,
                correct = m?.AgentCorrect ?? 0,
                total = m?.AgentTotal ?? 0,
                points = m?.AgentPoints ?? 0,
                thinking = m?.AgentThinking ?? false,
            };
        })
        .ToList();

    /// <summary>Seconds the chosen model takes here to read 12,000 tokens: live while serving, else from its Best-for-agents test; 0 when never measured.</summary>
    private static double AgentRead12k(AppSettingsData s) =>
        AgentServer.State != "off" && AgentServer.Read12kFor(AgentServer.ModelId) is > 0 and var live ? live
            : Measurements.Find(AgentServer.Repo ?? s.AgentRepo, AgentServer.File ?? s.AgentFile)?.AgentReadSeconds ?? 0;

    // ---- AI agents ------------------------------------------------------------    /// <summary>What the Agents tab shows: the state, the two addresses, the key, the model's name and what it is doing.</summary>
    private static object AgentStatus()
    {
        var s = AppSettings.Get();
        var port = AgentServer.State == "off" ? s.AgentPort : AgentServer.Port;
        var a = AgentServer.Activity;
        return new
        {
            state = AgentServer.State,
            error = AgentServer.Error,
            repo = AgentServer.Repo ?? s.AgentRepo,
            file = AgentServer.File ?? s.AgentFile,
            modelId = AgentServer.State == "off"
                ? (s.AgentFile is { } f ? AgentPlan.ModelId(f) : "")
                : AgentServer.ModelId,
            // asked: the chip (0 = Auto); context: the length the server really has, 0 until it is known
            asked = AgentServer.State == "off" ? AgentPlan.ClampContext(s.AgentLength) : AgentServer.Asked,
            context = AgentServer.State == "on" ? AgentServer.Context : 0,
            // every model agents can ask for, the one in memory, and how long a request to the chosen one is kept
            models = AgentServer.State == "off" ? [] : AgentServer.ModelIds,
            loaded = AgentServer.Loaded,
            // this PC's own limit: from the chosen model's measured reading speed (Best for agents, or seen while serving)
            targetTokens = RequestFitter.TargetTokens(AgentServer.State == "on" ? AgentServer.Context : 0, AgentRead12k(s)),
            read12k = AgentRead12k(s),
            onCpu = AgentServer.OnCpuInstead,
            drafting = AgentServer.State == "on" && AgentServer.Drafting,
            fitOn = AgentServer.FitRequests,
            thinking = AgentServer.Thinking,
            lastFit = AgentServer.LastFit is { } lf ? new
            {
                fromTokens = lf.FromTokens,
                context = lf.Context,
                toolsBefore = lf.Report.ToolsBefore,
                toolsAfter = lf.Report.ToolsAfter,
                turnsDropped = lf.Report.TurnsDropped,
                stepsDropped = lf.Report.StepsDropped,
                outputsCut = lf.Report.OutputsCut,
                instructionsCut = lf.Report.InstructionsCut,
                answered = lf.Answered,
            } : null,
            port,
            key = AgentServer.Key,
            openai = $"http://127.0.0.1:{port}/v1",
            anthropic = $"http://127.0.0.1:{port}",
            activity = new
            {
                phase = a.Phase,
                requests = a.Requests,
                progress = a.Progress,
                readSoFar = a.ReadSoFar,
                lastRead = a.LastRead,
                lastReadSeconds = a.LastReadSeconds,
                lastWritten = a.LastWritten,
                lastTps = a.LastTps,
                tooBigTokens = a.TooBigTokens,
                tooBigContext = a.TooBigContext,
            },
        };
    }

    /// <summary>Starts serving one downloaded model; the page follows the "agent" pushes while it loads.</summary>
    private object AgentStart(string repo, string file, int ctx, bool gpu)
    {
        if (!ModelStore.HasFile(repo, file)) return new { ok = false, error = "Download this file first." };
        var engine = Engine.Find();
        if (engine is null) return new { ok = false, error = "llama.cpp is not on this PC yet. Install it in Settings." };

        var s = AppSettings.Get();
        s.AgentRepo = repo;
        s.AgentFile = file;
        s.AgentLength = AgentPlan.ClampContext(ctx);
        AppSettings.Save(s);

        var hw = _hw;
        var onGpu = gpu && (hw?.HasGpu ?? true);
        var integrated = hw?.GpuClass == SpeedEstimate.Integrated;
        _ = Task.Run(async () =>
        {
            try
            {
                // every downloaded chat model is offered; agents load another by naming it
                var offers = ModelStore.ListInstalled()
                    .Where(f => f.Complete && !RepoFiles.IsHelper(f.File, f.Bytes / 1e9) && AgentPlan.LooksLikeChat(f.Repo + "/" + f.File))
                    .Select(f => new AgentServer.Offer(f.Repo, f.File, ModelStore.ResolvePath(f.Repo, f.File),
                        Measurements.Find(f.Repo, f.File)?.AgentReadSeconds ?? 0))
                    .ToList();
                await AgentServer.StartAsync(engine.ServerPath, onGpu ? Engine.FallbackFor(engine.ServerPath) : null,
                    offers, repo, file, s.AgentLength, onGpu, integrated, s.AgentPort);
            }
            catch (Exception ex)
            {
                AgentServer.Stop();
                Push("toast", new { text = "The server did not start: " + ex.Message, error = true });
            }
        });
        return new { ok = true };
    }

    private static object RunningList() => LocalRuns.List().Select(r => new
    {
        repo = r.Repo,
        file = r.File,
        url = r.Url,
        ready = r.Ready,
        context = r.Context,
        gpu = r.Gpu,
    }).ToList();

    /// <summary>
    /// Starts a downloaded model in llama.cpp's own server, in a terminal window the
    /// user can see and close, and opens its chat page in the browser. Closing that
    /// window stops the model; the app remembers the ones still alive so the Library
    /// can open their chat page again or stop them.
    /// </summary>
    private async Task<object> RunLocalAsync(string repo, string file, int ctx, bool gpu)
    {
        if (!ModelStore.HasFile(repo, file)) return new { ok = false, error = "Download this file first." };
        if (LocalRuns.Find(repo, file) is { Ready: true } already)
        {
            Open(already.Url);
            return new { ok = true, url = already.Url };
        }
        var engine = Engine.Find();
        if (engine is null) return new { ok = false, needEngine = true, error = "llama.cpp is not on this PC yet. Install it in Settings." };

        LocalRun run;
        var server = engine.ServerPath;
        // NVIDIA's build stops at once on a card or driver it does not support; the built-in one is tried then
        var builtIn = gpu ? Engine.FallbackFor(server) : null;
        try
        {
            run = LocalRuns.Start(server, ModelStore.ResolvePath(repo, file), repo, file, ctx, gpu);
        }
        catch (Exception ex)
        {
            return new { ok = false, error = "llama.cpp did not start: " + ex.Message };
        }

        // the chat page exists once the weights are loaded
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (DateTime.UtcNow < deadline)
        {
            if (LocalRuns.Find(repo, file) != run && builtIn is not null)
            {
                try
                {
                    run = LocalRuns.Start(builtIn, ModelStore.ResolvePath(repo, file), repo, file, ctx, gpu);
                    builtIn = null;
                    continue;
                }
                catch (Exception ex)
                {
                    return new { ok = false, error = "llama.cpp did not start: " + ex.Message };
                }
            }
            if (LocalRuns.Find(repo, file) != run)
                return new { ok = false, error = "The model stopped before it finished loading. It may be too big for this PC's memory, or not a chat model." };
            try
            {
                using var r = await ModelServer.Http.GetAsync(run.Url + "/health");
                if (r.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    LocalRuns.MarkReady(run);
                    Open(run.Url);
                    return new { ok = true, url = run.Url };
                }
            }
            catch (System.Net.Http.HttpRequestException) { }
            await Task.Delay(700);
        }
        return new { ok = false, error = "The model did not finish loading. Look at the llama.cpp window for the reason." };
    }

    /// <summary>
    /// Adds a downloaded file to LM Studio's own list with LM Studio's command-line tool.
    /// On one drive it is a hard link, which costs no disk space; across drives it is a copy.
    /// </summary>
    private static async Task<object> LmsImportAsync(string repo, string file, CancellationToken ct)
    {
        if (!ModelStore.HasFile(repo, file)) return new { ok = false, error = "Download this file first." };
        var lms = LocalTools.Lms();
        if (lms is null) return new { ok = false, error = "LM Studio's command-line tool is not on this PC." };
        var path = ModelStore.ResolvePath(repo, file);
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = lms,
                Arguments = $"import \"{path}\" {LocalTools.LmsImportMode(path)} --user-repo \"{repo}\" -y",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(10));   // a copy of a large file takes a while
            await p.WaitForExitAsync(limit.Token);
            var said = ((await stdout) + "\n" + (await stderr)).Trim();
            return p.ExitCode == 0
                ? new { ok = true, error = "" }
                : new { ok = false, error = said.Length > 0 ? said.Split('\n')[^1].Trim() : "LM Studio did not take the file." };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = "LM Studio's tool did not run: " + ex.Message };
        }
    }

    // ---- share ---------------------------------------------------------------
    /// <summary>Writes a picture the page drew (a battle result) where the user says, and puts it on the clipboard.</summary>
    private object SaveImage(string name, string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (!dataUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal) || comma < 0)
            return new { ok = false, error = "No picture was given." };
        var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the battle as a picture",
            FileName = string.Join("_", name.Split(Path.GetInvalidFileNameChars())),
            Filter = "PNG picture (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true) return new { ok = false, cancelled = true };
        File.WriteAllBytes(dlg.FileName, bytes);

        var copied = false;
        try
        {
            using var ms = new MemoryStream(bytes);
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(ms,
                System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            System.Windows.Clipboard.SetImage(frame);
            copied = true;
        }
        catch { /* the file is saved; the clipboard is a convenience */ }
        return new { ok = true, path = dlg.FileName, copied };
    }

    private CancellationTokenSource? _shareCts;

    /// <summary>llmfit keeps what it measures for sharing here: apart from its own default store, so the app's estimates are not bent by it.</summary>
    private static string ShareStore => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelFitStudio", "llmfit-bench");

    private static Dictionary<string, string> ShareEnv() => new() { ["LLMFIT_BENCH_STORE"] = ShareStore };

    /// <summary>
    /// Step one of sharing: llmfit's own benchmark runs on every downloaded model the
    /// app has measured (the community compares like with like, so it has to be
    /// llmfit's run), then llmfit prints exactly what it would send. Nothing leaves
    /// the machine here. Lines are pushed as "share".
    /// </summary>
    private async Task<object> SharePrepareAsync()
    {
        if (_benchKey is not null) return new { ok = false, error = "A model is being measured. Wait for it to finish." };
        var engine = Engine.Find();
        if (engine is null) return new { ok = false, needEngine = true };
        var files = Measurements.All().Where(m => m.GenTps > 0 && !m.Fallback && ModelStore.HasFile(m.Repo, m.File)).ToList();
        if (files.Count == 0)
            return new { ok = false, error = "Measure the speed of a downloaded model first: there is nothing to share yet." };

        var hw = _hw!;
        var cts = new CancellationTokenSource();
        _shareCts = cts;
        _benchKey = "share";
        void Say(string line) => Push("share", new { line });
        try
        {
            Directory.CreateDirectory(ShareStore);
            // only this run is offered: an earlier preview that was never sent is dropped
            var pending = Path.Combine(ShareStore, "pending");
            if (Directory.Exists(pending))
                foreach (var old in Directory.EnumerateFiles(pending, "*.json"))
                    try { File.Delete(old); } catch { }
            var done = 0;
            foreach (var m in files)
            {
                Say($"Benchmarking {m.File} with llmfit…");
                await using var server = await ModelServer.StartAsync(engine.ServerPath, ModelStore.ResolvePath(m.Repo, m.File),
                    hw.HasGpu, null, cts.Token);
                if (server.Fallback)
                {
                    // a CPU run filed under this GPU's name would mislead everyone who reads it
                    Say($"  skipped: the GPU could not load {m.File}.");
                    continue;
                }
                var json = new System.Text.StringBuilder();
                var code = await LlmfitRunner.RunStreamAsync(
                    $"bench --provider llamacpp --url {server.BaseUrl} --runs 3 --json --no-dashboard",
                    ShareEnv(), l => json.AppendLine(l), 20 * 60_000, cts.Token);
                var tps = System.Text.RegularExpressions.Regex.Match(json.ToString(), "\"avg_tps\":\\s*([0-9.]+)");
                if (code == 0 && tps.Success)
                {
                    done++;
                    Say($"  {double.Parse(tps.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture):0.0} tok/s");
                }
                else Say("  llmfit could not benchmark it.");
            }
            if (done == 0) return new { ok = false, error = "No model could be benchmarked for sharing." };

            Say("");
            await LlmfitRunner.RunStreamAsync("bench --share --dry-run --no-dashboard", ShareEnv(),
                // the preview names the local file it read; that path is not part of what is sent
                l => { if (!l.TrimStart().StartsWith("---", StringComparison.Ordinal)) Say(l); }, 60_000, cts.Token);
            return new { ok = true, models = done };
        }
        catch (OperationCanceledException)
        {
            return new { ok = false, cancelled = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
        finally
        {
            _benchKey = null;
            _shareCts = null;
            cts.Dispose();
        }
    }

    /// <summary>
    /// Step two, only on the user's click: llmfit opens a pull request on GitHub with
    /// what the preview showed. It signs in with GitHub's device flow, so it prints a
    /// code and a link; every line it writes goes to the page as it comes.
    /// </summary>
    private async Task<object> ShareSendAsync()
    {
        var cts = new CancellationTokenSource();
        _shareCts = cts;
        try
        {
            var code = await LlmfitRunner.RunStreamAsync("bench --share --yes --no-dashboard", ShareEnv(),
                l => Push("share", new { line = l }), 20 * 60_000, cts.Token);
            return new { ok = code == 0, exitCode = code };
        }
        catch (OperationCanceledException)
        {
            return new { ok = false, cancelled = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
        finally
        {
            _shareCts = null;
            cts.Dispose();
        }
    }

    /// <summary>A model split into parts is read as a whole, so its size is all of them.</summary>
    private static long WholeModelBytes(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*)-\d{5}-of-(\d{5})\.gguf$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!m.Success) return new FileInfo(path).Length;
            return Directory.EnumerateFiles(Path.GetDirectoryName(path)!, m.Groups[1].Value + "-*-of-" + m.Groups[2].Value + ".gguf")
                .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    // ---- downloads ---------------------------------------------------------
    private readonly Dictionary<string, CancellationTokenSource> _dlCancel = new();

    /// <summary>Queues one file. Progress is pushed to the UI as it moves.</summary>
    private void QueueDownload(string repo, string file, long expectedBytes)
    {
        var key = $"{repo}/{file}";
        lock (_downloads)
        {
            // already in flight — nothing to do
            if (_downloads.Any(d => $"{d.Repo}/{d.File}" == key &&
                                    d.State is DownloadState.Queued or DownloadState.Downloading))
                return;
            // paused/failed/cancelled from earlier — a fresh "get" continues it
            if (_downloads.Any(d => $"{d.Repo}/{d.File}" == key &&
                                    d.State is DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled))
            {
                ResumeDownload(key); // thread-safe: locks + Push marshal themselves
                return;
            }
        }

        var progress = new DownloadProgress { Repo = repo, File = file, Total = expectedBytes, State = DownloadState.Queued };
        lock (_downloads) _downloads.Add(progress);
        Push("download", ToDownloadJson(progress));
        SaveQueue();
        StartWorker(progress);
    }

    /// <summary>Runs one row through the download pump. Shared by queue and resume.</summary>
    private void StartWorker(DownloadProgress progress)
    {
        var key = $"{progress.Repo}/{progress.File}";
        var cts = new CancellationTokenSource();
        lock (_dlCancel) _dlCancel[key] = cts;

        _ = Task.Run(async () =>
        {
            // one at a time: Hugging Face throttles parallel reads from one IP,
            // and a single stream is faster than four
            var entered = false;
            using var reporting = new CancellationTokenSource();
            var updates = Task.CompletedTask;
            try
            {
                await _downloadPump.WaitAsync(cts.Token);
                entered = true;
                progress.State = DownloadState.Downloading;
                Push("download", ToDownloadJson(progress));
                updates = ReportDownloadAsync(progress, reporting.Token);
                await ModelStore.DownloadAsync(progress.Repo, progress.File, progress, cts.Token);
            }
            catch (OperationCanceledException)
            {
                progress.State = progress.PauseRequested ? DownloadState.Paused : DownloadState.Cancelled;
                progress.MbPerSec = 0;
            }
            catch (Exception ex)
            {
                progress.State = DownloadState.Failed;
                progress.Error = ex.Message;
                progress.MbPerSec = 0;
            }
            finally
            {
                reporting.Cancel();
                await updates;
                if (entered) _downloadPump.Release();
                lock (_dlCancel)
                {
                    if (_dlCancel.TryGetValue(key, out var current) && ReferenceEquals(current, cts))
                        _dlCancel.Remove(key);
                }
                cts.Dispose();
            }
            Push("download", ToDownloadJson(progress));
            SaveQueue();
            if (progress.State == DownloadState.Done)
            {
                Push("installed", new { repo = progress.Repo, file = progress.File });
            }
        });
    }

    private async Task ReportDownloadAsync(DownloadProgress progress, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                Push("download", ToDownloadJson(progress));
        }
        catch (OperationCanceledException) { }
    }

    private bool PauseDownload(string key)
    {
        DownloadProgress? row;
        lock (_downloads)
            row = _downloads.FirstOrDefault(d => $"{d.Repo}/{d.File}" == key &&
                                                 d.State is DownloadState.Downloading or DownloadState.Queued);
        if (row is null) return false;
        row.PauseRequested = true;

        CancellationTokenSource? cts;
        lock (_dlCancel) cts = _dlCancel.TryGetValue(key, out var c) ? c : null;
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { } // the transfer loop turns this into Paused
        }
        else
        {
            row.State = DownloadState.Paused; // still waiting on the pump
            Push("download", ToDownloadJson(row));
            SaveQueue();
        }
        return true;
    }

    private bool ResumeDownload(string key)
    {
        DownloadProgress? row;
        lock (_downloads)
            row = _downloads.FirstOrDefault(d => $"{d.Repo}/{d.File}" == key &&
                                                 d.State is DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled);
        if (row is null) return false;
        row.PauseRequested = false;
        row.Error = null;
        row.Note = null;
        row.MbPerSec = 0;
        row.Received = ModelStore.PartialBytes(row.Repo, row.File); // trust the disk, not the old counter
        row.State = DownloadState.Queued;
        Push("download", ToDownloadJson(row));
        SaveQueue();
        StartWorker(row);
        return true;
    }

    private int ResumeAllDownloads()
    {
        List<DownloadProgress> targets;
        lock (_downloads)
            targets = _downloads.Where(d => d.State is DownloadState.Paused or DownloadState.Failed or DownloadState.Cancelled).ToList();
        foreach (var t in targets) ResumeDownload($"{t.Repo}/{t.File}");
        return targets.Count;
    }

    private bool CancelDownload(string key)
    {
        CancellationTokenSource? cts;
        lock (_dlCancel) cts = _dlCancel.TryGetValue(key, out var c) ? c : null;
        if (cts is null) return false;
        try { cts.Cancel(); } catch { }
        return true;
    }

    /// <summary>Removes the row from the queue and deletes its partial file.</summary>
    private bool DiscardDownload(string key)
    {
        DownloadProgress? row;
        lock (_downloads) row = _downloads.FirstOrDefault(d => $"{d.Repo}/{d.File}" == key);
        if (row is null) return false;

        if (row.State is DownloadState.Downloading or DownloadState.Queued)
        {
            CancellationTokenSource? cts;
            lock (_dlCancel) cts = _dlCancel.TryGetValue(key, out var c) ? c : null;
            try { cts?.Cancel(); } catch { }
        }
        ModelStore.DiscardPartial(row.Repo, row.File);
        lock (_downloads) _downloads.Remove(row);
        Push("downloadRemoved", new { key });
        SaveQueue();
        return true;
    }

    private static string QueuePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModelFitStudio", "download-queue.json");

    /// <summary>
    /// Writes the live queue to disk so a closed (or crashed) app can pick the
    /// unfinished pulls back up — the .part files hold the actual bytes.
    /// </summary>
    private void SaveQueue()
    {
        try
        {
            object[] rows;
            lock (_downloads)
                rows = [.. _downloads
                    .Where(d => d.State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Paused)
                    .Select(d => (object)new
                    {
                        repo = d.Repo,
                        file = d.File,
                        total = d.Total,
                        active = d.State is DownloadState.Downloading or DownloadState.Queued,
                    })];
            Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
            File.WriteAllText(QueuePath, JsonSerializer.Serialize(rows));
        }
        catch { }
    }

    /// <summary>Restores the queue after launch; rows that were mid-flight continue.</summary>
    private void RestoreQueue()
    {
        try
        {
            if (!File.Exists(QueuePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(QueuePath));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var repo = e.TryGetProperty("repo", out var r) ? r.GetString() ?? "" : "";
                var file = e.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
                if (repo.Length == 0 || file.Length == 0) continue;
                if (ModelStore.HasFile(repo, file)) continue; // finished before the app closed
                var total = e.TryGetProperty("total", out var t) && t.TryGetInt64(out var tv) ? tv : 0;
                var active = e.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
                var have = ModelStore.PartialBytes(repo, file);
                if (total <= 0 && have <= 0) continue; // nothing on disk worth restoring
                var row = new DownloadProgress
                {
                    Repo = repo,
                    File = file,
                    Total = total,
                    Received = have,
                    State = active ? DownloadState.Queued : DownloadState.Paused,
                    Note = have > 0 ? $"resuming {ModelStore.Fbytes(have)} saved" : null,
                };
                lock (_downloads) _downloads.Add(row);
                Push("download", ToDownloadJson(row));
                if (active) StartWorker(row); // it was mid-flight — continue it
            }
        }
        catch { }
    }

    private object DownloadSnapshot()
    {
        lock (_downloads) return new { files = _downloads.Select(ToDownloadJson).ToArray() };
    }

    private static object ToDownloadJson(DownloadProgress d) => new
    {
        key = $"{d.Repo}/{d.File}",
        repo = d.Repo,
        file = d.File,
        state = d.State.ToString(),
        received = d.Received,
        total = d.Total,
        fraction = d.Fraction,
        mbPerSec = d.MbPerSec,
        error = d.Error,
        note = d.Note,
        path = d.Path,
    };

    private static object ToHardwareJson(HardwareInfo hw) => new    {
        hw = new
        {
            cpuName = hw.CpuName,
            cpuCores = hw.CpuCores,
            totalRamGb = hw.TotalRamGb,
            freeRamGb = hw.FreeRamGb,
            gpuName = hw.GpuName,
            hasGpu = hw.HasGpu,
            gpuCount = hw.GpuCount,
            unified = hw.UnifiedMemory,
            gpuClass = hw.GpuClass,
            isNvidia = hw.IsNvidia,
            backend = hw.Backend,
            budgetGb = hw.BudgetGb,
            // past the budget a model still runs, slower, up to this much
            ceilingGb = Math.Round(FitRule.LimitsFor(hw.BudgetGb, hw.TotalRamGb).CeilingGb * FitRule.CeilingShare, 1),
            detectedVramGb = hw.DetectedVramGb,
            poolGb = hw.PoolGb,
            poolFreeGb = hw.PoolFreeGb,
            vramCorrected = hw.VramCorrected,
            correctedFrom = hw.CorrectedFrom,
        },
        notes = hw.Notes,
    };

    private async Task HardwareScanAsync(CancellationToken ct)
    {
        var (hw, err) = await HardwareScanner.ScanAsync(ct);
        if (hw is null) throw new InvalidOperationException(err ?? "hardware scan failed");
        _hw = hw;
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    // ---- title bar ---------------------------------------------------------
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaxButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    // the X hides to the tray when that setting is on
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppSettings.Get().MinimizeToTrayOnClose)
            MinimizeToTray();
        else
            QuitFromTray();
    }

    private bool _trayNotified;
    private void MinimizeToTray()
    {
        if (_tray is null) // no tray available (rare) — fall back to a real close
        {
            _quitRequested = true;
            Close();
            return;
        }
        HideToTray(!_trayNotified);
        _trayNotified = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Alt+F4, taskbar close, or any other close path also go to the tray
        // unless the user explicitly chose Quit from the tray menu.
        if (!_quitRequested && AppSettings.Get().MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        ModelServer.KillCurrent();
        AgentServer.Stop();
        try { Web?.Dispose(); } catch { }
        base.OnClosed(e);
        System.Windows.Application.Current?.Shutdown();
    }
}
