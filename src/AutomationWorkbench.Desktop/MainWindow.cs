using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutomationWorkbench.Desktop;

public sealed class MainWindow : Form
{
    // The window is borderless: Studio renders the caption (drag area and the
    // minimize/maximize/close buttons) inside its own header and drives the
    // window through WebView2 messages handled by ApplyWindowCommand.
    private const int ResizeBorderThickness = 6;
    private const int WmNcHitTest = 0x0084;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HitCaption = 2;
    private const uint MonitorDefaultToNearest = 2;

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
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1440, 900);
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

    internal void ApplyWindowCommand(string command)
    {
        switch (command)
        {
            case "minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "toggle-maximize":
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal
                    : FormWindowState.Maximized;
                break;
            case "close":
                Close();
                break;
            case "begin-drag":
                BeginNativeDrag();
                break;
            case "get-state":
                NotifyWindowState();
                break;
        }
    }

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
                ApplyWindowCommand(command);
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

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmNcHitTest when WindowState == FormWindowState.Normal:
                base.WndProc(ref m);
                if (m.Result == (IntPtr)HitClient)
                    m.Result = (IntPtr)HitTestResizeBorder(m.LParam);
                return;
            case WmGetMinMaxInfo:
                base.WndProc(ref m);
                // Borderless windows would otherwise maximize over the taskbar.
                ConstrainMaximizeToWorkingArea(m.LParam);
                return;
            default:
                base.WndProc(ref m);
                return;
        }
    }

    private int HitTestResizeBorder(IntPtr lParam)
    {
        var screenPoint = new Point(
            (short)(lParam.ToInt64() & 0xFFFF),
            (short)((lParam.ToInt64() >> 16) & 0xFFFF));
        var point = PointToClient(screenPoint);
        var grip = ResizeBorderThickness;
        var left = point.X < grip;
        var right = point.X >= ClientSize.Width - grip;
        var top = point.Y < grip;
        var bottom = point.Y >= ClientSize.Height - grip;

        if (top && left) return HitTopLeft;
        if (top && right) return HitTopRight;
        if (bottom && left) return HitBottomLeft;
        if (bottom && right) return HitBottomRight;
        if (top) return HitTop;
        if (bottom) return HitBottom;
        if (left) return HitLeft;
        if (right) return HitRight;
        return HitClient;
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

    private const int HitClient = 1;
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
