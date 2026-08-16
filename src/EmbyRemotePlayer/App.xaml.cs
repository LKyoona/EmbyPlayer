using EmbyRemotePlayer.Services;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace EmbyRemotePlayer;

public partial class App : Application
{
    private int _showingUnhandledException;

    private void SuppressScrollBarContextMenu(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        LogService.Info($"Application starting. Version={version}; OS={Environment.OSVersion}; Runtime={Environment.Version}; 64Bit={Environment.Is64BitProcess}.");
        base.OnStartup(e);
    }


    /// <summary>
    /// Fits a window to the working area of the monitor it actually opens on.
    /// Monitor coordinates are physical pixels; WPF sizes are device-independent
    /// pixels, so the current monitor DPI is applied before assigning Width/Height.
    /// </summary>
    public static void ConfigureAdaptiveWindow(
        Window window,
        double widthRatio,
        double heightRatio,
        double minimumWidth,
        double minimumHeight,
        double maximumDesignWidth,
        double maximumDesignHeight)
    {
        var applied = false;

        void Apply()
        {
            if (applied || window.WindowState != WindowState.Normal) return;

            try
            {
                var helper = new WindowInteropHelper(window);
                var hwnd = helper.Handle;
                if (hwnd == IntPtr.Zero) return;

                var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero) return;

                var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info)) return;

                var dpi = TryGetDpiForWindow(hwnd);
                var dipPerPixel = 96.0 / Math.Max(96u, dpi);

                var physicalWidth = Math.Max(1, info.rcWork.Right - info.rcWork.Left);
                var physicalHeight = Math.Max(1, info.rcWork.Bottom - info.rcWork.Top);
                var workWidth = physicalWidth * dipPerPixel;
                var workHeight = physicalHeight * dipPerPixel;

                // On compact screens use almost all of the work area. On large
                // screens keep a cinematic desktop-window footprint instead of
                // expanding without limit.
                var compact = physicalWidth <= 1440 || physicalHeight <= 820;
                var targetWidthRatio = compact ? Math.Max(widthRatio, 0.965) : widthRatio;
                var targetHeightRatio = compact ? Math.Max(heightRatio, 0.94) : heightRatio;

                var width = Math.Min(workWidth * targetWidthRatio, maximumDesignWidth);
                var height = Math.Min(workHeight * targetHeightRatio, maximumDesignHeight);

                width = Math.Max(Math.Min(minimumWidth, workWidth * 0.96), width);
                height = Math.Max(Math.Min(minimumHeight, workHeight * 0.94), height);

                width = Math.Min(width, workWidth * 0.98);
                height = Math.Min(height, workHeight * 0.97);

                window.Width = Math.Max(640, width);
                window.Height = Math.Max(420, height);
                window.Left = (info.rcWork.Left * dipPerPixel) + (workWidth - window.Width) / 2.0;
                window.Top = (info.rcWork.Top * dipPerPixel) + (workHeight - window.Height) / 2.0;

                LogService.Info(
                    $"Adaptive window: {window.GetType().Name}; monitor={physicalWidth}x{physicalHeight}px; " +
                    $"dpi={dpi}; dip={workWidth:0}x{workHeight:0}; window={window.Width:0}x{window.Height:0}.");
                applied = true;
            }
            catch (Exception ex)
            {
                // Adaptation is cosmetic; never prevent the application from opening.
                LogService.Warning("Unable to apply adaptive window sizing.", ex);
            }
        }

        // Apply only when the HWND is first created. Resizing again during Loaded can
        // race LibVLCSharp.WPF VideoView native-surface initialization.
        window.SourceInitialized += (_, _) => Apply();
    }

    private static uint TryGetDpiForWindow(IntPtr hwnd)
    {
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 96u : dpi;
        }
        catch (EntryPointNotFoundException)
        {
            return 96u;
        }
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
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
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    protected override void OnExit(ExitEventArgs e)
    {
        LogService.Info($"Application exiting with code {e.ApplicationExitCode}.");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogService.Error("Unhandled UI exception.", e.Exception);
        e.Handled = true;

        if (Interlocked.Exchange(ref _showingUnhandledException, 1) != 0) return;
        try
        {
            MessageBox.Show(
                $"程序遇到一个界面错误，但已阻止自动退出。\n\n{e.Exception.Message}\n\n日志目录：{AppPaths.LogDirectory}",
                "Emby Remote Player",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _showingUnhandledException, 0);
        }
    }

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogService.Error($"Unhandled AppDomain exception. IsTerminating={e.IsTerminating}.", exception);
        }
        else
        {
            LogService.Error($"Unhandled AppDomain exception object: {e.ExceptionObject}. IsTerminating={e.IsTerminating}.");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogService.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
