using Frostbar.Core.Search;
using Frostbar.Platform.Infrastructure;
using Windows.ApplicationModel.DataTransfer;

namespace Frostbar.Platform.Search;

/// <summary>Where the search's Clipboard filter reads the history and puts an item back (stage 6, E3c).</summary>
public interface IClipboardHistory
{
    /// <summary>The history now (on the calling thread's context; the UI thread's in the shell).</summary>
    Task<(ClipboardState State, IReadOnlyList<ClipboardItem> Items)> ReadAsync();

    /// <summary>Puts an item of the last read back on the clipboard, to paste; false when it cannot.</summary>
    bool PutBack(string id);
}

/// <summary>
/// Windows' clipboard history (Win+V's; the user's choice "a", 2026-10-16), read when the Clipboard filter is turned on.
/// Off unless the user turned it on in Windows' settings (S25: off on this PC, GetHistoryItemsAsync answering
/// ClipboardHistoryDisabled in 41 ms from a process without a package). Only the newest items are read, a text's first
/// characters at most: nothing is written anywhere.
/// </summary>
public sealed class WindowsClipboardHistory : IClipboardHistory
{
    /// <summary>A text item's characters kept to search and show (a long copy is cut).</summary>
    const int TextKept = 4000;

    readonly Dictionary<string, ClipboardHistoryItem> _items = new();

    public async Task<(ClipboardState State, IReadOnlyList<ClipboardItem> Items)> ReadAsync()
    {
        try
        {
            if (!Clipboard.IsHistoryEnabled()) return (ClipboardState.Off, []);
            var result = await Clipboard.GetHistoryItemsAsync();
            if (result.Status != ClipboardHistoryItemsResultStatus.Success)
                return (result.Status == ClipboardHistoryItemsResultStatus.ClipboardHistoryDisabled ? ClipboardState.Off : ClipboardState.Unavailable, []);
            _items.Clear();
            var items = new List<ClipboardItem>();
            foreach (var item in result.Items.Take(SearchRanker.MaxFiltered))
            {
                var (kind, text) = await What(item.Content);
                items.Add(new ClipboardItem(item.Id, kind, text, item.Timestamp));
                _items[item.Id] = item;
            }
            return (ClipboardState.On, items);
        }
        catch (Exception ex)
        {
            Log.Warn("clipboard: the history could not be read", ex);
            return (ClipboardState.Unavailable, []);
        }
    }

    static async Task<(ClipboardKind, string)> What(DataPackageView view)
    {
        try
        {
            if (view.Contains(StandardDataFormats.Text))
            {
                string text = await view.GetTextAsync();
                return (ClipboardKind.Text, text.Length > TextKept ? text[..TextKept] : text);
            }
            if (view.Contains(StandardDataFormats.StorageItems))
            {
                var files = await view.GetStorageItemsAsync();
                return (ClipboardKind.Files, string.Join(", ", files.Select(f => f.Name)));
            }
            if (view.Contains(StandardDataFormats.Bitmap)) return (ClipboardKind.Image, "");
        }
        catch (Exception ex) { Log.Debug("clipboard: an item unread: " + ex.Message); }
        return (ClipboardKind.Other, "");
    }

    public bool PutBack(string id)
    {
        try { return _items.TryGetValue(id, out var item) && Clipboard.SetHistoryItemAsContent(item) == SetHistoryItemAsContentStatus.Success; }
        catch (Exception ex)
        {
            Log.Warn("clipboard: the item could not be put back", ex);
            return false;
        }
    }
}

/// <summary>
/// A spec's shell's own history (FROSTBAR_E2E_CLIPBOARD=spec, honoured with --diag only): set through the diagnostics pipe
/// (clipboard-set), an item put back only noted: the user's clipboard is neither read nor changed.
/// </summary>
public sealed class SpecClipboardHistory : IClipboardHistory
{
    public ClipboardState State { get; set; } = ClipboardState.On;
    public List<ClipboardItem> Items { get; set; } = new();
    /// <summary>The items put back, in order (diagnostics).</summary>
    public List<string> PutBackIds { get; } = new();
    public int Reads { get; private set; }

    public Task<(ClipboardState State, IReadOnlyList<ClipboardItem> Items)> ReadAsync()
    {
        Reads++;
        return Task.FromResult<(ClipboardState, IReadOnlyList<ClipboardItem>)>((State, State == ClipboardState.On ? [.. Items] : []));
    }

    public bool PutBack(string id)
    {
        if (State != ClipboardState.On || Items.All(i => i.Id != id)) return false;
        PutBackIds.Add(id);
        return true;
    }
}
