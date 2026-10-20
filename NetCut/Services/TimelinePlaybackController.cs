using NetCut.Models;

namespace NetCut.Services;

/// <summary>
/// One source of truth for mapping timeline time into playable source clips.
/// This is deliberately source-only; layered compositions require a composed preview.
/// </summary>
public sealed class TimelinePlaybackController
{
    private ProjectDocument _project;
    public TimelinePlaybackController(ProjectDocument project) => _project = project;
    public void SetProject(ProjectDocument project) => _project = project;

    public (TimelineClip Clip, MediaItem Media, double SourceSeconds)? Resolve(double seconds)
    {
        var clip = _project.Clips.Where(c => c.StartSeconds <= seconds &&
                seconds < c.StartSeconds + c.DurationSeconds)
            .OrderByDescending(c => c.TrackIndex).FirstOrDefault();
        if (clip is null) return null;
        var media = _project.Media.FirstOrDefault(m => m.Id == clip.MediaId);
        if (media is null || !File.Exists(media.Path)) return null;
        return (clip, media, Math.Clamp(clip.InSeconds + seconds - clip.StartSeconds, clip.InSeconds, clip.OutSeconds));
    }

    public double? NextPlayableTime(double afterSeconds)
    {
        foreach (var c in _project.Clips.Where(c => c.StartSeconds + c.DurationSeconds > afterSeconds)
            .OrderBy(c => c.StartSeconds).ThenByDescending(c => c.TrackIndex))
        {
            double time = Math.Max(afterSeconds, c.StartSeconds);
            if (Resolve(time) is not null) return time;
        }
        return null;
    }
}
