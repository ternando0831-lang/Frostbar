namespace Frostbar.App.AppGrid;

/// <summary>
/// Where an app carried out of the app grid can be kept: the Dock. Points are on the screen, in pixels; apps are named
/// by their AppsFolder parsing name (their AppID).
/// </summary>
public interface IAppShelf
{
    /// <summary>The app is over (<paramref name="x"/>, <paramref name="y"/>): true while it would be kept there.</summary>
    bool Carry(string appId, int x, int y);

    /// <summary>The carrying ended without a drop (cancelled, or the grid is closing).</summary>
    void CarryAway();

    /// <summary>
    /// (<paramref name="x"/>, <paramref name="y"/>) is over the Dock, a gap open there or not: the grid turns no page
    /// there (a Dock on a side of the screen stands in the grid's side margin, where a carried app turns the page).
    /// </summary>
    bool Covers(int x, int y);

    /// <summary>
    /// The app was let go at (<paramref name="x"/>, <paramref name="y"/>), its picture centred on
    /// (<paramref name="pictureX"/>, <paramref name="pictureY"/>) and <paramref name="sizePx"/> wide (it is held where it
    /// was taken, not by its centre): true when it is kept there now (its picture then flies from there into its place).
    /// </summary>
    bool Drop(string appId, string name, int x, int y, int pictureX, int pictureY, double sizePx);

    /// <summary>The app is kept there (the grid's right-click menu offers to take it out, else to keep it).</summary>
    bool Keeps(string appId);

    /// <summary>The app kept there, after the kept apps (the grid's menu: "Keep in Dock").</summary>
    void Keep(string appId, string name);

    /// <summary>The app no longer kept there (the grid's menu: "Remove from Dock").</summary>
    void Remove(string appId);
}
