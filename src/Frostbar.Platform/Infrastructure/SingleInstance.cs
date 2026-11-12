using System.IO.Pipes;
using System.Text;

namespace Frostbar.Platform.Infrastructure;

/// <summary>One shell per session: a named mutex, plus a pipe the second launch uses to hand over its command.</summary>
public sealed class SingleInstance : IDisposable
{
    readonly Mutex _mutex;
    public bool IsPrimary { get; private set; }

    SingleInstance(Mutex m, bool primary) { _mutex = m; IsPrimary = primary; }

    public static SingleInstance Acquire()
    {
        var m = new Mutex(false, AppPaths.MutexName);
        bool owned;
        try { owned = m.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; } // the previous owner crashed: it is ours now
        return new SingleInstance(m, owned);
    }

    /// <summary>For --replace: wait until the running instance has quit and released the mutex.</summary>
    public bool WaitForPrimary(TimeSpan timeout)
    {
        try { IsPrimary = _mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        return IsPrimary;
    }

    /// <summary>Sends one command line to the running instance; returns its reply or null.</summary>
    public static string? Send(string pipeName, string command, int timeoutMs = 3000)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            c.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(command.Replace('\n', ' ') + "\n");
            c.Write(bytes);
            c.Flush();
            using var r = new StreamReader(c, Encoding.UTF8);
            var task = r.ReadLineAsync();
            return task.Wait(timeoutMs) ? task.Result : null;
        }
        catch (TimeoutException) { return null; }
        catch (IOException) { return null; }
    }

    public void Dispose()
    {
        if (IsPrimary) { try { _mutex.ReleaseMutex(); } catch (ApplicationException) { } }
        _mutex.Dispose();
    }
}

/// <summary>
/// A line-based command pipe (current user only). Each connection sends one line and gets one line back; the handler
/// runs on the UI thread through <paramref name="dispatch"/>.
/// </summary>
public sealed class CommandServer : IDisposable
{
    readonly string _name;
    readonly Func<string, Task<string>> _handle;
    readonly CancellationTokenSource _cts = new();

    public CommandServer(string name, Func<string, Task<string>> handle)
    {
        _name = name;
        _handle = handle;
        _ = Task.Run(Loop);
    }

    async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var s = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await s.WaitForConnectionAsync(_cts.Token);
                using var r = new StreamReader(s, Encoding.UTF8, leaveOpen: true);
                var line = await r.ReadLineAsync(_cts.Token);
                string reply;
                try { reply = line == null ? "error: empty" : await _handle(line.Trim()); }
                catch (Exception ex) { reply = "error: " + ex.Message; Log.Warn($"pipe {_name}: command failed", ex); }
                // Not cancellable: "quit" stops the server right after answering, and the answer must still go out.
                var bytes = Encoding.UTF8.GetBytes(reply.Replace('\n', ' ') + "\n");
                await s.WriteAsync(bytes, CancellationToken.None);
                await s.FlushAsync(CancellationToken.None);
                s.WaitForPipeDrain();
            }
            catch (OperationCanceledException) { return; }
            catch (IOException ex) { Log.Debug($"pipe {_name}: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { Log.Warn($"pipe {_name} busy (another instance?)", ex); await Task.Delay(1000); }
        }
    }

    public void Dispose() => _cts.Cancel();
}
