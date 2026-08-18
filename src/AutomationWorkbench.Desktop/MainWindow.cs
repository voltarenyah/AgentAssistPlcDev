using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutomationWorkbench.Desktop;

public sealed class MainWindow : Form
{
    // Custom chrome over a sizable frame: the window keeps WS_THICKFRAME and
    // the min/max boxes so Windows snap assist, edge snapping, DWM shadow, and
    // native move/resize loops keep working, while WM_NCCALCSIZE removes the
    // visible caption and frame. Studio renders the caption (drag area and the
    // minimize/maximize/close buttons) inside its own header and drives the
    // window through WebView2 messages handled by ApplyWindowCommand. Because
    // the WebView2 child window covers the whole client area, edge resizing is
    // initiated from Studio (pointer near the viewport edge) via "begin-resize"
    // and completed by a native modal resize loop here.
    private const int WmNcCalcSize = 0x0083;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HitCaption = 2;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const uint MonitorDefaultToNearest = 2;

    // The restored (non-maximized) window must be large enough to fit the
    // whole Studio layout; users can shrink it afterwards.
    private const int MinimumUsableNormalWidth = 1200;
    private const int MinimumUsableNormalHeight = 760;

    private readonly BackendProcessHost backend;
    private readonly RuntimePaths paths;
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill };
    private readonly Panel startupSurface = new()
    {
        Name = "startupSurface",
        Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(247, 249, 252),
    };
    private readonly Label startupMessage = new()
    {
        Name = "startupMessage",
        Dock = DockStyle.Fill,
        Text = "Starting Automation Workbench…\r\nPreparing the local application window.",
        TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 16F, FontStyle.Regular),
        ForeColor = Color.FromArgb(32, 44, 58),
    };
    private bool startupStarted;
    private bool shutdownStarted;
    private FormWindowState lastNotifiedWindowState;

    public MainWindow(BackendProcessHost backend, RuntimePaths paths)
    {
        this.backend = backend;
        this.paths = paths;
        Text = "Automation Workbench";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        Size = DefaultNormalSize(Screen.PrimaryScreen.WorkingArea);
        MinimumSize = new Size(960, 640);
        WindowState = FormWindowState.Maximized;
        ShowInTaskbar = true;
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "AutomationWorkbench.ico")))
            Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "AutomationWorkbench.ico"));

        browser.Visible = false;
        startupSurface.Controls.Add(startupMessage);
        startupSurface.MouseDown += OnDragSurfaceMouseDown;
        startupMessage.MouseDown += OnDragSurfaceMouseDown;
        Controls.Add(browser);
        Controls.Add(startupSurface);
        browser.NavigationStarting += OnNavigationStarting;
        lastNotifiedWindowState = WindowState;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (startupStarted)
            return;
        startupStarted = true;
        _ = StartApplicationAsync();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (shutdownStarted)
        {
            base.OnFormClosing(e);
            return;
        }

        e.Cancel = true;
        shutdownStarted = true;
        try
        {
            await backend.StopAsync();
        }
        finally
        {
            Close();
        }
    }

    private async Task StartApplicationAsync()
    {
        try
        {
            startupMessage.Text = "Starting Automation Workbench…\r\nStarting local services and preparing the interface.";
            var backendTask = backend.StartAsync();
            var webViewTask = InitializeWebViewAsync();
            await Task.WhenAll(backendTask, webViewTask);

            browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            browser.CoreWebView2.Navigate(backend.BaseUrl + "/");
        }
        catch (BackendStartupException exception)
        {
            ShowStartupError(
                "Automation Workbench could not start its backend.",
                exception.Message);
        }
        catch (COMException exception)
        {
            ShowStartupError(
                "Microsoft Edge WebView2 Runtime is required.",
                $"Install the Evergreen WebView2 Runtime, then start Automation Workbench again.\n\n{exception.Message}");
        }
        catch (Exception exception)
        {
            ShowStartupError(
                "Automation Workbench could not open its application window.",
                exception.Message);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        Directory.CreateDirectory(paths.WebViewUserDataPath);
        var runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        if (string.IsNullOrWhiteSpace(runtimeVersion))
            throw new InvalidOperationException("Microsoft Edge WebView2 Runtime was not found.");

        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: paths.WebViewUserDataPath);
        await browser.EnsureCoreWebView2Async(environment);
        browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
    }

    internal void ApplyWindowCommand(string command, string? direction = null)
    {
        switch (command)
        {
            case "minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "toggle-maximize":
                if (WindowState == FormWindowState.Maximized)
                {
                    WindowState = FormWindowState.Normal;
                    ApplyDefaultNormalBoundsIfTooSmall();
                }
                else
                {
                    WindowState = FormWindowState.Maximized;
                }
                break;
            case "close":
                Close();
                break;
            case "begin-drag":
                BeginNativeDrag();
                break;
            case "begin-resize":
                BeginNativeResize(direction);
                break;
            case "get-state":
                NotifyWindowState();
                break;
        }
    }

    // Restored windows coming out of maximize must default to a size that fits
    // the whole Studio layout; a deliberate smaller user size is kept as-is.
    private void ApplyDefaultNormalBoundsIfTooSmall()
    {
        if (Width >= MinimumUsableNormalWidth && Height >= MinimumUsableNormalHeight)
            return;
        var workingArea = Screen.FromHandle(Handle).WorkingArea;
        var size = DefaultNormalSize(workingArea);
        SetBounds(
            workingArea.Left + (workingArea.Width - size.Width) / 2,
            workingArea.Top + (workingArea.Height - size.Height) / 2,
            size.Width,
            size.Height);
    }

    private static Size DefaultNormalSize(Rectangle workingArea)
        => new(
            Math.Min(Math.Max(1360, workingArea.Width * 85 / 100), workingArea.Width),
            Math.Min(Math.Max(850, workingArea.Height * 85 / 100), workingArea.Height));

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            if (!root.TryGetProperty("type", out var type)
                || type.GetString() != "window-control")
                return;
            if (root.TryGetProperty("command", out var commandElement)
                && commandElement.GetString() is { } command)
            {
                var direction = root.TryGetProperty("direction", out var directionElement)
                    ? directionElement.GetString()
                    : null;
                ApplyWindowCommand(command, direction);
            }
        }
        catch (JsonException)
        {
            // Not a window-control message; ignore.
        }
    }

    private void OnDragSurfaceMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            BeginNativeDrag();
    }

    private void BeginNativeDrag()
    {
        if (WindowState != FormWindowState.Normal)
            return;
        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, (IntPtr)HitCaption, IntPtr.Zero);
    }

    // Studio owns the viewport edges, so it detects the pointer near a border
    // and asks for a resize; the native modal loop takes over from there and
    // gives normal Windows resize feedback (including snap layouts on move).
    private void BeginNativeResize(string? direction)
    {
        if (WindowState != FormWindowState.Normal)
            return;
        var hitCode = direction switch
        {
            "left" => HitLeft,
            "right" => HitRight,
            "top" => HitTop,
            "bottom" => HitBottom,
            "top-left" => HitTopLeft,
            "top-right" => HitTopRight,
            "bottom-left" => HitBottomLeft,
            "bottom-right" => HitBottomRight,
            _ => 0,
        };
        if (hitCode == 0)
            return;
        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, (IntPtr)hitCode, IntPtr.Zero);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState != lastNotifiedWindowState)
        {
            lastNotifiedWindowState = WindowState;
            NotifyWindowState();
        }
    }

    private void NotifyWindowState()
    {
        if (browser.CoreWebView2 is null)
            return;
        var state = WindowState == FormWindowState.Maximized ? "maximized" : "normal";
        browser.CoreWebView2.PostWebMessageAsJson(
            $"{{\"type\":\"window-state\",\"state\":\"{state}\"}}");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 niceties: rounded corners and a subtle border line so the
        // frameless window still reads as a window. Both no-op on Windows 10.
        var cornerPreference = DwmwcpRound;
        _ = DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(int));
        var borderColor = 0x00464646; // COLORREF 0x00BBGGRR, a neutral dark gray
        _ = DwmSetWindowAttribute(Handle, DwmwaBorderColor, ref borderColor, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmNcCalcSize when m.WParam != IntPtr.Zero:
                // Reclaim the whole window rect as client area: the caption
                // and frame disappear but the sizable styles (and with them
                // snap assist, DWM shadow, and native move/resize) remain.
                m.Result = IntPtr.Zero;
                return;
            case WmGetMinMaxInfo:
                base.WndProc(ref m);
                // With no non-client area the window would otherwise maximize
                // over the taskbar.
                ConstrainMaximizeToWorkingArea(m.LParam);
                return;
            default:
                base.WndProc(ref m);
                return;
        }
    }

    private void ConstrainMaximizeToWorkingArea(IntPtr lParam)
    {
        var monitor = MonitorFromWindow(Handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return;
        var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return;

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMaxInfo.ptMaxPosition = new NativePoint
        {
            X = monitorInfo.rcWork.Left - monitorInfo.rcMonitor.Left,
            Y = monitorInfo.rcWork.Top - monitorInfo.rcMonitor.Top,
        };
        minMaxInfo.ptMaxSize = new NativePoint
        {
            X = monitorInfo.rcWork.Right - monitorInfo.rcWork.Left,
            Y = monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top,
        };
        Marshal.StructureToPtr(minMaxInfo, lParam, true);
    }

    private const int HitLeft = 10;
    private const int HitRight = 11;
    private const int HitTop = 12;
    private const int HitTopLeft = 13;
    private const int HitTopRight = 14;
    private const int HitBottom = 15;
    private const int HitBottomLeft = 16;
    private const int HitBottomRight = 17;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            ShowStartupError(
                "Automation Workbench could not load its application window.",
                $"WebView2 navigation failed with status {e.WebErrorStatus}.");
            return;
        }

        startupSurface.Visible = false;
        browser.Visible = true;
        browser.BringToFront();
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsAllowedApplicationUrl(e.Uri))
            return;

        e.Cancel = true;
        OpenExternalUrl(e.Uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternalUrl(e.Uri);
    }

    private bool IsAllowedApplicationUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var baseUri = new Uri(backend.BaseUrl + "/");
        return string.Equals(uri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == baseUri.Port;
    }

    private static void OpenExternalUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true,
        });
    }

    private void ShowStartupError(string title, string detail)
    {
        MessageBox.Show(
            this,
            $"{detail}\n\nBackend log: {paths.BackendLogPath}",
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        Close();
    }
}
