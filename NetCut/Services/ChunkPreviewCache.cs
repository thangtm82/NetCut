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
            if (proxy.Clips.Count == 0)
                throw new NotSupportedException("Rendering an entirely empty timeline interval requires a silent filler.");
            var temp = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                await FfmpegService.ExportCompositionAsync(proxy, temp, ct);
                ct.ThrowIfCancellationRequested();
                File.Move(temp, target, true);
                Evict();
                return target;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { _renderLock.Release(); }
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
