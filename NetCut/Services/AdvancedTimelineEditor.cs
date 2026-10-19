using NetCut.Models;

namespace NetCut.Services;

/// <summary>Undo-friendly non-destructive timeline operations in seconds.</summary>
public static class AdvancedTimelineEditor
{
    public static void TrimLeft(ProjectDocument project, TimelineClip clip, double newStart, bool ripple)
    {
        var oldEnd = clip.StartSeconds + clip.DurationSeconds;
        var start = Math.Clamp(newStart, Math.Max(0, clip.StartSeconds - clip.InSeconds), oldEnd - TimelineEditor.MinClipSeconds);
        var delta = start - clip.StartSeconds;
        clip.InSeconds += delta;
        clip.StartSeconds = start;
        if (ripple) ShiftFollowing(project, clip, oldEnd, -delta);
    }

    public static void TrimRight(ProjectDocument project, TimelineClip clip, double newEnd, bool ripple)
    {
        var originalEnd = clip.StartSeconds + clip.DurationSeconds;
        var media = project.Media.FirstOrDefault(m => m.Id == clip.MediaId)
            ?? throw new InvalidDataException("Media not found.");
        var end = Math.Clamp(newEnd, clip.StartSeconds + TimelineEditor.MinClipSeconds,
            clip.StartSeconds + Math.Max(0, media.DurationSeconds - clip.InSeconds));
        var delta = end - originalEnd;
        clip.OutSeconds += delta;
        if (ripple) ShiftFollowing(project, clip, originalEnd, delta);
    }

    public static void RippleDelete(ProjectDocument project, TimelineClip clip)
    {
        var end = clip.StartSeconds + clip.DurationSeconds;
        var amount = clip.DurationSeconds;
        project.Clips.Remove(clip);
        foreach (var other in project.Clips.Where(c => c.TrackIndex == clip.TrackIndex && c.StartSeconds >= end))
            other.StartSeconds = Math.Max(0, other.StartSeconds - amount);
    }

    public static void Move(ProjectDocument project, TimelineClip clip, double start, int track)
    {
        if (!double.IsFinite(start) || start < 0 || track < 0 || track > 32)
            throw new ArgumentOutOfRangeException(nameof(start));
        clip.StartSeconds = start;
        clip.TrackIndex = track;
    }

    private static void ShiftFollowing(ProjectDocument project, TimelineClip source, double boundary, double shift)
    {
        foreach (var other in project.Clips.Where(c => c.Id != source.Id &&
            c.TrackIndex == source.TrackIndex && c.StartSeconds >= boundary - 0.000001))
            other.StartSeconds = Math.Max(0, other.StartSeconds + shift);
    }
}
