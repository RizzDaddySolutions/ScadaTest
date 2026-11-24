using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Input;
using FlaUI.UIA3;
using ScadaTest.Core;
using Region = ScadaTest.Core.Region;

namespace ScadaTest.Hmi.Omi;

/// <summary>
/// Drives a running InTouch OMI ViewApp. Navigation uses UI Automation; symbol state is read by sampling
/// the symbol's region and matching the average colour against the states in the HMI map.
/// </summary>
/// <remarks>
/// Settings: <c>processName</c> (required, the ViewApp runtime process), <c>hmiMap</c> (required, path to the map YAML).
/// Needs an interactive desktop session with fixed resolution and DPI. Which strategy works best for OMI
/// graphics is the main question for the spike.
/// </remarks>
[ScadaAdapter("aveva-omi")]
[SupportedOSPlatform("windows")]
public sealed class OmiViewAppDriver : IScadaAdapter, IHmiDriver
{
    private UIA3Automation? _automation;
    private Application? _app;
    private HmiMap _map = new();

    public string Id => "aveva-omi";

    public Capability Capabilities => Capability.Hmi;

    public Task ConnectAsync(AdapterConfig config, CancellationToken ct)
    {
        _map = HmiMap.Load(config.Require("hmiMap"));
        _automation = new UIA3Automation();
        _app = Application.Attach(config.Require("processName"));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _app?.Dispose();
        _automation?.Dispose();
        return ValueTask.CompletedTask;
    }

    private Window MainWindow =>
        _app?.GetMainWindow(_automation!) ?? throw new InvalidOperationException("OMI driver is not connected.");

    public async Task OpenScreenAsync(string screen, CancellationToken ct)
    {
        if (!_map.Screens.TryGetValue(screen, out var entry))
            throw new InvalidOperationException($"Screen '{screen}' is not in the HMI map.");

        var window = MainWindow;
        window.Focus();
        var element = entry.AutomationId is not null
            ? window.FindFirstDescendant(cf => cf.ByAutomationId(entry.AutomationId))
            : window.FindFirstDescendant(cf => cf.ByName(entry.Name ?? screen));
        if (element is null)
            throw new InvalidOperationException($"Could not find the navigation element for screen '{screen}'.");

        element.Click();
        await Task.Delay(entry.Settle is null ? TimeSpan.FromSeconds(1) : Duration.Parse(entry.Settle), ct).ConfigureAwait(false);
    }

    public Task<SymbolState> GetSymbolStateAsync(SymbolRef symbol, CancellationToken ct)
    {
        var entry = Symbol(symbol);
        var rect = ToScreen(entry);
        using var capture = Capture.Rectangle(rect);
        var sample = Average(capture.Bitmap);
        var (state, distance) = ColorClassifier.Classify(sample, entry.States, entry.Tolerance);
        return Task.FromResult(new SymbolState(state ?? "unknown", $"avg {sample}, distance {distance:0}"));
    }

    public Task<byte[]> CaptureAsync(Region? region, CancellationToken ct)
    {
        using var capture = region is { } r
            ? Capture.Rectangle(new Rectangle(r.X, r.Y, r.Width, r.Height))
            : Capture.Element(MainWindow);
        using var ms = new MemoryStream();
        capture.Bitmap.Save(ms, ImageFormat.Png);
        return Task.FromResult(ms.ToArray());
    }

    public Task ClickAsync(SymbolRef symbol, CancellationToken ct)
    {
        var rect = ToScreen(Symbol(symbol));
        Mouse.Click(new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
        return Task.CompletedTask;
    }

    private SymbolEntry Symbol(SymbolRef symbol) =>
        _map.Symbols.TryGetValue(symbol.Name, out var e) && e.Region.Count == 4
            ? e
            : throw new InvalidOperationException($"Symbol '{symbol}' is not in the HMI map or has no [x, y, w, h] region.");

    /// <summary>Map regions are relative to the ViewApp window, so window moves don't break tests.</summary>
    private Rectangle ToScreen(SymbolEntry e)
    {
        var w = MainWindow.BoundingRectangle;
        return new Rectangle(w.X + e.Region[0], w.Y + e.Region[1], e.Region[2], e.Region[3]);
    }

    private static ColorClassifier.Rgb Average(Bitmap bmp)
    {
        long r = 0, g = 0, b = 0;
        var n = 0;
        for (var y = 0; y < bmp.Height; y++)
        for (var x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            r += c.R; g += c.G; b += c.B; n++;
        }
        return n == 0 ? default : new ColorClassifier.Rgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }
}
