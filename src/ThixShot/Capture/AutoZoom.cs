using System.Text.Json.Serialization;

namespace ThixShot.Capture;

[JsonConverter(typeof(JsonStringEnumConverter<InputKind>))]
public enum InputKind { Click, Typing }

/// <summary>
/// What happened during a recording, recorded beside the video: a click or a keystroke (never which key), at a position
/// inside the recorded area (0..1 from the upper left). For typing the position is the text cursor when the system names it,
/// otherwise the place of the last click, otherwise the mouse pointer.
/// </summary>
public sealed record InputEvent(double Seconds, InputKind Kind, double X, double Y)
{
    internal bool IsValid => double.IsFinite(Seconds) && Seconds >= 0 && X is >= 0 and <= 1 && Y is >= 0 and <= 1;
}

/// <summary>A run of keystrokes shown as one bar on the timeline.</summary>
public readonly record struct TypingSpan(double StartSeconds, double EndSeconds);

/// <summary>
/// Turns the clicks and the typing of a recording into zooms, the way a screen recorder for demos does: activity close
/// together is one zoom that stays while the person works (typing keeps it), the picture pans to a new place instead of
/// zooming out and in again, and it zooms out only after a pause.
/// </summary>
public static class AutoZoom
{
    /// <summary>The magnification of an automatic zoom.</summary>
    public const double Scale = 1.8;

    /// <summary>A pause longer than this ends a zoom.</summary>
    public const double Pause = 2.5;

    /// <summary>The zoom starts this long before the first click.</summary>
    public const double Lead = 0.6;

    /// <summary>The zoom stays this long after the last activity.</summary>
    public const double Hold = 1.6;

    /// <summary>Keystrokes closer than this belong to one bar on the timeline.</summary>
    public const double TypingGap = 0.8;

    public static ZoomPoint[] Build(IReadOnlyList<InputEvent> activity, double durationSeconds, double scale = Scale)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) return [];
        var events = activity.Where(e => e.IsValid && e.Seconds < durationSeconds).OrderBy(e => e.Seconds).ToArray();
        var zooms = new List<ZoomPoint>();
        int first = 0;
        while (first < events.Length)
        {
            int last = first;
            while (last + 1 < events.Length && events[last + 1].Seconds - events[last].Seconds <= Pause) last++;
            AddRun(events.AsSpan(first, last - first + 1), durationSeconds, scale, zooms);
            first = last + 1;
        }

        return [.. zooms];
    }

    /// <summary>The keystrokes as bars: keystrokes closer than <see cref="TypingGap"/> are one bar.</summary>
    public static TypingSpan[] Typing(IReadOnlyList<InputEvent> activity)
    {
        var spans = new List<TypingSpan>();
        foreach (var key in activity.Where(e => e.Kind == InputKind.Typing && e.IsValid).OrderBy(e => e.Seconds))
        {
            if (spans.Count > 0 && key.Seconds - spans[^1].EndSeconds <= TypingGap)
                spans[^1] = spans[^1] with { EndSeconds = key.Seconds + 0.15 };
            else
                spans.Add(new TypingSpan(key.Seconds, key.Seconds + 0.15));
        }

        return [.. spans];
    }

    // One run of activity without a long pause: one focus while the activity stays inside the middle of the zoomed picture,
    // a new focus (reached by panning) when it leaves it.
    private static void AddRun(ReadOnlySpan<InputEvent> run, double duration, double scale, List<ZoomPoint> zooms)
    {
        double reach = 0.32 / scale;
        var groups = new List<(double Start, double X, double Y)>();
        foreach (var activity in run)
        {
            if (groups.Count > 0 && Math.Abs(activity.X - groups[^1].X) <= reach && Math.Abs(activity.Y - groups[^1].Y) <= reach) continue;
            groups.Add((activity.Seconds, activity.X, activity.Y));
        }

        double runStart = Math.Max(0, run[0].Seconds - Lead);
        double runEnd = Math.Min(duration, Math.Max(run[^1].Seconds + Hold, runStart + 2 * VideoEditService.Edge));
        double half = VideoEditService.Edge / 2;
        for (int i = 0; i < groups.Count; i++)
        {
            // Neighbours overlap by one edge: one fades out while the next fades in, which is a pan at full zoom.
            double start = i == 0 ? runStart : Math.Max(runStart, groups[i].Start - 0.25 - half);
            double end = i == groups.Count - 1 ? runEnd : Math.Min(runEnd, groups[i + 1].Start - 0.25 + half);
            if (end - start < 0.05) continue;
            zooms.Add(new ZoomPoint(start, end - start, groups[i].X, groups[i].Y, scale));
        }
    }
}

/// <summary>
/// The ring shown where a click happened: it grows and fades for <see cref="Duration"/> seconds. The same numbers are drawn
/// by the preview, the Windows export and the macOS export (native/VideoEditing.swift).
/// </summary>
public static class ClickEffect
{
    public const double Duration = 0.6;

    /// <summary>The largest radius as a share of the shorter side of the recorded picture.</summary>
    public const double Radius = 0.03;

    /// <summary>The ring in ThixShot's accent #2F6FED: a soft fill, the ring and a thin white halo outside it (seen on dark and light screens).</summary>
    public const byte Red = 0x2F, Green = 0x6F, Blue = 0xED;

    public const double FillAlpha = 70, RingAlpha = 235, HaloAlpha = 210;

    /// <summary>Stroke widths as a share of the radius.</summary>
    public const double RingWidth = 0.16, HaloWidth = 0.07;

    /// <summary>The rings visible at a moment: position in the recorded picture (0..1), radius as a share of its shorter side, opacity.</summary>
    public static IEnumerable<(double X, double Y, double Radius, double Opacity)> At(double seconds, IReadOnlyList<InputEvent> activity)
    {
        foreach (var click in activity)
        {
            if (click.Kind != InputKind.Click || !click.IsValid) continue;
            double age = seconds - click.Seconds;
            if (age < 0 || age >= Duration) continue;
            double progress = age / Duration;
            double grow = 1 - Math.Pow(1 - progress, 3);
            yield return (click.X, click.Y, Radius * (0.35 + 0.65 * grow), Math.Pow(1 - progress, 1.5));
        }
    }
}
