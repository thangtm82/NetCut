using NetCut.Models;

namespace NetCut.Services;

/// <summary>Validates a timeline before invoking the currently supported V1 sequence renderer.</summary>
public static class RenderValidation
{
    public static IReadOnlyList<string> CheckSequence(ProjectDocument project)
    {
        var errors = new List<string>();
        var clips = project.Clips.OrderBy(c => c.StartSeconds).ToArray();
        if (clips.Length == 0) errors.Add("The timeline is empty.");
        if (clips.Any(c => c.TrackIndex != 0))
            errors.Add("This renderer supports V1 only; video layering and audio tracks need a compositing pipeline.");
        var cursor = 0d;
        foreach (var clip in clips)
        {
            var media = project.Media.FirstOrDefault(m => m.Id == clip.MediaId);
            if (media is null || !File.Exists(media.Path))
                errors.Add($"Source media not found: {clip.Name}");
            if (clip.DurationSeconds <= 0 || !double.IsFinite(clip.StartSeconds) ||
                !double.IsFinite(clip.InSeconds) || !double.IsFinite(clip.OutSeconds))
                errors.Add($"Invalid clip timing: {clip.Name}");
            if (Math.Abs(clip.StartSeconds - cursor) > 0.04)
                errors.Add($"Gap or overlap before clip: {clip.Name}");
            cursor += clip.DurationSeconds;
        }
        return errors;
    }
}
