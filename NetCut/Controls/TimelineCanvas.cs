using NetCut.Models;

namespace NetCut.Controls;

/// <summary>Native MAUI GraphicsView timeline. Horizontal positions are in seconds.</summary>
public sealed class TimelineCanvas : GraphicsView
{
    public TimelineCanvas()
    {
        Drawable = new TimelinePainter(this);
        HeightRequest = 298;
    }

    public IReadOnlyList<TimelineClip> Clips { get; set; } = Array.Empty<TimelineClip>();
    public double PixelsPerSecond { get; set; } = 4;
    public string? SelectedId { get; set; }

    public event Action<TimelineClip>? ClipSelected;
    public event Action<TimelineClip, double>? ClipMoved;

    private TimelineClip? _dragging;
    private double _dragOffset;
    private string _mode = "move";
    public event Action<TimelineClip, bool, double>? ClipTrimmed;
    public event Action<double>? PlayheadRequested;
    public double PlayheadSeconds { get; set; }
    public Dictionary<string, float[]> Waveforms { get; } = new();
    public event Action<TimelineClip>? ClipDragStarted;
    public event Action<TimelineClip>? ClipDragEnded;

    public void Refresh() => Invalidate();

    public void BeginPointer(double x, double y)
    {
        // 42px ruler; 82px per track.
        if (y < 42) { PlayheadRequested?.Invoke(Math.Max(0, x / PixelsPerSecond)); return; }
        var trackIndex = (int)((y - 42) / 82);
        _dragging = Clips.LastOrDefault(c => c.TrackIndex == trackIndex &&
            x >= c.StartSeconds * PixelsPerSecond &&
            x <= (c.StartSeconds + c.DurationSeconds) * PixelsPerSecond);
        if (_dragging is not null)
        {
            var left = _dragging.StartSeconds * PixelsPerSecond;
            var right = (_dragging.StartSeconds + _dragging.DurationSeconds) * PixelsPerSecond;
            _mode = x - left < 10 ? "left" : right - x < 10 ? "right" : "move";
            _dragOffset = x / PixelsPerSecond - _dragging.StartSeconds;
            ClipDragStarted?.Invoke(_dragging);
            SelectedId = _dragging.Id;
            ClipSelected?.Invoke(_dragging);
            Invalidate();
        }
    }

    public void MovePointer(double x)
    {
        if (_dragging is null) return;
        var time = Math.Max(0, x / PixelsPerSecond - _dragOffset);
        if (_mode == "move") ClipMoved?.Invoke(_dragging, time);
        else ClipTrimmed?.Invoke(_dragging, _mode == "left", Math.Max(0, x / PixelsPerSecond));
        Invalidate();
    }

    public void EndPointer()
    {
        var clip = _dragging;
        _dragging = null;
        if (clip is not null) ClipDragEnded?.Invoke(clip);
    }

    private sealed class TimelinePainter(TimelineCanvas owner) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.FillColor = Color.FromArgb("#16191F");
            canvas.FillRectangle(dirtyRect);
            float scale = (float)owner.PixelsPerSecond;
            const float rulerHeight = 42;
            const float trackHeight = 82;
            int tracks = Math.Max(3, owner.Clips.Count == 0 ? 0 : owner.Clips.Max(c => c.TrackIndex) + 1);
            canvas.FillColor = Color.FromArgb("#21252C");
            canvas.FillRectangle(0, 0, dirtyRect.Width, rulerHeight);
            double majorSeconds = scale < 2 ? 120 : scale < 5 ? 60 : scale < 12 ? 30 : 10;
            double minorSeconds = majorSeconds / 4;
            for (double second = 0; second <= dirtyRect.Width / scale + majorSeconds; second += minorSeconds)
            {
                float x = (float)(second * scale);
                bool major = Math.Abs(second / majorSeconds - Math.Round(second / majorSeconds)) < .001;
                canvas.StrokeColor = Color.FromArgb(major ? "#7A8493" : "#4A535E");
                canvas.StrokeSize = 1;
                canvas.DrawLine(x, major ? 21 : 31, x, rulerHeight);
                if (major)
                {
                    canvas.FontColor = Color.FromArgb("#D5DCE6");
                    canvas.FontSize = 11;
                    string label = TimeSpan.FromSeconds(second).ToString(second >= 3600 ? @"h\:mm\:ss" : @"mm\:ss");
                    canvas.DrawString(label, x + 4, 3, 88, 19, HorizontalAlignment.Left, VerticalAlignment.Center);
                }
            }
            for (int i = 0; i < tracks; i++)
            {
                float y = rulerHeight + i * trackHeight;
                canvas.FillColor = i % 2 == 0 ? Color.FromArgb("#242931") : Color.FromArgb("#20252C");
                canvas.FillRectangle(0, y, dirtyRect.Width, trackHeight - 2);
                canvas.StrokeColor = Color.FromArgb("#343B45");
                canvas.StrokeSize = 1;
                canvas.DrawLine(0, y + trackHeight - 2, dirtyRect.Width, y + trackHeight - 2);
            }
            foreach (var clip in owner.Clips)
            {
                float x = (float)(clip.StartSeconds * owner.PixelsPerSecond);
                float y = rulerHeight + clip.TrackIndex * trackHeight + 5;
                float width = (float)Math.Max(6, clip.DurationSeconds * owner.PixelsPerSecond);
                bool selected = clip.Id == owner.SelectedId;
                canvas.FillColor = Color.FromArgb(selected ? "#306FBE" : "#285A82");
                canvas.FillRoundedRectangle(x, y, width, 70, 5);
                canvas.FillColor = Color.FromArgb(selected ? "#74B6FF" : "#438BBB");
                canvas.FillRoundedRectangle(x, y, width, 21, 4);
                canvas.FontColor = Colors.White;
                canvas.FontSize = 11;
                canvas.DrawString(clip.Name, x + 8, y + 1, Math.Max(1, width - 16), 18, HorizontalAlignment.Left, VerticalAlignment.Center);
                // Video placeholder cells follow the visual rhythm of a thumbnail filmstrip.
                // Actual decoded frame thumbnails can be supplied by a later thumbnail cache.
                canvas.FillColor = Color.FromArgb("#213E5C");
                for (float cell = x + 4; cell < x + width - 6; cell += 48)
                {
                    float cellWidth = Math.Min(44, x + width - 5 - cell);
                    if (cellWidth <= 0) break;
                    canvas.FillRoundedRectangle(cell, y + 25, cellWidth, 39, 2);
                    canvas.FillColor = Color.FromArgb("#345E7F");
                    canvas.FillRectangle(cell + 5, y + 31, Math.Max(1, cellWidth - 10), 23);
                    canvas.FillColor = Color.FromArgb("#213E5C");
                }
                if (owner.Waveforms.TryGetValue(clip.MediaId, out var peaks) && peaks.Length > 0)
                {
                    canvas.StrokeColor = Color.FromArgb("#85E7B7");
                    canvas.StrokeSize = 1;
                    for (int i = 0; i < peaks.Length; i++)
                    {
                        float sampleX = x + 4 + Math.Max(1, width - 8) * i / peaks.Length;
                        float amplitude = Math.Clamp(peaks[i] * 15, 0, 15);
                        canvas.DrawLine(sampleX, y + 48 - amplitude, sampleX, y + 48 + amplitude);
                    }
                }
                canvas.FillColor = Colors.White;
                canvas.FillRoundedRectangle(x, y + 26, 3, 35, 1);
                canvas.FillRoundedRectangle(x + width - 3, y + 26, 3, 35, 1);
            }
            float px = (float)(owner.PlayheadSeconds * scale);
            canvas.StrokeColor = Color.FromArgb("#47A9FF");
            canvas.StrokeSize = 2;
            canvas.DrawLine(px, 9, px, dirtyRect.Height);
            canvas.FillColor = Color.FromArgb("#47A9FF");
            canvas.FillRoundedRectangle(px - 6, 1, 12, 15, 3);
        }
    }
}
