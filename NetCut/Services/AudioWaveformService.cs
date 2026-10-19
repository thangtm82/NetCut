using System.Diagnostics;
using System.Globalization;

namespace NetCut.Services;

/// <summary>Uses FFmpeg to sample audio into normalized amplitude buckets for timeline rendering.</summary>
public static class AudioWaveformService
{
    public static async Task<float[]> ExtractAsync(string path, int buckets = 180,
        CancellationToken cancellationToken = default)
    {
        var binary = Environment.GetEnvironmentVariable("NETCUT_FFMPEG") is { Length: > 0 } configured
            ? configured : "ffmpeg";
        var start = new ProcessStartInfo(binary)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new [] {
            "-v", "error", "-i", path, "-vn", "-ac", "1", "-ar", "8000",
            "-f", "s16le", "-acodec", "pcm_s16le", "pipe:1"
        }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("FFmpeg could not start.");
        using var cancellation = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
        });
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var samples = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(samples, cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var errors = await stderr;
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException(errors);
        var bytes = samples.ToArray();
        var peaks = new float[buckets];
        var count = bytes.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var bucket = Math.Min(buckets - 1, (int)((long)i * buckets / count));
            var value = Math.Abs((int)BitConverter.ToInt16(bytes, i * 2)) / 32768f;
            peaks[bucket] = Math.Max(peaks[bucket], value);
        }
        return peaks;
    }
}
