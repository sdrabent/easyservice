using EasyService.Resources;

namespace EasyService.Core;

/// <summary>
/// One machine-wide lock around changing a service definition.
///
/// Creating a service is not one operation: CreateService, then four ChangeServiceConfig2
/// calls, then the Parameters key. Two of those runs at the same time - Ansible with
/// several forks, or the window and the command line at once - can interleave and leave a
/// half-written definition behind. The SCM does not serialise this for us.
///
/// Global\ so it spans sessions: the window runs in the interactive session, a deployment
/// runs in session 0.
/// </summary>
public static class MachineLock
{
    private const string Name = @"Global\EasyService.Config";

    public static IDisposable Acquire(TimeSpan timeout) => Acquire(Name, timeout);

    /// <summary>Same lock under a different name, so tests do not fight a real deployment.</summary>
    internal static IDisposable Acquire(string name, TimeSpan timeout)
    {
        var mutex = new Mutex(false, name);
        try
        {
            if (!mutex.WaitOne(timeout))
            {
                mutex.Dispose();
                throw new TimeoutException(S.Svc_Err_Busy((int)timeout.TotalSeconds));
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died without releasing. We now own the lock; whatever it
            // was writing is the caller's problem, not ours.
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        return new Release(mutex);
    }

    /// <summary>Default wait: long enough for a slow SCM call, short enough to not hang a deployment.</summary>
    public static IDisposable Acquire() => Acquire(TimeSpan.FromSeconds(30));

    private sealed class Release : IDisposable
    {
        private readonly Mutex _mutex;
        private bool _released;

        public Release(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            if (_released) return;
            _released = true;
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned any more */ }
            _mutex.Dispose();
        }
    }
}
