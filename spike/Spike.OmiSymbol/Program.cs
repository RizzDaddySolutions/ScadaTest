using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using ScadaTest.Adapters.OpcUa;
using ScadaTest.Core;
using ScadaTest.Hmi.Omi;
using ScadaTest.Spike;
using ScadaTest.Spike.Omi;

// Spike 1: can we tell an OMI pump symbol's state from screen pixels? See spike/README.md for the run order.
var args0 = args;
SpikeArgs a;
try { a = new SpikeArgs(args0); }
catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }

if (a.Command is "help" or "-h")
{
    Console.WriteLine("""
        spike-omi <command> [options]

          windows                         List top-level windows with their process names (find the ViewApp)
          tree     --process P            Dump the ViewApp's UI Automation tree (can we navigate by AutomationId?)
          cursor   --process P            Print the mouse position relative to the window, to measure symbol regions
          capture  --process P            Save the window with both capture methods and compare them
          sample   --process P SYMBOL     Classify the symbol once, save the crop, print colour statistics
          watch    --process P SYMBOL     Classify repeatedly and print every state change (toggle the pump by hand)
          verify   --process P SYMBOL OPC Drive the pump through OPC UA and time how long the symbol takes to follow

        Common:  --title TEXT (pick a window by title)  --method screen|printwindow (default screen)
        SYMBOL:  --map hmimap.yaml --symbol P-101       or  --region x,y,w,h --state running=#00B050 --state stopped=#808080
                 --tolerance 40   --min-share 0.25 (share-of-pixels classifier)
        watch:   --seconds 60 --interval 250ms
        verify:  --endpoint opc.tcp://host:port --node NODEID --on true --off false
                 --on-state running --off-state stopped --cycles 5 --within 3s [--feedback NODEID]
                 [--secure] [--auto-accept] [--username U --password P]
        """);
    return 0;
}

using var report = new Report($"omi-{a.Command}", args0);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;
var imageDir = Path.Combine(report.ResultsDir, "omi-images");
Directory.CreateDirectory(imageDir);

try
{
    switch (a.Command)
    {
        case "windows": Windows(); break;
        case "tree": Tree(); break;
        case "cursor": await CursorAsync(); break;
        case "capture": CaptureBoth(); break;
        case "sample": await SampleAsync(); break;
        case "watch": await WatchAsync(); break;
        case "verify": await VerifyAsync(); break;
        default: report.Fail($"Unknown command '{a.Command}'. Run with no arguments for help."); break;
    }
}
catch (OperationCanceledException)
{
    report.Warn("Cancelled.");
}
catch (Exception ex)
{
    report.Exception(ex);
}
return report.Finish();

// ---------------------------------------------------------------------------------------------------------------

ViewApp Attach()
{
    var app = ViewApp.Attach(a.Require("process"), a.Get("title"));
    var b = app.Bounds;
    var wr = app.WindowRect();
    report.Info($"Window '{app.Window.Name}' of {app.ProcessName}: UIA bounds {Fmt(b)}, GetWindowRect {Fmt(wr)}, DPI {app.Dpi} ({app.Dpi / 96.0:P0}), minimized {app.IsMinimized}, foreground {app.IsForeground}");
    if (app.Dpi != 96)
        report.Warn($"Display scaling is {app.Dpi / 96.0:P0}. Regions are physical pixels, so keep this scaling fixed on every test machine.");
    if (app.IsMinimized)
        report.Warn("The window is minimized; screen capture will not see it.");
    return app;
}

CaptureMethod Method() => a.Get("method", "screen").ToLowerInvariant() switch
{
    "screen" => CaptureMethod.Screen,
    "printwindow" => CaptureMethod.PrintWindow,
    var m => throw new ArgumentException($"--method must be screen or printwindow, not '{m}'."),
};

SymbolSpec Symbol()
{
    var tolerance = a.Int("tolerance", -1);
    if (a.Get("map") is { } mapPath)
    {
        var name = a.Require("symbol");
        var map = HmiMap.Load(mapPath);
        if (!map.Symbols.TryGetValue(name, out var entry) || entry.Region.Count != 4)
            throw new ArgumentException($"Symbol '{name}' is not in {mapPath} or has no [x, y, w, h] region.");
        return new SymbolSpec(name, new Rectangle(entry.Region[0], entry.Region[1], entry.Region[2], entry.Region[3]),
            entry.States, tolerance >= 0 ? tolerance : entry.Tolerance, Path.GetFullPath(mapPath));
    }

    var parts = a.Require("region").Split(',').Select(p => int.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray();
    if (parts.Length != 4)
        throw new ArgumentException("--region must be x,y,w,h relative to the window.");
    var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var s in a.GetAll("state"))
    {
        var eq = s.IndexOf('=');
        if (eq <= 0) throw new ArgumentException($"--state '{s}' must look like running=#00B050.");
        states[s[..eq]] = s[(eq + 1)..];
    }
    if (states.Count == 0)
        throw new ArgumentException("Give at least one --state name=#RRGGBB (or use --map and --symbol).");
    return new SymbolSpec(a.Get("symbol", "symbol"), new Rectangle(parts[0], parts[1], parts[2], parts[3]), states,
        tolerance >= 0 ? tolerance : 40, null);
}

(string Average, string Share, Pixels Px) Classify(ViewApp app, SymbolSpec s, CaptureMethod method)
{
    using var bmp = app.CaptureRegion(s.Region, method);
    var px = Pixels.From(bmp);
    var byAvg = Classifiers.ByAverage(px, s.States, s.Tolerance);
    var byShare = Classifiers.ByShare(px, s.States, s.Tolerance, a.Double("min-share", 0.25));
    return (byAvg.State, byShare.State, px);
}

void Windows()
{
    using var automation = new FlaUI.UIA3.UIA3Automation();
    var rows = new List<string>();
    foreach (var w in automation.GetDesktop().FindAllChildren())
    {
        var pid = w.Properties.ProcessId.ValueOrDefault;
        string proc;
        try { proc = Process.GetProcessById(pid).ProcessName; } catch (ArgumentException) { proc = "?"; }
        var r = w.BoundingRectangle;
        if (r.Width == 0 || r.Height == 0) continue;
        rows.Add($"{proc,-28} pid {pid,-6} {Fmt(r),-22} '{w.Name}'");
    }
    foreach (var row in rows.Order(StringComparer.OrdinalIgnoreCase))
        report.Info(row);
    report.Pass($"{rows.Count} visible top-level windows listed. Use the ViewApp's process name as --process.");
}

void Tree()
{
    using var app = Attach();
    var maxDepth = a.Int("depth", 12);
    var sb = new StringBuilder();
    int total = 0, withId = 0, buttons = 0;
    var controlTypes = new Dictionary<string, int>();
    void Walk(AutomationElement e, int depth)
    {
        total++;
        var id = e.Properties.AutomationId.ValueOrDefault;
        var type = e.Properties.ControlType.ValueOrDefault.ToString();
        controlTypes[type] = controlTypes.GetValueOrDefault(type) + 1;
        if (!string.IsNullOrEmpty(id)) withId++;
        if (type == "Button") buttons++;
        sb.AppendLine(CultureInfo.InvariantCulture, $"{new string(' ', depth * 2)}{type} id='{id}' name='{e.Properties.Name.ValueOrDefault}' class='{e.Properties.ClassName.ValueOrDefault}' {Fmt(e.BoundingRectangle)}");
        if (depth >= maxDepth) return;
        foreach (var child in e.FindAllChildren())
            Walk(child, depth + 1);
    }
    Walk(app.Window, 0);
    var path = Path.Combine(report.ResultsDir, $"omi-uia-tree-{DateTime.Now:HHmmss}.txt");
    File.WriteAllText(path, sb.ToString());
    report.Info($"Control types: {string.Join(", ", controlTypes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}");
    report.Info($"Tree written to {path}");
    report.Check(total > 1, $"UI Automation sees {total} elements under the window ({withId} with an AutomationId, {buttons} buttons).");
    if (withId == 0)
        report.Warn("No AutomationIds. Screen navigation would have to use Name, or click by position.");
}

async Task CursorAsync()
{
    using var app = Attach();
    var until = DateTime.UtcNow + a.Time("seconds", "30s");
    report.Info("Hover over the symbol's top-left and bottom-right corners. Ctrl+C to stop.");
    var last = Point.Empty;
    while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
    {
        var p = Mouse.Position;
        if (p != last)
        {
            last = p;
            var b = app.Bounds;
            using var px = FlaUI.Core.Capturing.Capture.Rectangle(new Rectangle(p.X, p.Y, 1, 1));
            var c = px.Bitmap.GetPixel(0, 0);
            report.Info($"screen ({p.X},{p.Y})  window-relative ({p.X - b.X},{p.Y - b.Y})  colour #{c.R:X2}{c.G:X2}{c.B:X2}");
        }
        await Task.Delay(300, ct);
    }
    report.Pass("Use the window-relative corners as region: [x, y, width, height] in hmimap.yaml.");
}

void CaptureBoth()
{
    using var app = Attach();
    var stamp = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
    Bitmap? screen = null, print = null;
    try
    {
        try
        {
            screen = app.CaptureWindow(CaptureMethod.Screen);
            var p = Path.Combine(imageDir, $"window-screen-{stamp}.png");
            screen.Save(p, ImageFormat.Png);
            var blank = Pixels.From(screen).LooksBlank();
            report.Check(!blank, $"Screen capture {screen.Width}x{screen.Height} saved to {p}{(blank ? " but it is black (locked desktop or minimized RDP?)" : string.Empty)}");
        }
        catch (Exception ex) { report.Fail($"Screen capture: {ex.Message}"); }

        try
        {
            print = app.CaptureWindow(CaptureMethod.PrintWindow);
            var p = Path.Combine(imageDir, $"window-printwindow-{stamp}.png");
            print.Save(p, ImageFormat.Png);
            var blank = Pixels.From(print).LooksBlank();
            if (blank)
                report.Warn($"PrintWindow capture saved to {p} is black: OMI content does not render through PrintWindow. Stick with screen capture.");
            else
                report.Pass($"PrintWindow capture {print.Width}x{print.Height} saved to {p}");
        }
        catch (Exception ex) { report.Warn($"PrintWindow capture failed: {ex.Message}"); }

        if (screen is not null && print is not null && screen.Size == print.Size)
        {
            var s = Pixels.From(screen).Average();
            var q = Pixels.From(print).Average();
            report.Info($"Average colour: screen {s}, printwindow {q} (distance {s.DistanceTo(q):0}). Open both PNGs to compare.");
        }
    }
    finally
    {
        screen?.Dispose();
        print?.Dispose();
    }
}

async Task SampleAsync()
{
    using var app = Attach();
    var s = Symbol();
    var method = Method();
    report.Info($"Symbol {s.Name}: region {Fmt(s.Region)}, tolerance {s.Tolerance}, states {string.Join(", ", s.States.Select(kv => $"{kv.Key}={kv.Value}"))}, method {method}");

    var stamp = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
    using (var window = app.CaptureWindow(method))
    {
        using (var g = Graphics.FromImage(window))
        using (var pen = new Pen(Color.Magenta, 2))
            g.DrawRectangle(pen, s.Region);
        var p = Path.Combine(imageDir, $"{Safe(s.Name)}-window-{stamp}.png");
        window.Save(p, ImageFormat.Png);
        report.Info($"Window with the region outlined in magenta: {p} (check the box sits on the pump)");
    }

    using var crop = app.CaptureRegion(s.Region, method);
    var cropPath = Path.Combine(imageDir, $"{Safe(s.Name)}-crop-{stamp}.png");
    using (var zoom = new Bitmap(crop.Width * 8, crop.Height * 8))
    {
        using var g = Graphics.FromImage(zoom);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(crop, 0, 0, zoom.Width, zoom.Height);
        zoom.Save(cropPath, ImageFormat.Png);
    }
    report.Info($"Crop (8x zoom): {cropPath}");

    var px = Pixels.From(crop);
    if (px.LooksBlank())
        report.Warn("The region is black. Is the desktop locked, the RDP window minimized, or the region off the window?");
    report.Info($"Average colour {px.Average()}");
    report.Info("Dominant colours: " + string.Join(", ", px.Dominant(5).Select(d => $"{d.Color} {d.Share:P0}")));

    var byAvg = Classifiers.ByAverage(px, s.States, s.Tolerance);
    var byShare = Classifiers.ByShare(px, s.States, s.Tolerance, a.Double("min-share", 0.25));
    report.Info($"Average classifier: {byAvg.State} ({byAvg.Detail})");
    report.Info($"Share classifier:   {byShare.State} ({byShare.Detail})");

    if (a.Get("expect") is { } expected)
    {
        report.Check(string.Equals(byAvg.State, expected, StringComparison.OrdinalIgnoreCase), $"Average classifier says '{byAvg.State}', expected '{expected}'");
        report.Check(string.Equals(byShare.State, expected, StringComparison.OrdinalIgnoreCase), $"Share classifier says '{byShare.State}', expected '{expected}'");
    }
    else
    {
        report.Check(byAvg.State != "unknown" || byShare.State != "unknown", "At least one classifier recognised a configured state. Add --expect STATE to make this a strict check.");
    }

    if (s.MapPath is not null)
        await DriverParityAsync(s, ct);
}

async Task DriverParityAsync(SymbolSpec s, CancellationToken token)
{
    // The real driver, as the runner would use it.
    await using var driver = new OmiViewAppDriver();
    var settings = new Dictionary<string, string> { ["processName"] = Path.GetFileNameWithoutExtension(a.Require("process")), ["hmiMap"] = s.MapPath! };
    await driver.ConnectAsync(new AdapterConfig("omi", settings, TagMap.Empty), token);
    var state = await driver.GetSymbolStateAsync(s.Name, token);
    report.Info($"OmiViewAppDriver.GetSymbolStateAsync says: {state.State} ({state.Detail})");
}

async Task WatchAsync()
{
    using var app = Attach();
    var s = Symbol();
    var method = Method();
    var interval = a.Time("interval", "250ms");
    var until = DateTime.UtcNow + a.Time("seconds", "60s");
    report.Info($"Watching {s.Name} every {interval.TotalMilliseconds:0} ms. Start and stop the pump now (from OMI, the IDE or the simulator). Ctrl+C to stop.");

    string? lastAvg = null, lastShare = null;
    int samples = 0, changes = 0, blanks = 0;
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var sw = Stopwatch.StartNew();
    while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
    {
        var t0 = sw.Elapsed;
        var (avg, share, px) = Classify(app, s, method);
        samples++;
        if (px.LooksBlank()) blanks++;
        if (avg != lastAvg || share != lastShare)
        {
            if (lastAvg is not null) changes++;
            report.Info($"t={sw.Elapsed.TotalSeconds,7:0.00}s  average={avg,-10} share={share,-10} ({px.Average()})");
            lastAvg = avg;
            lastShare = share;
        }
        seen.Add(avg);
        seen.Add(share);
        var spent = sw.Elapsed - t0;
        if (spent < interval)
            await Task.Delay(interval - spent, ct);
    }

    report.Info($"{samples} samples, {changes} changes, states seen: {string.Join(", ", seen)}");
    if (blanks > 0)
        report.Fail($"{blanks} samples were black. Screen capture stops when the session is locked or the RDP window is minimized.");
    var configured = s.States.Keys.Where(seen.Contains).ToList();
    report.Check(configured.Count >= 2, $"Saw {configured.Count} of the configured states ({string.Join(", ", configured)}). Toggle the pump during the watch to see at least two.");
}

async Task VerifyAsync()
{
    using var app = Attach();
    var s = Symbol();
    var method = Method();
    var node = a.Require("node");
    var onState = a.Get("on-state", "running");
    var offState = a.Get("off-state", "stopped");
    var cycles = a.Int("cycles", 5);
    var within = a.Time("within", "3s");
    var feedback = a.Get("feedback");

    await using var opc = new OpcUaAdapter();
    var settings = new Dictionary<string, string>
    {
        ["endpoint"] = a.Require("endpoint"),
        ["useSecurity"] = a.Flag("secure").ToString(),
        ["autoAcceptCertificates"] = a.Flag("auto-accept").ToString(),
        ["pkiPath"] = a.Get("pki", OpcUaRaw.DefaultPkiPath),
    };
    if (a.Get("username") is { } u) { settings["username"] = u; settings["password"] = a.Get("password", ""); }
    await opc.ConnectAsync(new AdapterConfig("galaxy", settings, TagMap.Empty), ct);
    report.Pass($"Connected to {settings["endpoint"]}");

    var original = await opc.ReadAsync(node, ct);
    report.Info($"{node} is {OpcUaRaw.Format(original.Value)} ({original.Quality}) before the test");

    var avgTimes = new List<double>();
    var shareTimes = new List<double>();
    int avgMisses = 0, shareMisses = 0;
    try
    {
        for (var i = 1; i <= cycles; i++)
        {
            foreach (var (value, target) in new[] { (a.Get("on", "true"), onState), (a.Get("off", "false"), offState) })
            {
                await opc.WriteAsync(node, value, ct);
                var sw = Stopwatch.StartNew();
                double? avgAt = null, shareAt = null, feedbackAt = null;
                string lastAvg = "", lastShare = "";
                while (sw.Elapsed < within && (avgAt is null || shareAt is null))
                {
                    var (avg, share, _) = Classify(app, s, method);
                    lastAvg = avg;
                    lastShare = share;
                    if (avgAt is null && string.Equals(avg, target, StringComparison.OrdinalIgnoreCase)) avgAt = sw.Elapsed.TotalMilliseconds;
                    if (shareAt is null && string.Equals(share, target, StringComparison.OrdinalIgnoreCase)) shareAt = sw.Elapsed.TotalMilliseconds;
                    if (feedback is not null && feedbackAt is null)
                    {
                        var fb = await opc.ReadAsync(feedback, ct);
                        if (ValueComparer.AreEqual(fb.Value, value, 0)) feedbackAt = sw.Elapsed.TotalMilliseconds;
                    }
                    await Task.Delay(50, ct);
                }

                if (avgAt is { } t1) avgTimes.Add(t1); else avgMisses++;
                if (shareAt is { } t2) shareTimes.Add(t2); else shareMisses++;
                report.Info($"cycle {i}: wrote {value}, want '{target}'. average: {Ms(avgAt, lastAvg)}  share: {Ms(shareAt, lastShare)}" +
                    (feedback is null ? string.Empty : $"  feedback tag: {(feedbackAt is { } f ? $"{f:0} ms" : "not seen")}"));
            }
        }
    }
    finally
    {
        await opc.WriteAsync(node, original.Value, CancellationToken.None);
        report.Info($"Restored {node} to {OpcUaRaw.Format(original.Value)}");
    }

    var total = cycles * 2;
    report.Check(avgMisses == 0, $"Average classifier (today's driver) followed {total - avgMisses}/{total} changes within {within.TotalSeconds:0.#}s. {Stats(avgTimes)}");
    report.Check(shareMisses == 0, $"Share classifier followed {total - shareMisses}/{total} changes within {within.TotalSeconds:0.#}s. {Stats(shareTimes)}");
    if (s.MapPath is not null)
        await DriverParityAsync(s, ct);
}

static string Ms(double? ms, string last) => ms is { } v ? $"{v:0} ms" : $"MISSED (last saw '{last}')";

static string Stats(List<double> t) => t.Count == 0 ? string.Empty : $"Latency min {t.Min():0} / avg {t.Average():0} / max {t.Max():0} ms.";

static string Fmt(Rectangle r) => $"[{r.X},{r.Y} {r.Width}x{r.Height}]";

static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

internal sealed record SymbolSpec(string Name, Rectangle Region, IReadOnlyDictionary<string, string> States, int Tolerance, string? MapPath);
