using System.Diagnostics;

namespace ScadaTest.Core;

/// <summary>Polls a check until it passes or the timeout expires. SCADA tests wait; they never sleep.</summary>
public static class Eventually
{
    public static async Task<string> UntilAsync(
        string description,
        Func<CancellationToken, Task<(bool Passed, string Actual)>> check,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var lastActual = "(not read)";
        Exception? lastError = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (passed, actual) = await check(ct).ConfigureAwait(false);
                lastActual = actual;
                lastError = null;
                if (passed)
                    return actual;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                lastError = ex;
            }

            if (sw.Elapsed >= timeout)
                throw new ExpectationFailedException(description, lastActual, timeout, lastError);
            var remaining = timeout - sw.Elapsed;
            await Task.Delay(remaining < pollInterval ? remaining : pollInterval, ct).ConfigureAwait(false);
        }
    }
}

public sealed class ExpectationFailedException(string expectation, string lastActual, TimeSpan timeout, Exception? lastError)
    : Exception($"Expected {expectation} within {timeout.TotalSeconds:0.###} s, but last saw {lastActual}" +
                (lastError is null ? "." : $" (last error: {lastError.Message})."), lastError)
{
    public string Expectation { get; } = expectation;
    public string LastActual { get; } = lastActual;
    public TimeSpan Timeout { get; } = timeout;
}
