using NetCut.Models;

namespace NetCut.Controls;

/// <summary>Native MAUI GraphicsView timeline. Horizontal positions are in seconds.</summary>
public sealed class TimelineCanvas : GraphicsView
{
    public TimelineCanvas()
    {
        Drawable = new TimelinePainter(this);
        HeightRequest = 154;
    }

    public IReadOnlyList<TimelineClip> Clips { get; set; } = Array.Empty<TimelineClip>();
    public double PixelsPerSecond { get; set; } = 4;
    public string? SelectedId { get; set; }
    public IReadOnlyList<SubtitleCue> Subtitles { get; set; } = Array.Empty<SubtitleCue>();
    public Dictionary<string, List<IImage>> Filmstrips { get; } = new();

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
        if (y < 26) { PlayheadRequested?.Invoke(Math.Max(0, x / PixelsPerSecond)); return; }
        if (y >= 86) return;
        _dragging = Clips.OrderBy(c => c.TrackIndex).LastOrDefault(c =>
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
            const float ruler = 26;
            const float videoY = 26;
            const float audioY = 86;
            const float captionsY = 114;
            float scale = (float)Math.Max(.1, owner.PixelsPerSecond);
            canvas.FillColor = Color.FromArgb("#F7F8FA");
            canvas.FillRectangle(dirtyRect);
            canvas.FillColor = Colors.White;
            canvas.FillRectangle(0, 0, dirtyRect.Width, ruler);
            double step = scale < 2 ? 120 : scale < 5 ? 120 : scale < 15 ? 30 : 10;
            for (double t = 0; t < dirtyRect.Width / scale + step; t += step / 4)
            {
                float x = (float)(t * scale);
                bool major = Math.Abs(t / step - Math.Round(t / step)) < .001;
                canvas.StrokeColor = Color.FromArgb(major ? "#C6CBD2" : "#E2E5E9");
                canvas.StrokeSize = 1;
                canvas.DrawLine(x, major ? 5 : 17, x, 26);
                if (major)
                {
                    canvas.FontColor = Color.FromArgb("#586270");
                    canvas.FontSize = 10;
                    canvas.DrawString(TimeSpan.FromSeconds(t).ToString(@"h\:mm\:ss"),
                        x + 3, 0, 86, 15, HorizontalAlignment.Left, VerticalAlignment.Center);
                }
            }
            canvas.FillColor = Color.FromArgb("#E9EDF2");
            canvas.FillRectangle(0, videoY, dirtyRect.Width, 60);
            canvas.FillColor = Color.FromArgb("#DAF2EB");
            canvas.FillRectangle(0, audioY, dirtyRect.Width, 28);
            canvas.FillColor = Color.FromArgb("#F0F1F4");
            canvas.FillRectangle(0, captionsY, dirtyRect.Width, 28);
            foreach (var clip in owner.Clips)
            {
                float x = (float)(clip.StartSeconds * scale);
                float width = (float)Math.Max(5, clip.DurationSeconds * scale);
                bool selected = clip.Id == owner.SelectedId;
                canvas.FillColor = Color.FromArgb(selected ? "#448FC7" : "#CDD6E0");
                canvas.FillRoundedRectangle(x, videoY + 2, width, 56, 2);
                if (owner.Filmstrips.TryGetValue(clip.MediaId, out var frames) && frames.Count > 0)
                {
                    for (float cellX = x + 2; cellX < x + width - 3; cellX += 36)
                    {
                        float cellW = Math.Min(34, x + width - 3 - cellX);
                        if (cellW < 1) break;
                        double elapsed = (cellX - x) / scale;
                        double sourcePosition = clip.InSeconds + elapsed;
                        int index = (int)Math.Clamp(sourcePosition / Math.Max(.001, clip.OutSeconds) * frames.Count, 0, frames.Count - 1);
                        canvas.DrawImage(frames[index], cellX, videoY + 5, cellW, 48);
                    }
                }
                else
                {
                    canvas.FillColor = Color.FromArgb("#869DB0");
                    for (float cellX = x + 2; cellX < x + width - 3; cellX += 36)
                    {
                        float cellW = Math.Min(34, x + width - 3 - cellX);
                        if (cellW < 1) break;
                        canvas.FillRoundedRectangle(cellX, videoY + 5, cellW, 48, 1);
                    }
                }
                canvas.FontColor = Colors.White;
                canvas.FontSize = 10;
                canvas.DrawString(clip.Name, x + 5, videoY + 3, Math.Max(1, width - 10), 15, HorizontalAlignment.Left, VerticalAlignment.Center);
                canvas.FillColor = Color.FromArgb("#2CB999");
                canvas.FillRoundedRectangle(x, audioY + 1, width, 26, 2);
                if (owner.Waveforms.TryGetValue(clip.MediaId, out var peaks) && peaks.Length > 0)
                {
                    canvas.StrokeColor = Colors.White;
                    canvas.StrokeSize = 1;
                    int count = Math.Max(1, (int)width);
                    for (int i = 0; i < count; i += 2)
                    {
                        double duration = Math.Max(.001, clip.OutSeconds - clip.InSeconds);
                        double mediaTime = clip.InSeconds + i / scale;
                        int index = (int)Math.Clamp(mediaTime / Math.Max(.001, clip.OutSeconds) * peaks.Length, 0, peaks.Length - 1);
                        float amplitude = Math.Clamp(peaks[index] * 11, 1, 11);
                        canvas.DrawLine(x + i, audioY + 14 - amplitude, x + i, audioY + 14 + amplitude);
                    }
                }
                canvas.FillColor = Colors.White;
                canvas.FillRectangle(x, videoY + 19, 2, 34);
                canvas.FillRectangle(x + width - 2, videoY + 19, 2, 34);
            }
            foreach (var cue in owner.Subtitles)
            {
                if (cue.End <= cue.Start) continue;
                float x = (float)(cue.Start * scale);
                float width = (float)Math.Max(3, (cue.End - cue.Start) * scale);
                canvas.FillColor = Color.FromArgb("#F1BF28");
                canvas.FillRoundedRectangle(x, captionsY + 6, width, 17, 2);
                canvas.FontColor = Color.FromArgb("#524113");
                canvas.FontSize = 9;
                canvas.DrawString(cue.Text, x + 2, captionsY + 5, Math.Max(1, width - 4), 19, HorizontalAlignment.Left, VerticalAlignment.Center);
            }
            float playhead = (float)(owner.PlayheadSeconds * scale);
            canvas.StrokeColor = Color.FromArgb("#1689FF");
            canvas.StrokeSize = 2;
            canvas.DrawLine(playhead, 5, playhead, dirtyRect.Height);
            canvas.FillColor = Color.FromArgb("#1689FF");
            canvas.FillRoundedRectangle(playhead - 5, 1, 10, 13, 4);
        }
    }
}
