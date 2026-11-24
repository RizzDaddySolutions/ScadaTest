using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;

namespace ScadaTest.Spike.Omi;

internal enum CaptureMethod { Screen, PrintWindow }

/// <summary>The running ViewApp window, found by process name (and optionally a title fragment).</summary>
internal sealed class ViewApp : IDisposable
{
    private readonly UIA3Automation _automation;
    private readonly Application _app;

    public Window Window { get; }

    public IntPtr Handle { get; }

    public string ProcessName { get; }

    private ViewApp(UIA3Automation automation, Application app, Window window, string processName)
    {
        _automation = automation;
        _app = app;
        Window = window;
        Handle = window.Properties.NativeWindowHandle.ValueOrDefault;
        ProcessName = processName;
    }

    public static ViewApp Attach(string processName, string? titleContains)
    {
        var name = Path.GetFileNameWithoutExtension(processName);
        var process = Process.GetProcessesByName(name)
            .OrderByDescending(p => p.MainWindowHandle != IntPtr.Zero)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No running process named '{name}'. Run 'spike-omi windows' to list candidates.");

        var automation = new UIA3Automation();
        var app = Application.Attach(process);
        var windows = app.GetAllTopLevelWindows(automation);
        var window = (titleContains is null
                ? windows.OrderByDescending(w => w.BoundingRectangle.Width * w.BoundingRectangle.Height).FirstOrDefault()
                : windows.FirstOrDefault(w => (w.Name ?? string.Empty).Contains(titleContains, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException($"Process '{name}' (pid {process.Id}) has no matching top-level window.");
        return new ViewApp(automation, app, window, name);
    }

    /// <summary>Window bounds as UI Automation reports them; the driver measures symbol regions from here.</summary>
    public Rectangle Bounds => Window.BoundingRectangle;

    public uint Dpi => Native.GetDpiForWindow(Handle);

    public bool IsMinimized => Native.IsIconic(Handle);

    public bool IsForeground => Native.GetForegroundWindow() == Handle;

    /// <summary>Captures a region given relative to the window, with either method.</summary>
    public Bitmap CaptureRegion(Rectangle relative, CaptureMethod method)
    {
        if (method == CaptureMethod.Screen)
        {
            var b = Bounds;
            using var image = Capture.Rectangle(new Rectangle(b.X + relative.X, b.Y + relative.Y, relative.Width, relative.Height));
            return new Bitmap(image.Bitmap);
        }

        using var whole = CaptureWindow(CaptureMethod.PrintWindow);
        return whole.Clone(Rectangle.Intersect(relative, new Rectangle(Point.Empty, whole.Size)), PixelFormat.Format32bppArgb);
    }

    /// <summary>
    /// Captures the whole window. <see cref="CaptureMethod.Screen"/> copies what is on the monitor (needs the window visible
    /// and the desktop unlocked); <see cref="CaptureMethod.PrintWindow"/> asks the window to paint itself (may work when covered).
    /// </summary>
    public Bitmap CaptureWindow(CaptureMethod method)
    {
        var b = Bounds;
        if (method == CaptureMethod.Screen)
        {
            using var image = Capture.Rectangle(b);
            return new Bitmap(image.Bitmap);
        }

        Native.GetWindowRect(Handle, out var wr);
        var windowRect = wr.ToRectangle();
        using var full = new Bitmap(windowRect.Width, windowRect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            var hdc = g.GetHdc();
            try
            {
                if (!Native.PrintWindow(Handle, hdc, Native.PwRenderFullContent))
                    throw new InvalidOperationException("PrintWindow returned false.");
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        // Re-base onto the UIA bounds so regions mean the same thing for both methods.
        var offset = new Rectangle(b.X - windowRect.X, b.Y - windowRect.Y, b.Width, b.Height);
        return full.Clone(Rectangle.Intersect(offset, new Rectangle(Point.Empty, full.Size)), PixelFormat.Format32bppArgb);
    }

    public Rectangle WindowRect()
    {
        Native.GetWindowRect(Handle, out var wr);
        return wr.ToRectangle();
    }

    public UIA3Automation Automation => _automation;

    public void Dispose()
    {
        _app.Dispose();
        _automation.Dispose();
    }
}
