using Frostbar.Platform.Interop;
using Microsoft.Win32;

namespace Frostbar.Platform.Infrastructure;

/// <summary>
/// Calls back when a key under HKCU (and the keys under it) changes: values written or deleted, keys made or removed.
/// Windows signals an event (RegNotifyChangeKeyValue, asynchronous and thread-agnostic: the request does not end with
/// the pool thread that made it); a pool wait asks again and calls back, on that pool thread. Nothing polls.
/// </summary>
public sealed class RegistryWatcher : IDisposable
{
    readonly RegistryKey _key;
    readonly AutoResetEvent _signal = new(false);
    readonly RegisteredWaitHandle _wait;
    readonly Action _changed;
    volatile bool _disposed;

    /// <summary>Null when the key cannot be opened (it may not exist yet).</summary>
    public static RegistryWatcher? Open(string path, Action changed)
    {
        var key = Registry.CurrentUser.OpenSubKey(path);
        return key == null ? null : new RegistryWatcher(key, changed);
    }

    RegistryWatcher(RegistryKey key, Action changed)
    {
        _key = key;
        _changed = changed;
        Ask();
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) =>
        {
            if (_disposed) return;
            // Inside the catch too: disposed between the check and here (the shell stopping), the closed key threw on
            // this pool thread, which ends the process.
            try
            {
                Ask(); // before calling back: a change during the callback is not missed
                _changed();
            }
            catch (Exception ex) when (_disposed && ex is ObjectDisposedException) { }
            catch (Exception ex) { Log.Warn("registry watcher: the callback failed", ex); }
        }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    void Ask()
    {
        int r = Native.RegNotifyChangeKeyValue(_key.Handle, subtree: true,
            Native.REG_NOTIFY_CHANGE_NAME | Native.REG_NOTIFY_CHANGE_LAST_SET | Native.REG_NOTIFY_THREAD_AGNOSTIC, _signal.SafeWaitHandle, asynchronous: true);
        if (r != 0) Log.Warn($"registry watcher: not watched ({r})");
    }

    public void Dispose()
    {
        _disposed = true;
        _wait.Unregister(null);
        _key.Dispose(); // closing the key ends the request
        _signal.Dispose();
    }
}
