using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetCut.Models;

namespace NetCut.Services;

/// <summary>Disk-backed 5-second preview chunks, with in-flight de-duplication and LRU eviction.</summary>
public sealed class ChunkPreviewCache
{
    public const double ChunkSeconds = 5;
    private readonly SemaphoreSlim _renderLock = new(1, 1);
    private readonly string _root;
    private const long MaxBytes = 2L * 1024 * 1024 * 1024;

    public ChunkPreviewCache(string? root = null)
    {
        _root = root ?? Path.Combine(FileSystem.CacheDirectory, "netcut-chunks");
        Directory.CreateDirectory(_root);
    }

    public static int Index(double seconds) => Math.Max(0, (int)Math.Floor(seconds / ChunkSeconds));
    public static double Start(int index) => index * ChunkSeconds;

    public async Task<string> GetAsync(ProjectDocument document, int index, CancellationToken ct)
    {
        if (index < 0 || Start(index) >= document.DurationSeconds)
            throw new ArgumentOutOfRangeException(nameof(index));
        var from = Start(index);
        var end = Math.Min(document.DurationSeconds, from + ChunkSeconds);
        var clips = document.Clips.Where(c => c.StartSeconds < end && c.StartSeconds + c.DurationSeconds > from)
            .OrderBy(c => c.Id).ToArray();
        var media = document.Media.Where(m => clips.Any(c => c.MediaId == m.Id))
            .OrderBy(m => m.Id).Select(m => new {
                m.Id, m.Path,
                Size = File.Exists(m.Path) ? new FileInfo(m.Path).Length : -1L,
                Modified = File.Exists(m.Path) ? File.GetLastWriteTimeUtc(m.Path).Ticks : 0L
            }).ToArray();
        var identity = JsonSerializer.Serialize(new {
            Version = 2, Index = index, End = end, Media = media,
            Clips = clips.Select(c => new { c.Id, c.MediaId, c.TrackIndex, c.StartSeconds, c.InSeconds, c.OutSeconds })
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var target = Path.Combine(_root, hash + ".mp4");

        await _renderLock.WaitAsync(ct);
        try
        {
            if (File.Exists(target) && new FileInfo(target).Length > 0)
            {
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
                return target;
            }
            var proxy = new ProjectDocument { Name = document.Name + " preview" };
            foreach (var m in document.Media) proxy.Media.Add(m);
            foreach (var c in clips)
            {
                var left = Math.Max(from, c.StartSeconds);
                var right = Math.Min(end, c.StartSeconds + c.DurationSeconds);
                var offset = left - c.StartSeconds;
                proxy.Clips.Add(new TimelineClip {
                    MediaId = c.MediaId, Name = c.Name, TrackIndex = c.TrackIndex,
                    StartSeconds = left - from, InSeconds = c.InSeconds + offset,
                    OutSeconds = c.InSeconds + offset + right - left
                });
            }
            // The existing compositor needs at least one clip.
            var empty = proxy.Clips.Count == 0;
            var temp = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                if (empty) await RenderBlankAsync(temp, end - from, ct);
                else await FfmpegService.ExportCompositionAsync(proxy, temp, ct,
                    minimumDurationSeconds: end - from);
                ct.ThrowIfCancellationRequested();
                File.Move(temp, target, true);
                Evict();
                return target;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { _renderLock.Release(); }
    }


    private static async Task RenderBlankAsync(string output, double duration, CancellationToken ct)
    {
        var binary = Environment.GetEnvironmentVariable("NETCUT_FFMPEG") is { Length: > 0 } ffmpeg
            ? ffmpeg : "ffmpeg";
        var info = new System.Diagnostics.ProcessStartInfo(binary)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var arg in new[] {
            "-y", "-f", "lavfi", "-i", "color=c=black:s=1280x720:r=30",
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
            "-t", duration.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-shortest", output })
            info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info)
            ?? throw new InvalidOperationException("Unable to launch FFmpeg.");
        using var registration = ct.Register(() => {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
        });
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync(ct);
        await stdout;
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException(await stderr);
    }

    private void Evict()
    {
        var files = new DirectoryInfo(_root).GetFiles("*.mp4").OrderBy(f => f.LastWriteTimeUtc).ToArray();
        var total = files.Sum(f => f.Length);
        foreach (var f in files)
        {
            if (total <= MaxBytes) break;
            try { var size = f.Length; f.Delete(); total -= size; }
            catch (IOException) { }
        }
    }
}
