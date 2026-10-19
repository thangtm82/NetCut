using System.Diagnostics;
using System.Globalization;

namespace NetCut.Services;

public static class ExportProcess
{
    /// <summary>Runs ffmpeg with machine-readable -progress pipe:1 updates.</summary>
    public static async Task RunAsync(string executable, IEnumerable<string> arguments,
        double durationSeconds, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-progress");
        info.ArgumentList.Add("pipe:1");
        info.ArgumentList.Add("-nostats");
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException("Could not launch FFmpeg.");
        using var registration = cancellationToken.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                // FFmpeg progress reports microseconds in out_time_us.
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var micros) && durationSeconds > 0)
                    progress?.Report(Math.Clamp(micros / 1_000_000d / durationSeconds, 0, 1));
            }
            await process.WaitForExitAsync(cancellationToken);
            var stderr = await errors;
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("FFmpeg failed: " + stderr[^Math.Min(stderr.Length, 3000)..]);
            progress?.Report(1);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
    }
}
