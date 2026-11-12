using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Frostbar.Platform.Infrastructure;

public enum LogLevel { Error, Warn, Info, Debug }

/// <summary>
/// Small rolling file log (one file per day, 7 days kept), written by a background thread.
/// Window titles and file paths are personal data: log them with <see cref="Redact"/> unless at Debug level.
/// </summary>
public static class Log
{
    static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 20_000);
    static readonly ManualResetEventSlim Idle = new(true);
    static Thread? _writer;
    static string _dir = "", _prefix = "frostbar";
    public static LogLevel Level { get; set; } = LogLevel.Info;

    public static void Init(string dir, string prefix, LogLevel level)
    {
        _dir = dir; _prefix = prefix; Level = level;
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var f in Directory.GetFiles(dir, prefix + "-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _writer ??= new Thread(Write) { IsBackground = true, Name = "log" };
        if (!_writer.IsAlive) _writer.Start();
    }

    public static LogLevel Parse(string? s) => s switch { "error" => LogLevel.Error, "warn" => LogLevel.Warn, "debug" => LogLevel.Debug, _ => LogLevel.Info };

    public static void Error(string msg, Exception? ex = null) => Add(LogLevel.Error, msg, ex);
    public static void Warn(string msg, Exception? ex = null) => Add(LogLevel.Warn, msg, ex);
    public static void Info(string msg) => Add(LogLevel.Info, msg, null);
    public static void Debug(string msg) => Add(LogLevel.Debug, msg, null);

    /// <summary>"&lt;12 chars&gt;" instead of the text itself, except at Debug level.</summary>
    public static string Redact(string? s) => s == null ? "null" : Level >= LogLevel.Debug ? s : $"<{s.Length} chars>";

    static void Add(LogLevel level, string msg, Exception? ex)
    {
        if (level > Level || _writer == null) return; // not initialised (unit tests, early startup)
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ')
          .Append(level.ToString().ToUpperInvariant().PadRight(5)).Append(' ')
          .Append('[').Append(Environment.CurrentManagedThreadId).Append("] ").Append(msg);
        if (ex != null) sb.Append(Environment.NewLine).Append(ex);
        Idle.Reset();
        if (!Queue.TryAdd(sb.ToString())) Idle.Set();
    }

    /// <summary>Waits until everything queued so far is on disk.</summary>
    public static void Flush(int timeoutMs = 2000) => Idle.Wait(timeoutMs);

    /// <summary>OS id of the writer thread (diagnostics).</summary>
    public static uint ThreadId { get; private set; }

    static void Write()
    {
        ThreadId = Interop.Native.GetCurrentThreadId();
        StreamWriter? w = null;
        string day = "";
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                string today = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (w == null || today != day)
                {
                    w?.Dispose();
                    day = today;
                    var fs = new FileStream(Path.Combine(_dir, $"{_prefix}-{day}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    w = new StreamWriter(fs, new UTF8Encoding(false));
                }
                w.WriteLine(line);
                if (Queue.Count == 0) { w.Flush(); Idle.Set(); }
            }
            // Any failure (a log file made read-only: UnauthorizedAccess) costs that line, not the process: an exception
            // left this thread and ended the shell or the watchdog. The file is opened again for the next line.
            catch (Exception)
            {
                try { w?.Dispose(); } catch (Exception) { }
                w = null;
                Idle.Set();
            }
        }
    }
}
