using System.Diagnostics;
using System.Globalization;
using NetCut.Models;

namespace NetCut.Services;

/// <summary>Renders a single still frame at a timeline instant using native FFmpeg layering.</summary>
public static class TimelineFramePreview
{
    private static string Ffmpeg => Environment.GetEnvironmentVariable("NETCUT_FFMPEG") is { Length: > 0 } binary
        ? binary : "ffmpeg";

    public static async Task<string> RenderAsync(ProjectDocument project, double seconds, string outputFile,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        var clips = project.Clips
            .Where(c => c.StartSeconds <= seconds && seconds < c.StartSeconds + c.DurationSeconds)
            .OrderBy(c => c.TrackIndex).ThenBy(c => c.StartSeconds).ToArray();

        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        var graph = new List<string> { "color=c=black:s=1280x720:r=30:d=1,format=rgba[bg]" };
        var previous = "bg";
        var index = 0;
        foreach (var clip in clips)
        {
            var media = project.Media.FirstOrDefault(m => m.Id == clip.MediaId);
            if (media is null || !File.Exists(media.Path)) continue;
            // Audio-only files have no frames. Probe to exclude those inputs.
            if (!await HasVideoAsync(media.Path, cancellationToken)) continue;
            var offset = clip.InSeconds + seconds - clip.StartSeconds;
            args.AddRange(["-ss", offset.ToString("0.######", CultureInfo.InvariantCulture), "-i", media.Path]);
            var layer = $"layer{index}";
            graph.Add($"[{index}:v:0]scale=1280:720:force_original_aspect_ratio=decrease," +
                      $"pad=1280:720:(ow-iw)/2:(oh-ih)/2,setsar=1,format=rgba[{layer}]");
            var next = $"mix{index}";
            graph.Add($"[{previous}][{layer}]overlay=shortest=1:eof_action=pass[{next}]");
            previous = next;
            index++;
        }
        graph.Add($"[{previous}]format=rgb24[frame]");
        args.AddRange(["-filter_complex", string.Join(";", graph),
            "-map", "[frame]", "-frames:v", "1", "-update", "1", outputFile]);

        Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);
        var start = new ProcessStartInfo(Ffmpeg)
        {
            UseShellExecute = false, RedirectStandardError = true,
            RedirectStandardOutput = true, CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to run FFmpeg.");
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        var stderr = await stderrTask;
        await stdoutTask;
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException("Frame preview failed: " + stderr);
        return outputFile;
    }

    private static async Task<bool> HasVideoAsync(string path, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("NETCUT_FFPROBE") is { Length: > 0 } bin ? bin : "ffprobe")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=index",
                                    "-of", "csv=p=0", path }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to run FFprobe.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        await stderr;
        return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(await stdout);
    }
}
