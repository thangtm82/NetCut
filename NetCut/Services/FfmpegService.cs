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
