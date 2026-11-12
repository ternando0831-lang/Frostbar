using System.Diagnostics;
using Frostbar.Core.Concurrency;

namespace Frostbar.Core.Settings;

/// <summary>
/// settings.json saved off the caller's thread. On the UI thread a save took 22 to 115 ms (2026-10-24, save-bench),
/// nearly all of it File.Replace, about as long as something else held the .tmp just written; once the Dock's save
/// (this and its AppIDs) took 5 s there, the cause not found. The settings change on the caller's thread, so they are serialised there (under
/// 1 ms once warm) and the text goes to one writer, which writes in turn and, of the texts waiting, only the newest
/// (each is the whole of the settings). Before the process ends or another instance reads the file: <see cref="Wait"/>.
/// </summary>
public sealed class SettingsSaver
{
    /// <summary>A save held off by a reader (ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION) is tried this many times in all.</summary>
    public const int Tries = 5;
    public const int RetryMs = 100;
    /// <summary>A write taking this long or longer is told (<c>slow</c>).</summary>
    public const int SlowMs = 300;

    readonly string _path;
    readonly Func<string, string, bool> _write;
    readonly Action<string, Exception?> _failed;
    readonly Action<long, int>? _slow;
    readonly LatestValueWorker<string> _writer;

    /// <param name="failed">Told on the writer's thread when a save did not happen (and why).</param>
    /// <param name="slow">Told on the writer's thread when a write took <see cref="SlowMs"/> or more: the ms, and the tries.</param>
    /// <param name="write">Writes a text (default <see cref="SettingsStore.Write"/>).</param>
    /// <param name="start">How the writer starts (default: the thread pool).</param>
    public SettingsSaver(string path, Action<string, Exception?> failed, Action<long, int>? slow = null,
        Func<string, string, bool>? write = null, Action<Action>? start = null)
    {
        _path = path;
        _failed = failed;
        _slow = slow;
        _write = write ?? SettingsStore.Write;
        _writer = new LatestValueWorker<string>(Write, start, ex => _failed("saving failed", ex));
    }

    /// <summary>Serialises the settings here and has them written.</summary>
    public void Save(AppSettings s) => _writer.Set(SettingsStore.Text(s));

    /// <summary>Waits for the saves asked for: false when <paramref name="timeout"/> ran out first.</summary>
    public bool Wait(TimeSpan timeout) => _writer.Wait(timeout);

    void Write(string text)
    {
        var took = Stopwatch.StartNew();
        for (int tries = 1; ; tries++)
        {
            try
            {
                if (!_write(_path, text)) _failed("not saved: settings.json could not be read at start, and the defaults in use must not replace it", null);
                else if (took.ElapsedMilliseconds >= SlowMs) _slow?.Invoke(took.ElapsedMilliseconds, tries);
                return;
            }
            catch (IOException e) when (tries < Tries && (e.HResult & 0xFFFF) is 32 or 33) { Thread.Sleep(RetryMs); }
        }
    }
}
