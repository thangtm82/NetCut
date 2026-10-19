using NetCut.Models;

namespace NetCut.Services;

/// <summary>
/// Renders a short composited proxy and plays it via the existing native WinUI MediaPlayerElement.
/// This is an offline preview segment, not zero-latency live compositing.
/// </summary>
public static class PreviewSegmentService
{
    public static async Task<string> RenderAsync(ProjectDocument project, double fromSeconds,
        double segmentSeconds = 5, CancellationToken ct = default)
    {
        if (fromSeconds < 0 || !double.IsFinite(fromSeconds))
            throw new ArgumentOutOfRangeException(nameof(fromSeconds));
        if (segmentSeconds <= 0 || segmentSeconds > 15)
            throw new ArgumentOutOfRangeException(nameof(segmentSeconds));
        if (project.Clips.Count == 0) throw new InvalidOperationException("Timeline is empty.");
        if (fromSeconds >= project.DurationSeconds) throw new ArgumentOutOfRangeException(nameof(fromSeconds));

        // Build a time-shifted project with only intersecting clips, maintaining source in/out.
        var duration = Math.Min(segmentSeconds, project.DurationSeconds - fromSeconds);
        var proxy = new ProjectDocument { Name = project.Name + " Preview" };
        foreach (var media in project.Media) proxy.Media.Add(media);
        foreach (var original in project.Clips)
        {
            var left = Math.Max(original.StartSeconds, fromSeconds);
            var right = Math.Min(original.StartSeconds + original.DurationSeconds, fromSeconds + duration);
            if (right <= left) continue;
            var trim = left - original.StartSeconds;
            proxy.Clips.Add(new TimelineClip
            {
                Name = original.Name,
                MediaId = original.MediaId,
                TrackIndex = original.TrackIndex,
                StartSeconds = left - fromSeconds,
                InSeconds = original.InSeconds + trim,
                OutSeconds = original.InSeconds + trim + (right - left)
            });
        }
        // In a gap, the existing compositor cannot render an empty project.
        if (proxy.Clips.Count == 0)
            throw new InvalidOperationException("No active clips in this preview interval.");
        var dir = Path.Combine(FileSystem.CacheDirectory, "netcut-preview");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            await FfmpegService.ExportCompositionAsync(proxy, path, ct);
            return path;
        }
        catch
        {
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }
    }
}
