using System.Diagnostics;
using System.Runtime.InteropServices;
using Frostbar.Platform.Infrastructure;

namespace Frostbar.Platform.Search;

/// <summary>
/// The search's file source on a thread of its own (STA: ADO's objects are apartment-threaded, and from a pool thread
/// COM would run every call on the process's main STA, the UI thread). Only the latest query matters: one that arrives
/// while another runs replaces any still waiting. "auto" asks Everything when it is running, else Windows Search.
/// </summary>
public sealed class FileSearch : IDisposable
{
    /// <summary>An answer: the query's id, what was found (null: the source failed), which source, how long it took.</summary>
    public sealed record Answer(int Id, string Text, List<FoundItem>? Items, string Source, long Ms);

    readonly Thread _thread;
    readonly AutoResetEvent _wake = new(false);
    readonly object _lock = new();
    // Made with the shell on purpose, though a shell that never searches files does not need it. Made at the first query
    // instead, the menu bar's first showing took 260–280 ms instead of 50–60 and the start-up about 150 ms longer
    // (2026-11-08, `startup` with the builds in turns). Its thread's WPF dispatcher and window seem to get something of
    // WPF's going ahead of the UI thread's first window; what was not looked into.
    readonly EverythingClient _everything = new();
    (int id, string text, string source, int max, Action<Answer> done)? _next;
    volatile bool _stop;

    public FileSearch()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "file search" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <param name="source">"auto", "everything" or "windowsSearch" (settings).</param>
    /// <param name="done">Called on the search thread.</param>
    public void Query(int id, string text, string source, int max, Action<Answer> done)
    {
        lock (_lock) _next = (id, text, source, max, done);
        _wake.Set();
    }

    void Loop()
    {
        while (!_stop)
        {
            _wake.WaitOne();
            while (true)
            {
                (int id, string text, string source, int max, Action<Answer> done) job;
                lock (_lock)
                {
                    if (_next == null) break;
                    job = _next.Value;
                    _next = null;
                }
                var sw = Stopwatch.StartNew();
                string used = job.source;
                List<FoundItem>? items = null;
                try
                {
                    if (used == "auto") used = EverythingClient.Available() ? "everything" : "windowsSearch";
                    items = used == "everything" ? _everything.Search(job.text, job.max) : WindowsSearch(job.text, job.max);
                }
                catch (Exception ex) { Log.Warn($"file search ({used}) failed", ex); }
                try { job.done(new Answer(job.id, job.text, items, used, sw.ElapsedMilliseconds)); }
                catch (Exception ex) { Log.Warn("file search answer failed", ex); }
            }
        }
    }

    const string IndexConnection = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    /// <summary>
    /// Windows Search (the system index) when Everything is not there: its OLE DB provider through ADO (part of
    /// Windows; late-bound COM, no package). Names only, newest first, where Windows indexes (the user's folders).
    /// </summary>
    static List<FoundItem>? WindowsSearch(string text, int max)
    {
        string q = text.Trim();
        if (q.Length == 0) return [];
        string sql = $"SELECT TOP {Math.Clamp(max, 1, 500)} System.ItemUrl, System.DateModified, System.ItemType FROM SystemIndex " +
                     $"WHERE SCOPE='file:' AND System.FileName LIKE '{Like(q)}' ORDER BY System.DateModified DESC";
        object? connection = null, records = null;
        try
        {
            var type = Type.GetTypeFromProgID("ADODB.Connection");
            if (type == null) return null;
            dynamic c = Activator.CreateInstance(type)!;
            connection = c;
            c.Open(IndexConnection);
            dynamic r = c.Execute(sql);
            records = r;
            var list = new List<FoundItem>();
            while (!(bool)r.EOF)
            {
                string? url = r.Fields.Item(0).Value as string;
                object modified = r.Fields.Item(1).Value;
                string? kind = r.Fields.Item(2).Value as string;
                if (url != null && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    list.Add(new FoundItem(url[5..].Replace('/', '\\'), string.Equals(kind, "Directory", StringComparison.OrdinalIgnoreCase),
                        modified is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : default)); // the index keeps UTC without saying so
                r.MoveNext();
            }
            return list;
        }
        catch (Exception ex) { Log.Debug("windows search: " + ex.Message); return null; }
        finally
        {
            try { if (records != null) ((dynamic)records).Close(); } catch (Exception) { }
            try { if (connection != null) ((dynamic)connection).Close(); } catch (Exception) { }
            if (records != null) Marshal.ReleaseComObject(records);
            if (connection != null) Marshal.ReleaseComObject(connection);
        }
    }

    /// <summary>A LIKE pattern for a plain substring: %, _ and [ taken literally, ' doubled.</summary>
    static string Like(string q)
    {
        var sb = new System.Text.StringBuilder("%");
        foreach (char c in q)
        {
            if (c is '%' or '_' or '[') sb.Append('[').Append(c).Append(']');
            else if (c == '\'') sb.Append("''");
            else sb.Append(c);
        }
        return sb.Append('%').ToString();
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _everything.Dispose();
    }
}
