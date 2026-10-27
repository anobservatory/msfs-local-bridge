using System.Diagnostics;
using System.Text.Json;
using MsfsLocalBridge.Models;
using MsfsLocalBridge.Services;

internal static class DiagnosticsChecks
{
    public static async Task RunAsync()
    {
        var clock = new ManualClock();
        var calls = 0;
        var service = new BridgeDiagnosticsService(_ => Task.FromResult(Report(++calls)), clock);
        await service.GetAsync();
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            await service.GetAsync();
        }
        Require(calls == 1, "Six background polls reuse one full diagnostic query.");
        clock.Advance(TimeSpan.FromSeconds(5));
        await service.GetAsync();
        Require(calls == 2, "Diagnostics expire at the 30-second boundary.");
        await service.GetAsync(forceRefresh: true);
        service.Invalidate();
        await service.GetAsync();
        Require(calls == 4, "Explicit refresh and host changes bypass cached diagnostics.");

        var pending = new TaskCompletionSource<(DiagnosticsResult, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        calls = 0;
        service = new BridgeDiagnosticsService(_ => { calls++; return pending.Task; }, clock);
        var first = service.GetAsync();
        var second = service.GetAsync();
        pending.SetResult(Report(1));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Require(calls == 1, "Overlapping reads share a single full query.");

        pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        calls = 0;
        service = new BridgeDiagnosticsService(_ => ++calls == 1 ? pending.Task : Task.FromResult(Report(calls)), clock);
        first = service.GetAsync();
        second = service.GetAsync(forceRefresh: true);
        pending.SetResult(Report(1));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        var latest = await service.GetAsync();
        Require(calls == 2 && latest.RawJson == "2", "A forced refresh during an older query cannot cache the older host state.");

        calls = 0;
        service = new BridgeDiagnosticsService(_ => ++calls == 1
            ? Task.FromException<(DiagnosticsResult, string)>(new InvalidOperationException("fixture failure"))
            : Task.FromResult(Report(calls)), clock);
        try { await service.GetAsync(); throw new Exception("Failed diagnostics were accepted."); }
        catch (InvalidOperationException) { }
        await service.GetAsync();
        Require(calls == 2, "Failed diagnostics are retried instead of cached.");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        calls = 0;
        service = new BridgeDiagnosticsService(async token =>
        {
            if (++calls == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Report(calls);
        }, clock);
        using var activeCancellation = new CancellationTokenSource();
        first = service.GetAsync(activeCancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var waitingCancellation = new CancellationTokenSource();
        second = service.GetAsync(waitingCancellation.Token);
        waitingCancellation.Cancel();
        await ExpectCancellationAsync(second);
        activeCancellation.Cancel();
        await ExpectCancellationAsync(first);
        await service.GetAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Require(calls == 2, "Cancelled queries and waiting callers release the gate without caching results.");
    }

    public static async Task MeasureAsync(string repositoryRoot)
    {
        var actual = new BridgeDiagnosticsService(new BridgeWorkspace(repositoryRoot), new PowerShellRunner());
        var fullQueries = 0;
        var cached = new BridgeDiagnosticsService(async token =>
        {
            fullQueries++;
            return await actual.GetAsync(token, forceRefresh: true);
        }, TimeProvider.System);
        var elapsed = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            if (i > 0) await Task.Delay(TimeSpan.FromSeconds(5));
            var watch = Stopwatch.StartNew();
            await cached.GetAsync();
            elapsed.Add(Math.Round(watch.Elapsed.TotalMilliseconds, 2));
        }
        Console.WriteLine(JsonSerializer.Serialize(new { fullQueries, pollCount = elapsed.Count, elapsedMs = elapsed }));
    }

    private static (DiagnosticsResult, string) Report(int value) => (new DiagnosticsResult(), value.ToString());

    private static async Task ExpectCancellationAsync(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine($"PASS: {message}");
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
