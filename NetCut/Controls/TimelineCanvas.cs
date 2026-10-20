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
    public double ViewStartSeconds { get; set; }
    public double ProjectDurationSeconds { get; set; }
    private double XToSeconds(double x) => ViewStartSeconds + x / PixelsPerSecond;
    public string? SelectedId { get; set; }
    public IReadOnlyList<SubtitleCue> Subtitles { get; set; } = Array.Empty<SubtitleCue>();
    public Dictionary<string, List<Microsoft.Maui.Graphics.IImage>> Filmstrips { get; } = new();

    public event Action<TimelineClip>? ClipSelected;
    public event Action<TimelineClip, double>? ClipMoved;

    private TimelineClip? _dragging;
    private double _dragOffset;
    private string _mode = "move";
    private bool _scrubbingPlayhead;
    public bool IsScrubbingPlayhead => _scrubbingPlayhead;
    public event Action<double>? PlayheadScrubStarted;
    public event Action<double>? PlayheadScrubEnded;
    public event Action<double>? ScrubEdgePanRequested;
    public double ViewportWidth { get; set; } = 900;
    private double ClampPointerTime(double x) => Math.Clamp(XToSeconds(x), 0, Math.Max(0, ProjectDurationSeconds));
    private void ScrubTo(double x)
    {
        var seconds = ClampPointerTime(x);
        PlayheadSeconds = seconds;
        PlayheadRequested?.Invoke(seconds);
        Invalidate();
    }
    public event Action<TimelineClip, bool, double>? ClipTrimmed;
    public event Action<double>? PlayheadRequested;
    public double PlayheadSeconds { get; set; }
    public Dictionary<string, float[]> Waveforms { get; } = new();
    public event Action<TimelineClip>? ClipDragStarted;
    public event Action<TimelineClip>? ClipDragEnded;

    public void Refresh() => Invalidate();

    public void BeginPointer(double x, double y)
    {
        // Drag the ruler handle or the vertical playhead line to scrub continuously.
        double handleX = (PlayheadSeconds - ViewStartSeconds) * PixelsPerSecond;
        if (y < 26 || (Math.Abs(x - handleX) <= 9 && y <= Height))
        {
            _scrubbingPlayhead = true;
            _dragging = null;
            PlayheadScrubStarted?.Invoke(PlayheadSeconds);
            ScrubTo(x);
            return;
        }
        if (y >= 86) return;
        _dragging = Clips.OrderBy(c => c.TrackIndex).LastOrDefault(c =>
            XToSeconds(x) >= c.StartSeconds &&
            XToSeconds(x) <= c.StartSeconds + c.DurationSeconds);
        if (_dragging is not null)
        {
            var left = (_dragging.StartSeconds - ViewStartSeconds) * PixelsPerSecond;
            var right = (_dragging.StartSeconds + _dragging.DurationSeconds - ViewStartSeconds) * PixelsPerSecond;
            _mode = x - left < 10 ? "left" : right - x < 10 ? "right" : "move";
            _dragOffset = XToSeconds(x) - _dragging.StartSeconds;
            ClipDragStarted?.Invoke(_dragging);
            SelectedId = _dragging.Id;
            ClipSelected?.Invoke(_dragging);
            Invalidate();
        }
    }

    public void MovePointer(double x)
    {
        if (_scrubbingPlayhead)
        {
            // Follow the pointer beyond the visible boundary, as DonkeyCut does.
            const double edge = 36;
            if (x > ViewportWidth - edge) ScrubEdgePanRequested?.Invoke(14);
            else if (x < edge) ScrubEdgePanRequested?.Invoke(-14);
            ScrubTo(Math.Clamp(x, 0, ViewportWidth));
            return;
        }
        if (_dragging is null) return;
        var time = Math.Max(0, XToSeconds(x) - _dragOffset);
        if (_mode == "move") ClipMoved?.Invoke(_dragging, time);
        else ClipTrimmed?.Invoke(_dragging, _mode == "left", Math.Max(0, XToSeconds(x)));
        Invalidate();
    }

    public void EndPointer()
    {
        if (_scrubbingPlayhead)
        {
            _scrubbingPlayhead = false;
            PlayheadScrubEnded?.Invoke(PlayheadSeconds);
            return;
        }
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
            double firstTick = Math.Floor(owner.ViewStartSeconds / (step / 4)) * (step / 4);
            for (double t = firstTick; t < owner.ViewStartSeconds + dirtyRect.Width / scale + step; t += step / 4)
            {
                float x = (float)((t - owner.ViewStartSeconds) * scale);
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
                if (clip.StartSeconds + clip.DurationSeconds < owner.ViewStartSeconds || clip.StartSeconds > owner.ViewStartSeconds + dirtyRect.Width / scale) continue;
                float x = (float)((clip.StartSeconds - owner.ViewStartSeconds) * scale);
                float width = (float)Math.Max(5, clip.DurationSeconds * scale);
                bool selected = clip.Id == owner.SelectedId;
                canvas.FillColor = Color.FromArgb(selected ? "#448FC7" : "#CDD6E0");
                canvas.FillRoundedRectangle(x, videoY + 2, width, 56, 2);
                if (owner.Filmstrips.TryGetValue(clip.MediaId, out var frames) && frames.Count > 0)
                {
                    for (float cellX = Math.Max(x + 2, x + 2 + (float)Math.Floor((0 - x - 2) / 36) * 36); cellX < Math.Min(x + width - 3, dirtyRect.Width); cellX += 36)
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
                    int first = Math.Max(0, (int)((-x) / 2) * 2);
                    int last = Math.Min((int)Math.Ceiling(width), (int)Math.Ceiling(dirtyRect.Width - x));
                    for (int i = first; i < last; i += 2)
                    {
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
                if (cue.End <= cue.Start || cue.End < owner.ViewStartSeconds || cue.Start > owner.ViewStartSeconds + dirtyRect.Width / scale) continue;
                float x = (float)((cue.Start - owner.ViewStartSeconds) * scale);
                float width = (float)Math.Max(3, (cue.End - cue.Start) * scale);
                canvas.FillColor = Color.FromArgb("#F1BF28");
                canvas.FillRoundedRectangle(x, captionsY + 6, width, 17, 2);
                canvas.FontColor = Color.FromArgb("#524113");
                canvas.FontSize = 9;
                canvas.DrawString(cue.Text, x + 2, captionsY + 5, Math.Max(1, width - 4), 19, HorizontalAlignment.Left, VerticalAlignment.Center);
            }
            float playhead = (float)((owner.PlayheadSeconds - owner.ViewStartSeconds) * scale);
            canvas.StrokeColor = Color.FromArgb("#1689FF");
            canvas.StrokeSize = 2;
            canvas.DrawLine(playhead, 5, playhead, dirtyRect.Height);
            canvas.FillColor = Color.FromArgb("#1689FF");
            canvas.FillRoundedRectangle(playhead - 5, 1, 10, 13, 4);
        }
    }
}
