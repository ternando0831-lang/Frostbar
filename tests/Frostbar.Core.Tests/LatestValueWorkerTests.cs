using Frostbar.Core.Concurrency;
using Xunit;

namespace Frostbar.Core.Tests;

public class LatestValueWorkerTests
{
    [Fact]
    public void ValuesAskedForWhileOneIsAppliedCollapseIntoTheLast()
    {
        var applied = new List<int>();
        LatestValueWorker<int>? w = null;
        w = new LatestValueWorker<int>(v =>
        {
            applied.Add(v);
            if (v == 1) { w!.Set(2); w.Set(3); w.Set(4); } // a slider dragged on while the first value is being set
        }, start: work => work());
        w.Set(1);
        Assert.Equal([1, 4], applied);
    }

    [Fact]
    public void ValuesAskedForOneAfterAnotherAreEachApplied()
    {
        var applied = new List<int>();
        var w = new LatestValueWorker<int>(applied.Add, start: work => work());
        w.Set(10);
        w.Set(20);
        Assert.Equal([10, 20], applied);
    }

    [Fact]
    public void AFailureDoesNotStopTheWorker()
    {
        var applied = new List<int>();
        var errors = new List<Exception>();
        LatestValueWorker<int>? w = null;
        w = new LatestValueWorker<int>(v =>
        {
            if (v == 1) { w!.Set(2); throw new InvalidOperationException("WMI said no"); }
            applied.Add(v);
        }, start: work => work(), failed: errors.Add);
        w.Set(1);
        Assert.Equal([2], applied);
        Assert.Single(errors);
        w.Set(5);
        Assert.Equal([2, 5], applied);
    }

    [Fact]
    public void WaitReturnsOnceTheValuesAskedForAreApplied()
    {
        using var go = new ManualResetEventSlim();
        string? last = null;
        var ct = TestContext.Current.CancellationToken;
        var w = new LatestValueWorker<string>(v => { go.Wait(TimeSpan.FromSeconds(10), ct); Volatile.Write(ref last, v); });
        Assert.True(w.Wait(TimeSpan.Zero)); // nothing asked for yet
        w.Set("first");
        w.Set("second");
        Assert.False(w.Wait(TimeSpan.FromMilliseconds(100)));
        go.Set();
        Assert.True(w.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal("second", Volatile.Read(ref last));
    }

    [Fact]
    public async Task OnThePoolTheLastValueAlwaysLands()
    {
        int last = -1, calls = 0;
        var w = new LatestValueWorker<int>(v => { Thread.Sleep(2); Volatile.Write(ref last, v); Interlocked.Increment(ref calls); });
        for (int i = 0; i <= 200; i++) w.Set(i);
        for (int t = 0; t < 200 && Volatile.Read(ref last) != 200; t++) await Task.Delay(10);
        Assert.Equal(200, Volatile.Read(ref last));
        Assert.True(Volatile.Read(ref calls) < 200, $"{calls} calls: the steps in between should have been skipped");
    }
}
