using System.Globalization;
using System.Text;

namespace ScadaTest.Spike;

/// <summary>
/// Prints PASS / FAIL / WARN / INFO lines to the console and to <c>spike-results/&lt;name&gt;-&lt;time&gt;.log</c>,
/// so a run can be sent back as one file. The exit code is 1 when anything failed.
/// </summary>
public sealed class Report : IDisposable
{
    private readonly StreamWriter _log;
    private int _passed;
    private int _failed;
    private int _warned;

    public string LogPath { get; }

    public string ResultsDir { get; }

    public Report(string name, string[] args)
    {
        ResultsDir = Path.GetFullPath(Environment.GetEnvironmentVariable("SPIKE_RESULTS") ?? "spike-results");
        Directory.CreateDirectory(ResultsDir);
        LogPath = Path.Combine(ResultsDir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _log = new StreamWriter(LogPath, append: false, Encoding.UTF8) { AutoFlush = true };
        _log.WriteLine($"# {name} {string.Join(' ', args)}");
        _log.WriteLine($"# machine {Environment.MachineName}, user {Environment.UserName}, os {Environment.OSVersion}, .NET {Environment.Version}");
        _log.WriteLine($"# started {DateTimeOffset.Now:O}");
    }

    public void Info(string message) => Write("INFO", message, ConsoleColor.Gray);

    public void Pass(string message) { _passed++; Write("PASS", message, ConsoleColor.Green); }

    public void Fail(string message) { _failed++; Write("FAIL", message, ConsoleColor.Red); }

    public void Warn(string message) { _warned++; Write("WARN", message, ConsoleColor.Yellow); }

    /// <summary>Records PASS when <paramref name="ok"/> is true, otherwise FAIL.</summary>
    public bool Check(bool ok, string message)
    {
        if (ok) Pass(message); else Fail(message);
        return ok;
    }

    /// <summary>Runs one step; an exception becomes a FAIL line instead of ending the program.</summary>
    public async Task<T?> StepAsync<T>(string what, Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Fail($"{what}: {ex.GetType().Name}: {ex.Message}");
            _log.WriteLine(ex.ToString());
            return default;
        }
    }

    public async Task<bool> StepAsync(string what, Func<Task> action) =>
        await StepAsync(what, async () => { await action().ConfigureAwait(false); return true; }).ConfigureAwait(false);

    public void Exception(Exception ex)
    {
        Fail($"{ex.GetType().Name}: {ex.Message}");
        _log.WriteLine(ex.ToString());
    }

    /// <summary>Prints the summary line and returns the process exit code.</summary>
    public int Finish()
    {
        var verdict = _failed == 0 ? "PASS" : "FAIL";
        Write("DONE", $"{verdict}: {_passed} passed, {_failed} failed, {_warned} warnings. Log: {LogPath}",
            _failed == 0 ? ConsoleColor.Green : ConsoleColor.Red);
        return _failed == 0 ? 0 : 1;
    }

    public void Dispose() => _log.Dispose();

    private void Write(string level, string message, ConsoleColor color)
    {
        var line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} {message}";
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(line);
        Console.ForegroundColor = previous;
        _log.WriteLine(line);
    }
}
