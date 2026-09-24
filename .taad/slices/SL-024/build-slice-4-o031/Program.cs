// SL-024 build attempt 4, evidence for O-031: calls the App's own MainWindow.UnlessCancelled, as built,
// with a volume query that blocks the way GetDiskFreeSpaceEx does on a stalled mapped drive, and times
// when the window's wait ends.
using System.Diagnostics;
using System.Reflection;

string appDll = args[0];
Assembly app = Assembly.LoadFrom(appDll);
MethodInfo unlessCancelled = app.GetType("FsAnalyser.App.MainWindow", throwOnError: true)!
    .GetMethod("UnlessCancelled", BindingFlags.NonPublic | BindingFlags.Static)
    ?? throw new InvalidOperationException("MainWindow.UnlessCancelled not found");
Task<long?> Call(Func<long?> query, CancellationToken token) => (Task<long?>)unlessCancelled.Invoke(null, [query, token])!;
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

// 1. Cancel requested before the query: it is never started.
{
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    bool started = false;
    var sw = Stopwatch.StartNew();
    long? r = await Call(() => { started = true; return 1; }, cts.Token);
    Check(r is null && !started, $"cancelled before: result {r?.ToString() ?? "null"}, query started {started}, {sw.Elapsed.TotalMilliseconds:F1} ms");
}

// 2. Cancel arrives while the query is blocked (a stalled share holds it 62-66 s; 30 s here).
{
    using var cts = new CancellationTokenSource();
    using var release = new ManualResetEventSlim();
    using var queryStarted = new ManualResetEventSlim();
    Task<long?> waiting = Call(() => { queryStarted.Set(); release.Wait(TimeSpan.FromSeconds(30)); return 7; }, cts.Token);
    queryStarted.Wait();
    Thread.Sleep(300);
    var sw = Stopwatch.StartNew();
    cts.Cancel();
    bool ended = waiting.Wait(TimeSpan.FromSeconds(2));
    TimeSpan took = sw.Elapsed;
    Check(ended && waiting.Result is null, $"cancelled during: the wait ended {took.TotalMilliseconds:F1} ms after Cancel with {(ended ? waiting.Result?.ToString() ?? "null" : "nothing")}, the query still running: {!release.IsSet}");
    release.Set();
}

// 3. No cancel: the query's answer is waited for and returned.
{
    using var cts = new CancellationTokenSource();
    var sw = Stopwatch.StartNew();
    long? r = await Call(() => { Thread.Sleep(200); return 42; }, cts.Token);
    Check(r == 42, $"not cancelled: result {r?.ToString() ?? "null"} after {sw.Elapsed.TotalMilliseconds:F0} ms");
}

// 4. The query runs on its own thread, not the caller's.
{
    using var cts = new CancellationTokenSource();
    int caller = Environment.CurrentManagedThreadId, ran = 0;
    await Call(() => { ran = Environment.CurrentManagedThreadId; return 0; }, cts.Token);
    Check(ran != caller && ran != 0, $"own thread: caller {caller}, query ran on {ran}");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures;
