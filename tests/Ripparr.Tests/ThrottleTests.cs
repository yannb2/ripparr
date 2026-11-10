using Ripparr.Components;

namespace Ripparr.Tests;

[TestClass]
public class ThrottleTests
{
    [TestMethod]
    public void TheLastChangeOfABurstIsNotLost()
    {
        // A job's last progress tick, then "finished" a moment later: the page must reload after both.
        var runs = 0;
        using var again = new ManualResetEventSlim();
        using var throttle = new Throttle(TimeSpan.FromMilliseconds(300), () =>
        {
            if (Interlocked.Increment(ref runs) == 2)
            {
                again.Set();
            }
        });

        throttle.Poke();
        Assert.AreEqual(1, Volatile.Read(ref runs), "The first change runs at once.");

        throttle.Poke();
        throttle.Poke();
        throttle.Poke();
        Assert.AreEqual(1, Volatile.Read(ref runs), "Changes inside the interval wait.");

        Assert.IsTrue(again.Wait(TimeSpan.FromSeconds(5)), "The waiting changes run once the interval is up.");
        Thread.Sleep(500);
        Assert.AreEqual(2, Volatile.Read(ref runs), "A burst runs once more, not once per change.");
    }

    [TestMethod]
    public void NothingRunsAfterDispose()
    {
        var runs = 0;
        var throttle = new Throttle(TimeSpan.FromMilliseconds(200), () => Interlocked.Increment(ref runs));
        throttle.Poke();
        throttle.Poke();
        throttle.Dispose();
        throttle.Poke();
        Thread.Sleep(500);
        Assert.AreEqual(1, Volatile.Read(ref runs));
    }

    [TestMethod]
    public void AFailingActionDoesNotThrow()
    {
        using var throttle = new Throttle(TimeSpan.FromMilliseconds(50), () => throw new ObjectDisposedException("page"));
        throttle.Poke();
        throttle.Poke();
        Thread.Sleep(200);
    }
}
