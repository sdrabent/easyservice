using EasyService.Core;

namespace EasyService.Tests;

/// <summary>
/// Tests for the machine-wide lock that keeps two deployments from writing the same
/// service definition at once. The property that matters is exclusion, so both tests
/// look at what a second thread can do while the first holds the lock.
/// </summary>
internal static class LockTests
{
    // Eigener Name statt des echten: sonst faellt der Test um, wenn jemand waehrenddessen
    // einen Dienst anlegt.
    private static readonly string Name = @"Global\EasyService.Tests." + Guid.NewGuid().ToString("N")[..8];

    public static IEnumerable<(string Name, Action Test)> All()
    {
        yield return ("Zwei Änderungen laufen nacheinander", SecondWaiterIsBlocked);
        yield return ("Nach der Freigabe kommt der Wartende dran", LockIsHandedOver);
    }

    private static void SecondWaiterIsBlocked()
    {
        using var held = MachineLock.Acquire(Name, TimeSpan.FromSeconds(5));

        // Aus einem anderen Thread, weil ein Mutex denselben Thread wieder hereinlaesst.
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var _ = MachineLock.Acquire(Name, TimeSpan.FromMilliseconds(300));
                caught = null;
            }
            catch (Exception e) { caught = e; }
        });
        thread.Start();
        Assert(thread.Join(TimeSpan.FromSeconds(10)), "der zweite Thread haengt");

        Assert(caught is TimeoutException, $"erwartet: TimeoutException, bekommen: {caught?.GetType().Name ?? "kein Fehler"}");
        Assert(caught!.Message.Contains("EasyService"), $"unbrauchbare Meldung: {caught.Message}");
    }

    private static void LockIsHandedOver()
    {
        var acquired = new ManualResetEventSlim(false);
        var held = MachineLock.Acquire(Name, TimeSpan.FromSeconds(5));

        var thread = new Thread(() =>
        {
            using var _ = MachineLock.Acquire(Name, TimeSpan.FromSeconds(10));
            acquired.Set();
        });
        thread.Start();

        Assert(!acquired.Wait(TimeSpan.FromMilliseconds(300)), "die Sperre hat nicht gesperrt");
        held.Dispose();

        Assert(acquired.Wait(TimeSpan.FromSeconds(10)), "der Wartende kam nach der Freigabe nicht dran");
        Assert(thread.Join(TimeSpan.FromSeconds(10)), "der zweite Thread haengt");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
