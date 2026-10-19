using NetCut.Models;

namespace NetCut.Controls;

/// <summary>Native MAUI GraphicsView timeline. Horizontal positions are in seconds.</summary>
public sealed class TimelineCanvas : GraphicsView
{
    public TimelineCanvas()
    {
        Drawable = new TimelinePainter(this);
        HeightRequest = 230;
    }

    public IReadOnlyList<TimelineClip> Clips { get; set; } = Array.Empty<TimelineClip>();
    public double PixelsPerSecond { get; set; } = 35;
    public string? SelectedId { get; set; }

    public event Action<TimelineClip>? ClipSelected;
    public event Action<TimelineClip, double>? ClipMoved;

    private TimelineClip? _dragging;
    private double _dragOffset;

    public void Refresh() => Invalidate();

    public void BeginPointer(double x, double y)
    {
        // 36px ruler; 54px per track.
        if (y < 36) return;
        var trackIndex = (int)((y - 36) / 54);
        _dragging = Clips.LastOrDefault(c => c.TrackIndex == trackIndex &&
            x >= c.StartSeconds * PixelsPerSecond &&
            x <= (c.StartSeconds + c.DurationSeconds) * PixelsPerSecond);
        if (_dragging is not null)
        {
            _dragOffset = x / PixelsPerSecond - _dragging.StartSeconds;
            SelectedId = _dragging.Id;
            ClipSelected?.Invoke(_dragging);
            Invalidate();
        }
    }

    public void MovePointer(double x)
    {
        if (_dragging is null) return;
        var time = Math.Max(0, x / PixelsPerSecond - _dragOffset);
        ClipMoved?.Invoke(_dragging, time);
        Invalidate();
    }

    public void EndPointer() => _dragging = null;

    private sealed class TimelinePainter(TimelineCanvas owner) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.FillColor = Color.FromArgb("#20242A");
            canvas.FillRectangle(dirtyRect);
            float scale = (float)owner.PixelsPerSecond;
            int tracks = Math.Max(3, owner.Clips.Count == 0 ? 0 : owner.Clips.Max(c => c.TrackIndex) + 1);
            for (int s = 0; s < dirtyRect.Width / scale + 2; s += 5)
            {
                float x = s * scale;
                canvas.StrokeColor = Color.FromArgb("#505762");
                canvas.DrawLine(x, 0, x, dirtyRect.Height);
                canvas.FontColor = Colors.White;
                canvas.FontSize = 11;
                canvas.DrawString(TimeSpan.FromSeconds(s).ToString(@"mm\:ss"), x + 3, 5, 55, 20, HorizontalAlignment.Left, VerticalAlignment.Center);
            }
            for (int i = 0; i < tracks; i++)
            {
                float y = 36 + i * 54;
                canvas.FillColor = i % 2 == 0 ? Color.FromArgb("#292F38") : Color.FromArgb("#252A32");
                canvas.FillRectangle(0, y, dirtyRect.Width, 52);
                canvas.FontColor = Colors.White;
                canvas.FontSize = 10;
                canvas.DrawString($"V{i+1}", 3, y + 3, 30, 18, HorizontalAlignment.Left, VerticalAlignment.Center);
            }
            foreach (var clip in owner.Clips)
            {
                float x = (float)(clip.StartSeconds * owner.PixelsPerSecond);
                float y = 36 + clip.TrackIndex * 54 + 3;
                float width = (float)Math.Max(5, clip.DurationSeconds * owner.PixelsPerSecond);
                canvas.FillColor = clip.Id == owner.SelectedId ? Color.FromArgb("#18A999") : Color.FromArgb("#167867");
                canvas.FillRoundedRectangle(x, y, width, 46, 6);
                canvas.FontColor = Colors.White;
                canvas.FontSize = 12;
                canvas.DrawString(clip.Name, x + 6, y + 4, width - 12, 38, HorizontalAlignment.Left, VerticalAlignment.Center);
            }
        }
    }
}
