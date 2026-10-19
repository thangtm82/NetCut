using System.Diagnostics;
using System.Globalization;
using NetCut.Models;

namespace NetCut.Services;

public static class FfmpegService
{
    // Specify NETCUT_FFMPEG and NETCUT_FFPROBE or place binaries on PATH.
    private static string Executable(string env, string fallback) =>
        Environment.GetEnvironmentVariable(env) is { Length: > 0 } value ? value : fallback;

    private static async Task<string> RunAsync(string executable, IEnumerable<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException($"Cannot start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} failed: {stderr}");
        return stdout;
    }

    public static async Task<double> ProbeDurationAsync(string input, CancellationToken ct = default)
    {
        var stdout = await RunAsync(Executable("NETCUT_FFPROBE", "ffprobe"),
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", input], ct);
        if (!double.TryParse(stdout.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || duration <= 0)
            throw new InvalidDataException("Could not read video duration.");
        return duration;
    }


    /// <summary>
    /// Render a cut of sequential clips on V1. Reject overlapping or multi-track layouts
    /// rather than silently dropping footage. Video sources are normalized to 1280x720/30fps.
    /// Audio uses optional stream mapping plus a silent track for sources without audio.
    /// </summary>
    public static async Task ExportSequenceAsync(ProjectDocument project, string output, CancellationToken ct = default)
    {
        var clips = project.Clips.OrderBy(c => c.StartSeconds).ToArray();
        if (clips.Length == 0) throw new InvalidOperationException("No clips to export.");
        if (clips.Any(c => c.TrackIndex != 0))
            throw new NotSupportedException("Multi-layer composition is not implemented. Export supports V1 only.");
        const double tolerance = 0.04;
        var cursor = 0d;
        foreach (var clip in clips)
        {
            if (Math.Abs(clip.StartSeconds - cursor) > tolerance)
                throw new NotSupportedException("Sequence export currently requires clips with no gaps or overlaps.");
            if (clip.DurationSeconds <= 0) throw new InvalidOperationException("Invalid clip duration.");
            cursor += clip.DurationSeconds;
        }

        var directory = Path.Combine(Path.GetTempPath(), "NetCut", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var normalized = new List<string>();
        var ci = CultureInfo.InvariantCulture;
        try
        {
            for (var i = 0; i < clips.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var clip = clips[i];
                var media = project.Media.FirstOrDefault(m => m.Id == clip.MediaId)
                    ?? throw new FileNotFoundException("Timeline media reference missing.");
                if (!File.Exists(media.Path)) throw new FileNotFoundException("Source media missing.", media.Path);
                var segment = Path.Combine(directory, $"segment-{i:D4}.mp4");
                // A silent input provides consistent stereo audio when source audio is absent.
                var hasAudio = await ProbeHasAudioAsync(media.Path, ct);
                var args = new List<string> { "-hide_banner", "-y", "-ss", clip.InSeconds.ToString(ci),
                    "-i", media.Path };
                if (!hasAudio)
                    args.AddRange(["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
                args.AddRange(["-t", clip.DurationSeconds.ToString(ci), "-map", "0:v:0",
                    "-map", hasAudio ? "0:a:0" : "1:a:0",
                    "-vf", "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "veryfast", "-crf", "20",
                    "-c:a", "aac", "-ar", "48000", "-ac", "2", "-shortest", segment]);
                await RunAsync(Executable("NETCUT_FFMPEG", "ffmpeg"), args, ct);
                normalized.Add(segment);
            }
            // Concat demuxer consumes filenames from a temporary list of known files.
            var listPath = Path.Combine(directory, "concat.txt");
            await File.WriteAllLinesAsync(listPath,
                normalized.Select(f => "file '" + f.Replace("'", "'\\''") + "'"), ct);
            await RunAsync(Executable("NETCUT_FFMPEG", "ffmpeg"),
                ["-hide_banner", "-y", "-f", "concat", "-safe", "0", "-i", listPath,
                 "-c", "copy", "-movflags", "+faststart", output], ct);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<bool> ProbeHasAudioAsync(string path, CancellationToken ct)
    {
        var output = await RunAsync(Executable("NETCUT_FFPROBE", "ffprobe"),
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=index",
             "-of", "csv=p=0", path], ct);
        return !string.IsNullOrWhiteSpace(output);
    }


    public static async Task ExportCompositionAsync(ProjectDocument project, string output,
        CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var types = new Dictionary<string, (bool Video, bool Audio)>();
        foreach (var media in project.Media.Where(m => project.Clips.Any(c => c.MediaId == m.Id)))
        {
            var video = await ProbeStreamAsync(media.Path, "v:0", ct);
            var audio = await ProbeStreamAsync(media.Path, "a:0", ct);
            types[media.Id] = (video, audio);
        }
        var items = CompositionPlan.Validate(project, types);
        var duration = items.Max(item => item.Clip.StartSeconds + item.Clip.DurationSeconds);
        var args = new List<string> { "-hide_banner", "-y" };
        var ci = CultureInfo.InvariantCulture;
        foreach (var item in items)
        {
            args.AddRange(["-ss", item.Clip.InSeconds.ToString(ci), "-t",
                item.Clip.DurationSeconds.ToString(ci), "-i", item.Media.Path]);
        }
        args.AddRange(["-filter_complex", CompositionPlan.BuildFilter(items, duration),
            "-map", "[vout]", "-map", "[aout]",
            "-c:v", "libx264", "-crf", "20", "-preset", "veryfast", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-ar", "48000", "-ac", "2", "-movflags", "+faststart", output]);
        await ExportProcess.RunAsync(Executable("NETCUT_FFMPEG", "ffmpeg"), args, duration, progress, ct);
    }

    private static async Task<bool> ProbeStreamAsync(string path, string selection, CancellationToken ct)
    {
        var output = await RunAsync(Executable("NETCUT_FFPROBE", "ffprobe"),
            ["-v", "error", "-select_streams", selection, "-show_entries", "stream=index",
             "-of", "csv=p=0", path], ct);
        return !string.IsNullOrWhiteSpace(output);
    }

    public static Task ExportClipAsync(MediaItem media, TimelineClip clip, string output, CancellationToken ct = default)
    {
        if (clip.OutSeconds <= clip.InSeconds) throw new ArgumentException("Clip out must be greater than in.");
        var ci = CultureInfo.InvariantCulture;
        return RunAsync(Executable("NETCUT_FFMPEG", "ffmpeg"),
            ["-hide_banner", "-y", "-ss", clip.InSeconds.ToString(ci), "-i", media.Path,
             "-t", clip.DurationSeconds.ToString(ci),
             "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
             "-c:a", "aac", "-movflags", "+faststart", output], ct);
    }
}
