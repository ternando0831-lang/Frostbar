namespace Frostbar.Core.Shell;

/// <summary>A rectangle in the island's own measure (DIPs): x from the camera's middle (the screen's), y from the island's top.</summary>
public readonly record struct IslandBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double MiddleX => X + Width / 2;
    public double MiddleY => Y + Height / 2;
}

/// <summary>
/// Where each part of the island is for one of its faces: the island itself and its corners; the picture (a record's
/// sleeve, an app's icon) and the corners that keep it concentric with the island; the middle of what stands at the
/// trailing end (a level meter, a ring); the detached circle of a second activity.
/// </summary>
public sealed record IslandPlan(IslandBox Body, double Radius, IslandBox? Picture, double PictureRadius, (double X, double Y)? End, IslandBox? Second);

/// <summary>
/// The Dynamic Island's measures after Apple's Human Interface Guidelines (Live Activities, read 2026-11-08; its
/// change of 2026-10-13: "macOS: Use the provided iOS dimensions"), at the menu bar's height (the user's choice "A
/// 高さ 22" from Lab mock island2, 2026-11-08). The iPhone's island is 126 × 37.33 points and each compact end 52.33
/// for 36.67: here the same proportions 22 high. What the guidelines ask and this keeps: the compact presentation
/// "as narrow as possible", "snug against the TrueDepth camera", its two ends "similarly sized"; two activities as
/// minimal presentations, "one attached to the Dynamic Island" and one "detached"; "even, matching margins", a rounded
/// element near a corner matching the island's corners (ContainerRelativeShape); the expanded presentation a fixed
/// width (371 on the 393-point iPhones) and only the height its content needs (84 to 160). The camera's place (the
/// idle island) stays at the screen's middle in every face: nothing the island holds goes there but words.
/// </summary>
public static class IslandLayout
{
    /// <summary>The island's height: its row in the 24-DIP menu bar (a DIP under the screen's top).</summary>
    public const double Height = 22;

    /// <summary>The idle island (the camera's place): 126 / 37.33 of its height.</summary>
    public const double Camera = 76;

    /// <summary>Each compact end beside the camera: 52.33 / 36.67 of the height.</summary>
    public const double Side = 32;

    /// <summary>What an end holds; its inset (22 − 16) / 2 = 3 all round, so its corners are the island's 11 less 3.</summary>
    public const double Picture = 16;

    /// <summary>Between the island and the detached circle (the iPhone's look; not in the guidelines).</summary>
    public const double Gap = 4;

    /// <summary>The expanded island: its width (the guidelines' 371), its corners, its margin all round.</summary>
    public const double OpenWidth = 371, OpenRadius = 30, Margin = 18;

    /// <summary>The expanded picture: a record's sleeve, an app's icon.</summary>
    public const double Art = 52, Icon = 40;

    /// <summary>What plays opened: the sleeve with its words, 14 under it the row of buttons (30 high).</summary>
    public const double PlayingOpenHeight = Margin + Art + 14 + 30 + Margin;

    /// <summary>A progress opened: the icon with its words, 12 under it the track (4 high).</summary>
    public const double ProgressOpenHeight = Margin + Icon + 12 + 4 + Margin;

    /// <summary>The notices (their words across the middle: there is no camera there on a PC): with two lines, with one.</summary>
    public const double NoticeTall = 46, NoticeTallWidth = 300, NoticeBandWidth = 240;

    /// <summary>The level meter at the trailing end (five bars two wide, two apart).</summary>
    public const double MeterWidth = 18;

    /// <summary>
    /// The plan for a face: <paramref name="shape"/> with its main activity's <paramref name="main"/> kind, whether a
    /// second goes beside it, and the notice's kind.
    /// </summary>
    public static IslandPlan For(IslandShape shape, IslandKind? main, bool second, NoticeKind? notice)
    {
        const double inset = (Height - Picture) / 2, end = Height / 2, r = Height / 2;
        switch (shape)
        {
            case IslandShape.Compact when main != null && second:
            {
                // Two activities: the main one attached at the island's leading end, the island grown on that side only (the
                // camera stays where it is), the second detached beside its trailing end.
                var body = new IslandBox(-Camera / 2 - Height, 0, Camera + Height, Height);
                return new IslandPlan(body, r, new IslandBox(body.X + inset, inset, Picture, Picture), r - inset, null,
                    new IslandBox(Camera / 2 + Gap, 0, Height, Height));
            }
            case IslandShape.Compact when main != null:
            {
                var body = new IslandBox(-Camera / 2 - Side, 0, Camera + 2 * Side, Height);
                // The picture and what stands at the trailing end mirror each other: each middle half the height in from its end.
                return new IslandPlan(body, r, new IslandBox(body.X + inset, inset, Picture, Picture), r - inset, (body.Right - end, end), null);
            }
            case IslandShape.Notice:
            {
                bool tall = notice is NoticeKind.Headphones or NoticeKind.Battery;
                double w = tall ? NoticeTallWidth : NoticeBandWidth, h = tall ? NoticeTall : Height;
                return new IslandPlan(new IslandBox(-w / 2, 0, w, h), h / 2, null, 0, null, null);
            }
            case IslandShape.Open when main != null:
            {
                bool playing = main == IslandKind.Playing;
                double h = playing ? PlayingOpenHeight : ProgressOpenHeight, art = playing ? Art : Icon;
                var body = new IslandBox(-OpenWidth / 2, 0, OpenWidth, h);
                (double, double)? meter = playing ? (body.Right - Margin - MeterWidth / 2, Margin + Art / 2) : null;
                return new IslandPlan(body, OpenRadius, new IslandBox(body.X + Margin, Margin, art, art), OpenRadius - Margin, meter, null);
            }
            default:
                return new IslandPlan(new IslandBox(-Camera / 2, 0, Camera, Height), r, null, 0, null, null);
        }
    }

    /// <summary>
    /// The island's corners at a height (half of it, up to the expanded island's), and a picture's that keeps a margin
    /// of <paramref name="inset"/> concentric with them, as the island grows and shrinks between its faces.
    /// </summary>
    public static double RadiusAt(double height) => Math.Min(height / 2, OpenRadius);

    public static double PictureRadiusAt(double height, double inset) => Math.Max(0, RadiusAt(height) - inset);

    /// <summary>
    /// How far under its new height the island may go as its spring settles, shrinking. The spring's 9% past a fall from
    /// opened (132) to the band (22) took it 10 under, half the band: a sliver for a few frames (the island-shots film,
    /// 2026-11-08). Its width keeps the whole spring, and growing keeps it too (it swells into the free room under the bar).
    /// </summary>
    public const double MostUnder = 2;

    /// <summary>
    /// The spring's <paramref name="k"/> (past 1 when beyond its mark) for the height shrinking by <paramref name="fall"/>
    /// DIPs: no further past than <see cref="MostUnder"/>.
    /// </summary>
    public static double Under(double k, double fall) => fall <= 0 || k <= 1 ? k : Math.Min(k, 1 + MostUnder / fall);

    /// <summary>
    /// Going from one face to another, the picture and what stands at the trailing end move to their new places (the
    /// guidelines: "animating existing elements to their new positions rather than removing and animating them back
    /// in"): the same activity shown in both, each face holding it (a notice holds its own).
    /// </summary>
    public static bool Carries(IslandShape from, string? fromKey, IslandShape to, string? toKey) =>
        fromKey != null && fromKey == toKey && from is IslandShape.Compact or IslandShape.Open && to is IslandShape.Compact or IslandShape.Open;

    /// <summary>
    /// The key line round the island: "When the background is dark — for example, in Dark Mode — a key line appears around
    /// the Dynamic Island to distinguish it from other content" (its colour the activity's). Not round the idle island:
    /// nothing is shown in it.
    /// </summary>
    public static bool KeyLine(IslandShape shape, bool darkBehind) => darkBehind && shape != IslandShape.Idle;
}
