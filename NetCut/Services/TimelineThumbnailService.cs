using System.Diagnostics;
using Microsoft.Maui.Graphics;
using Microsoft.Maui.Graphics.Platform;

namespace NetCut.Services;

/// <summary>Extracts evenly spaced real video frames via FFmpeg for timeline filmstrips.</summary>
public static class TimelineThumbnailService
{
    public static async Task<List<IImage>> ExtractAsync(string path, double duration, int count = 12)
    {
        var result = new List<IImage>();
        if (!File.Exists(path) || duration <= 0) return result;
        string ffmpeg = Environment.GetEnvironmentVariable("NETCUT_FFMPEG") is { Length: > 0 } custom ? custom : "ffmpeg";
        string folder = Path.Combine(FileSystem.CacheDirectory, "netcut-filmstrip", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            for (int i = 0; i < count; i++)
            {
                double position = Math.Min(Math.Max(0, duration - .05), duration * (i + .5) / count);
                string output = Path.Combine(folder, i.ToString("D3") + ".png");
                var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-ss", position.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", path, "-frames:v", "1", "-vf", "scale=96:72:force_original_aspect_ratio=increase,crop=96:72", "-y", output })
                    psi.ArgumentList.Add(arg);
                using var process = Process.Start(psi) ?? throw new InvalidOperationException("Cannot launch FFmpeg.");
                string stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                if (process.ExitCode != 0 || !File.Exists(output)) continue;
                using var stream = File.OpenRead(output);
                var image = PlatformImage.FromStream(stream);
                if (image is not null) result.Add(image);
            }
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
        return result;
    }
}
