using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ArchestrA.MxAccess;

namespace ScadaTest.Spike.MxAccess;

/// <summary>
/// Spike 2b: read, write and watch Galaxy attributes (including alarm sub-attributes) through MXAccess.
/// MXAccess is a 32-bit COM server: this must run as x86 on an STA thread that pumps Windows messages,
/// because data-change and write-complete callbacks arrive as messages.
/// </summary>
internal static class Program
{
    private static readonly Dictionary<int, string> ItemNames = new Dictionary<int, string>();
    private static readonly Dictionary<int, Update> Latest = new Dictionary<int, Update>();
    private static readonly Dictionary<int, string> WriteResults = new Dictionary<int, string>();
    private static StreamWriter? _log;
    private static int _passed, _failed;
    private static bool _printUpdates;

    private sealed class Update
    {
        public object? Value;
        public int Quality;
        public object? Timestamp;
        public string Status = "";
        public int Count;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var options = Parse(args.Skip(1).ToArray());
        if (command == "help" || command == "-h")
        {
            Console.WriteLine(@"spike-mxaccess <command> [options]      (32-bit; run on the System Platform node)

  read      --item OBJ.Attr [--item ...]          First value, quality and timestamp of each item
  roundtrip --item OBJ.Attr --value V             Write V, wait for the data change, restore the original
  watch     --item OBJ.Attr [--item ...] [--seconds 60]
                                                  Print every change (use for alarm sub-attributes while you drive the PV)
  ack       --alarm OBJ.Attr.Condition [--comment ""text""]
                                                  Acknowledge an alarm by writing its AckMsg, then confirm .Acked goes true

Options: --timeout 10 (seconds to wait for callbacks)  --user-id 0 (MXAccess user id for writes)
Examples: --item P101.Running   --item LT301.PV.Hi.InAlarm --item LT301.PV.Hi.Acked   --alarm LT301.PV.Hi");
            return 0;
        }

        var resultsDir = Path.GetFullPath(Environment.GetEnvironmentVariable("SPIKE_RESULTS") ?? "spike-results");
        Directory.CreateDirectory(resultsDir);
        var logPath = Path.Combine(resultsDir, "mxaccess-" + command + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
        _log = new StreamWriter(logPath) { AutoFlush = true };
        _log.WriteLine("# mxaccess " + string.Join(" ", args));
        _log.WriteLine("# machine " + Environment.MachineName + ", user " + Environment.UserName + ", 64-bit process " + Environment.Is64BitProcess +
                       ", apartment " + Thread.CurrentThread.GetApartmentState());

        LMXProxyServerClass? server = null;
        var handle = 0;
        try
        {
            Check(!Environment.Is64BitProcess, "Running as a 32-bit process");
            server = new LMXProxyServerClass();
            server.OnDataChange += OnDataChange;
            server.OnWriteComplete += OnWriteComplete;
            handle = server.Register("ScadaTest.Spike");
            Check(handle != 0, "Registered with MXAccess (handle " + handle + ")");

            var timeout = TimeSpan.FromSeconds(double.Parse(Get(options, "timeout", "10"), CultureInfo.InvariantCulture));
            var userId = int.Parse(Get(options, "user-id", "0"), CultureInfo.InvariantCulture);
            switch (command)
            {
                case "read": Read(server, handle, All(options, "item"), timeout); break;
                case "roundtrip": RoundTrip(server, handle, Require(options, "item"), Require(options, "value"), timeout, userId); break;
                case "watch": Watch(server, handle, All(options, "item"), TimeSpan.FromSeconds(double.Parse(Get(options, "seconds", "60"), CultureInfo.InvariantCulture)), timeout); break;
                case "ack": Ack(server, handle, Require(options, "alarm"), Get(options, "comment", "ScadaTest spike"), timeout, userId); break;
                default: Fail("Unknown command '" + command + "'. Run with no arguments for help."); break;
            }
        }
        catch (COMException ex)
        {
            Fail("COM error 0x" + ex.ErrorCode.ToString("X8", CultureInfo.InvariantCulture) + ": " + ex.Message +
                 " (is this an x86 build on a node with System Platform and a deployed platform?)");
            _log.WriteLine(ex);
        }
        catch (Exception ex)
        {
            Fail(ex.GetType().Name + ": " + ex.Message);
            _log.WriteLine(ex);
        }
        finally
        {
            if (server != null && handle != 0)
            {
                foreach (var item in ItemNames.Keys.ToList())
                {
                    try { server.UnAdvise(handle, item); server.RemoveItem(handle, item); }
                    catch (COMException) { /* best effort */ }
                }
                try { server.Unregister(handle); } catch (COMException) { /* best effort */ }
            }
            if (server != null) Marshal.ReleaseComObject(server);
        }

        Line("DONE", (_failed == 0 ? "PASS" : "FAIL") + ": " + _passed + " passed, " + _failed + " failed. Log: " + logPath,
            _failed == 0 ? ConsoleColor.Green : ConsoleColor.Red);
        _log.Dispose();
        return _failed == 0 ? 0 : 1;
    }

    // ---- commands ---------------------------------------------------------------------------------------------

    private static void Read(LMXProxyServerClass server, int handle, IList<string> items, TimeSpan timeout)
    {
        if (items.Count == 0) throw new ArgumentException("Give at least one --item.");
        var handles = items.Select(i => AddAndAdvise(server, handle, i)).ToList();
        var sw = Stopwatch.StartNew();
        Pump(timeout, () => handles.All(h => Latest.ContainsKey(h)));
        foreach (var h in handles)
        {
            Update? u;
            if (Latest.TryGetValue(h, out u))
                Check(IsGood(u.Quality), ItemNames[h] + " = " + Format(u.Value) + " (" + (u.Value == null ? "null" : u.Value.GetType().Name) +
                      "), quality " + u.Quality + ", time " + Format(u.Timestamp) + ", status " + u.Status);
            else
                Fail(ItemNames[h] + ": no data change within " + timeout.TotalSeconds + "s (is the object deployed and the attribute name right?)");
        }
        Info("First values arrived after " + sw.ElapsedMilliseconds + " ms");
    }

    private static void RoundTrip(LMXProxyServerClass server, int handle, string item, string text, TimeSpan timeout, int userId)
    {
        var h = AddAndAdvise(server, handle, item);
        if (!Pump(timeout, () => Latest.ContainsKey(h)))
        {
            Fail(item + ": no initial value within " + timeout.TotalSeconds + "s");
            return;
        }
        var original = Latest[h].Value;
        Info("Before: " + item + " = " + Format(original) + " (" + (original == null ? "null" : original.GetType().Name) + ")");

        var value = ConvertLike(text, original);
        if (!WriteAndWait(server, handle, h, value, timeout, userId)) return;
        var sw = Stopwatch.StartNew();
        var seen = Pump(timeout, () => Equals(Latest[h].Value, value));
        Check(seen, "Data change shows " + Format(Latest[h].Value) + " after " + sw.ElapsedMilliseconds + " ms (wanted " + Format(value) + ")");

        if (WriteAndWait(server, handle, h, original, timeout, userId))
        {
            var restored = Pump(timeout, () => Equals(Latest[h].Value, original));
            Check(restored, "Restored " + item + " to " + Format(Latest[h].Value));
        }
    }

    private static void Watch(LMXProxyServerClass server, int handle, IList<string> items, TimeSpan duration, TimeSpan timeout)
    {
        if (items.Count == 0) throw new ArgumentException("Give at least one --item.");
        var handles = items.Select(i => AddAndAdvise(server, handle, i)).ToList();
        _printUpdates = true;
        Info("Watching " + string.Join(", ", items) + " for " + duration.TotalSeconds + "s. Drive the process now (simulator, OMI, IDE). Ctrl+C to stop.");
        Pump(duration, () => false);
        _printUpdates = false;
        foreach (var h in handles)
        {
            Update? u;
            var count = Latest.TryGetValue(h, out u) ? u.Count : 0;
            Check(count > 0, ItemNames[h] + ": " + count + " updates, last " + (u == null ? "none" : Format(u.Value) + " q" + u.Quality));
        }
    }

    private static void Ack(LMXProxyServerClass server, int handle, string alarm, string comment, TimeSpan timeout, int userId)
    {
        var inAlarm = AddAndAdvise(server, handle, alarm + ".InAlarm");
        var acked = AddAndAdvise(server, handle, alarm + ".Acked");
        var ackMsg = AddAndAdvise(server, handle, alarm + ".AckMsg");
        Pump(timeout, () => Latest.ContainsKey(inAlarm) && Latest.ContainsKey(acked));
        Info(alarm + ": InAlarm " + Value(inAlarm) + ", Acked " + Value(acked));
        if (Equals(Latest.ContainsKey(acked) ? Latest[acked].Value : null, true))
            Info("The alarm is already acknowledged. Raise it again (drive the PV past the limit) to test a real ack.");

        if (!WriteAndWait(server, handle, ackMsg, comment, timeout, userId)) return;
        var ok = Pump(timeout, () => Latest.ContainsKey(acked) && Equals(Latest[acked].Value, true));
        Check(ok, alarm + ".Acked = " + Value(acked) + " after writing AckMsg '" + comment + "'");
    }

    // ---- MXAccess plumbing ------------------------------------------------------------------------------------

    private static int AddAndAdvise(LMXProxyServerClass server, int handle, string item)
    {
        var h = server.AddItem(handle, item);
        ItemNames[h] = item;
        server.Advise(handle, h);
        return h;
    }

    private static bool WriteAndWait(LMXProxyServerClass server, int handle, int item, object? value, TimeSpan timeout, int userId)
    {
        WriteResults.Remove(item);
        server.Write(handle, item, value, userId);
        var done = Pump(timeout, () => WriteResults.ContainsKey(item));
        if (!done)
        {
            Fail("Write " + Format(value) + " to " + ItemNames[item] + ": no OnWriteComplete within " + timeout.TotalSeconds + "s");
            return false;
        }
        var result = WriteResults[item];
        return Check(result.StartsWith("ok", StringComparison.Ordinal), "Write " + Format(value) + " to " + ItemNames[item] + ": " + result);
    }

    private static void OnDataChange(int hLMXServerHandle, int phItemHandle, object pvItemValue, int pwItemQuality, object pftItemTimeStamp, ref MXSTATUS_PROXY[] ItemStatus)
    {
        Update? u;
        if (!Latest.TryGetValue(phItemHandle, out u))
            Latest[phItemHandle] = u = new Update();
        u.Value = pvItemValue;
        u.Quality = pwItemQuality;
        u.Timestamp = pftItemTimeStamp;
        u.Status = Describe(ItemStatus);
        u.Count++;
        if (_printUpdates)
        {
            string? name;
            Info((ItemNames.TryGetValue(phItemHandle, out name) ? name : "#" + phItemHandle) + " = " + Format(pvItemValue) + "  q" + pwItemQuality + "  " + Format(pftItemTimeStamp));
        }
    }

    private static void OnWriteComplete(int hLMXServerHandle, int phItemHandle, ref MXSTATUS_PROXY[] ItemStatus)
    {
        var ok = ItemStatus == null || ItemStatus.Length == 0 || ItemStatus[0].success != 0;
        WriteResults[phItemHandle] = (ok ? "ok " : "failed ") + Describe(ItemStatus);
    }

    private static string Describe(MXSTATUS_PROXY[]? status)
    {
        if (status == null || status.Length == 0) return "(no status)";
        var s = status[0];
        return "success=" + s.success + " category=" + s.category + " detectedBy=" + s.detectedBy + " detail=" + s.detail;
    }

    /// <summary>Pumps Windows messages (so COM callbacks run) until the condition holds or the time is up.</summary>
    private static bool Pump(TimeSpan timeout, Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            NativeMessage msg;
            while (PeekMessage(out msg, IntPtr.Zero, 0, 0, 1))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (done()) return true;
            Thread.Sleep(10);
        }
        return done();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage msg);

    // ---- helpers ----------------------------------------------------------------------------------------------

    /// <summary>OPC-style quality: 192 (0xC0) and above is good.</summary>
    private static bool IsGood(int quality) => (quality & 0xC0) == 0xC0;

    private static string Value(int item)
    {
        Update? u;
        return Latest.TryGetValue(item, out u) ? Format(u.Value) : "(no value)";
    }

    private static object ConvertLike(string text, object? current)
    {
        if (current is bool) return text == "1" || (text != "0" && bool.Parse(text));
        if (current == null) return text;
        return Convert.ChangeType(text, current.GetType(), CultureInfo.InvariantCulture);
    }

    private static string Format(object? v)
    {
        if (v == null) return "null";
        if (v is string s) return "\"" + s + "\"";
        if (v is DateTime d) return d.ToString("O", CultureInfo.InvariantCulture);
        var f = v as IFormattable;
        return f != null ? f.ToString(null, CultureInfo.InvariantCulture) : v.ToString() ?? "";
    }

    private static Dictionary<string, List<string>> Parse(string[] args)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unexpected argument '" + args[i] + "'.");
            var key = args[i].Substring(2);
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            List<string>? list;
            if (!d.TryGetValue(key, out list)) d[key] = list = new List<string>();
            list.Add(value);
        }
        return d;
    }

    private static string Get(Dictionary<string, List<string>> o, string key, string fallback)
    {
        List<string>? v;
        return o.TryGetValue(key, out v) ? v[v.Count - 1] : fallback;
    }

    private static string Require(Dictionary<string, List<string>> o, string key)
    {
        List<string>? v;
        if (!o.TryGetValue(key, out v)) throw new ArgumentException("Missing required option --" + key + ".");
        return v[v.Count - 1];
    }

    private static IList<string> All(Dictionary<string, List<string>> o, string key)
    {
        List<string>? v;
        return o.TryGetValue(key, out v) ? (IList<string>)v : new List<string>();
    }

    private static bool Check(bool ok, string message)
    {
        if (ok) { _passed++; Line("PASS", message, ConsoleColor.Green); }
        else Fail(message);
        return ok;
    }

    private static void Fail(string message) { _failed++; Line("FAIL", message, ConsoleColor.Red); }

    private static void Info(string message) => Line("INFO", message, ConsoleColor.Gray);

    private static void Line(string level, string message, ConsoleColor color)
    {
        var line = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + level + " " + message;
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(line);
        Console.ForegroundColor = previous;
        _log?.WriteLine(line);
    }
}
