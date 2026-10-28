using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SaymonTools.App.UI;

/// <summary>
/// The trim bar: frames of the video along the track, the kept part between two handles, the rest dimmed,
/// and a playhead. Drag a handle to move the start or end, click or drag elsewhere to move the playhead.
/// </summary>
internal sealed class TrimTimeline : FrameworkElement
{
    private const double HandleWidth = 14;
    private const double Corner = 10;

    /// <summary>Shortest part that can be kept, in seconds.</summary>
    public const double MinLength = 0.1;

    private readonly List<BitmapSource?> _frames = [];
    private Drag _drag;

    private enum Drag
    {
        None,
        Start,
        End,
        Playhead,
    }

    public TrimTimeline()
    {
        Height = 72;
        Cursor = Cursors.Hand;
        Focusable = false;
        SnapsToDevicePixels = true;
    }

    public double Duration { get; private set; }

    public double Start { get; private set; }

    public double End { get; private set; }

    public double Position { get; private set; }

    /// <summary>The start handle moved (true) or the end handle (false); the dialog shows that frame.</summary>
    public event Action<bool>? RangeChanged;

    /// <summary>The user moved the playhead.</summary>
    public event Action? Seeked;

    public void SetDuration(double seconds)
    {
        Duration = Math.Max(seconds, MinLength);
        End = End <= 0 || End > Duration ? Duration : End;
        Start = Math.Min(Start, End - MinLength);
        InvalidateVisual();
    }

    public void SetStart(double seconds)
    {
        Start = Math.Clamp(seconds, 0, Math.Max(0, End - MinLength));
        InvalidateVisual();
    }

    public void SetEnd(double seconds)
    {
        End = Math.Clamp(seconds, Start + MinLength, Duration);
        InvalidateVisual();
    }

    public void SetPosition(double seconds)
    {
        Position = Math.Clamp(seconds, 0, Duration);
        InvalidateVisual();
    }

    /// <summary>Frames to show along the track, filled in as they are read.</summary>
    public void SetFrameCount(int count)
    {
        _frames.Clear();
        _frames.AddRange(Enumerable.Repeat<BitmapSource?>(null, count));
        InvalidateVisual();
    }

    public void SetFrame(int index, BitmapSource frame)
    {
        if (index >= 0 && index < _frames.Count)
        {
            _frames[index] = frame;
            InvalidateVisual();
        }
    }

    // ---------------------------------------------------------------- geometry

    private Rect Track => new(HandleWidth, 6, Math.Max(1, ActualWidth - 2 * HandleWidth), Math.Max(1, ActualHeight - 12));

    private double X(double seconds) => Track.Left + (Duration <= 0 ? 0 : seconds / Duration * Track.Width);

    private double Seconds(double x) => Duration <= 0 ? 0 : Math.Clamp((x - Track.Left) / Track.Width, 0, 1) * Duration;

    protected override void OnRender(DrawingContext dc)
    {
        var track = Track;
        var clip = new RectangleGeometry(track, Corner, Corner);
        dc.DrawRoundedRectangle(Theme.Track, null, track, Corner, Corner);

        // Frames side by side, each scaled to fill its slot.
        dc.PushClip(clip);
        if (_frames.Count > 0)
        {
            var slot = track.Width / _frames.Count;
            for (var i = 0; i < _frames.Count; i++)
            {
                if (_frames[i] is not { } frame)
                    continue;
                var scale = Math.Max(slot / frame.Width, track.Height / frame.Height);
                var w = frame.Width * scale;
                var h = frame.Height * scale;
                var cell = new Rect(track.Left + i * slot, track.Top, slot + 0.5, track.Height);
                dc.PushClip(new RectangleGeometry(cell));
                dc.DrawImage(frame, new Rect(cell.Left + (slot - w) / 2, track.Top + (track.Height - h) / 2, w, h));
                dc.Pop();
            }
        }
        if (Duration > 0)
        {
            // What gets cut away is dimmed.
            var dim = new SolidColorBrush(Color.FromArgb(0xB0, 0x08, 0x06, 0x10));
            dim.Freeze();
            dc.DrawRectangle(dim, null, new Rect(track.Left, track.Top, Math.Max(0, X(Start) - track.Left), track.Height));
            dc.DrawRectangle(dim, null, new Rect(X(End), track.Top, Math.Max(0, track.Right - X(End)), track.Height));
        }
        dc.Pop();

        if (Duration <= 0)
            return;

        var startX = X(Start);
        var endX = X(End);
        var accent = Theme.AccentGradient;
        // The kept part: framed by the accent, with a handle on each side.
        dc.DrawRectangle(null, new Pen(accent, 3), new Rect(startX, track.Top + 1.5, Math.Max(0, endX - startX), track.Height - 3));
        DrawHandle(dc, new Rect(startX - HandleWidth, track.Top - 4, HandleWidth, track.Height + 8), left: true);
        DrawHandle(dc, new Rect(endX, track.Top - 4, HandleWidth, track.Height + 8), left: false);

        // Playhead.
        var px = X(Position);
        var white = new Pen(Brushes.White, 2);
        white.Freeze();
        dc.DrawLine(white, new Point(px, track.Top - 6), new Point(px, track.Bottom + 6));
        dc.DrawEllipse(Brushes.White, null, new Point(px, track.Top - 6), 4.5, 4.5);
    }

    private static void DrawHandle(DrawingContext dc, Rect r, bool left)
    {
        var radius = new CornerRadius(left ? 8 : 0, left ? 0 : 8, left ? 0 : 8, left ? 8 : 0);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(r.Left + radius.TopLeft, r.Top), true, true);
            g.LineTo(new Point(r.Right - radius.TopRight, r.Top), false, false);
            g.ArcTo(new Point(r.Right, r.Top + radius.TopRight), new Size(radius.TopRight, radius.TopRight), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(r.Right, r.Bottom - radius.BottomRight), false, false);
            g.ArcTo(new Point(r.Right - radius.BottomRight, r.Bottom), new Size(radius.BottomRight, radius.BottomRight), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(r.Left + radius.BottomLeft, r.Bottom), false, false);
            g.ArcTo(new Point(r.Left, r.Bottom - radius.BottomLeft), new Size(radius.BottomLeft, radius.BottomLeft), 0, false, SweepDirection.Clockwise, false, false);
            g.LineTo(new Point(r.Left, r.Top + radius.TopLeft), false, false);
            g.ArcTo(new Point(r.Left + radius.TopLeft, r.Top), new Size(radius.TopLeft, radius.TopLeft), 0, false, SweepDirection.Clockwise, false, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(Theme.AccentGradient, null, geometry);
        // Grip lines.
        var grip = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), 1.5);
        grip.Freeze();
        var cx = r.Left + r.Width / 2;
        var cy = r.Top + r.Height / 2;
        dc.DrawLine(grip, new Point(cx - 2, cy - 8), new Point(cx - 2, cy + 8));
        dc.DrawLine(grip, new Point(cx + 2, cy - 8), new Point(cx + 2, cy + 8));
    }

    // ---------------------------------------------------------------- mouse

    private Drag HitTest(Point p)
    {
        if (Duration <= 0)
            return Drag.None;
        var startX = X(Start);
        var endX = X(End);
        // The inner edge of each handle wins when both are close (a very short part).
        if (p.X >= startX - HandleWidth - 2 && p.X <= startX + 4)
            return Drag.Start;
        if (p.X >= endX - 4 && p.X <= endX + HandleWidth + 2)
            return Drag.End;
        return Drag.Playhead;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _drag = HitTest(e.GetPosition(this));
        if (_drag == Drag.None)
            return;
        CaptureMouse();
        Apply(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_drag != Drag.None && IsMouseCaptured)
            Apply(p);
        else
            Cursor = HitTest(p) is Drag.Start or Drag.End ? Cursors.SizeWE : Cursors.Hand;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _drag = Drag.None;
        ReleaseMouseCapture();
    }

    private void Apply(Point p)
    {
        var t = Seconds(p.X);
        switch (_drag)
        {
            case Drag.Start:
                SetStart(t);
                RangeChanged?.Invoke(true);
                break;
            case Drag.End:
                SetEnd(t);
                RangeChanged?.Invoke(false);
                break;
            case Drag.Playhead:
                SetPosition(t);
                Seeked?.Invoke();
                break;
        }
    }
}
