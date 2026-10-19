using NetCut.Models;

namespace NetCut.Services;

/// <summary>Pure project editing operations. Timeline positions use seconds.</summary>
public static class TimelineEditor
{
    public const double MinClipSeconds = 0.01;

    public static (TimelineClip Left, TimelineClip Right) Split(ProjectDocument project, string clipId, double timelineSeconds)
    {
        var clip = project.Clips.FirstOrDefault(c => c.Id == clipId)
            ?? throw new ArgumentException("Selected clip not found.", nameof(clipId));
        var offset = timelineSeconds - clip.StartSeconds;
        if (offset < MinClipSeconds || offset > clip.DurationSeconds - MinClipSeconds)
            throw new ArgumentOutOfRangeException(nameof(timelineSeconds), "Split point must be inside the clip.");

        var splitInSource = clip.InSeconds + offset;
        var right = new TimelineClip
        {
            MediaId = clip.MediaId,
            TrackIndex = clip.TrackIndex,
            Name = clip.Name + " (right)",
            StartSeconds = timelineSeconds,
            InSeconds = splitInSource,
            OutSeconds = clip.OutSeconds
        };
        clip.OutSeconds = splitInSource;
        project.Clips.Add(right);
        return (clip, right);
    }

    public static void Trim(ProjectDocument project, string clipId, double inSeconds, double outSeconds)
    {
        var clip = project.Clips.FirstOrDefault(c => c.Id == clipId)
            ?? throw new ArgumentException("Selected clip not found.", nameof(clipId));
        var media = project.Media.FirstOrDefault(m => m.Id == clip.MediaId)
            ?? throw new InvalidDataException("Media is missing.");
        if (!double.IsFinite(inSeconds) || !double.IsFinite(outSeconds) ||
            inSeconds < 0 || outSeconds - inSeconds < MinClipSeconds ||
            outSeconds > media.DurationSeconds)
            throw new ArgumentOutOfRangeException(nameof(outSeconds), "Invalid source trim range.");
        clip.InSeconds = inSeconds;
        clip.OutSeconds = outSeconds;
    }

    public static void Move(TimelineClip clip, double startSeconds, int trackIndex)
    {
        if (!double.IsFinite(startSeconds) || startSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        if (trackIndex < 0 || trackIndex > 32)
            throw new ArgumentOutOfRangeException(nameof(trackIndex));
        clip.StartSeconds = startSeconds;
        clip.TrackIndex = trackIndex;
    }

    public static double Snap(double seconds, IEnumerable<TimelineClip> clips, string? excludeId = null,
        double thresholdSeconds = 0.12)
    {
        var nearest = seconds;
        var distance = thresholdSeconds;
        foreach (var clip in clips.Where(c => c.Id != excludeId))
        {
            foreach (var edge in new[] { clip.StartSeconds, clip.StartSeconds + clip.DurationSeconds })
            {
                var delta = Math.Abs(edge - seconds);
                if (delta < distance) { distance = delta; nearest = edge; }
            }
        }
        return Math.Max(0, nearest);
    }
}
