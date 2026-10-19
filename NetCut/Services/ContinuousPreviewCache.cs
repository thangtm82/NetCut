using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetCut.Models;

namespace NetCut.Services;

/// <summary>
/// A cached, full-timeline composed proxy. Reuses the existing compositor so
/// preview can play continuously, with WinUI's native transport controls.
/// Rendering the first version is offline, not real-time editing playback.
/// </summary>
public static class ContinuousPreviewCache
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static string CacheRoot => Path.Combine(FileSystem.CacheDirectory, "netcut-continuous");

    public static async Task<string> GetOrRenderAsync(ProjectDocument project,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (project.Clips.Count == 0)
            throw new InvalidOperationException("Timeline is empty.");

        // Include relevant source metadata so changed files invalidate cached renders.
        var sources = project.Media.Where(m => project.Clips.Any(c => c.MediaId == m.Id))
            .OrderBy(m => m.Id)
            .Select(m => new
            {
                m.Id, m.Path,
                Exists = File.Exists(m.Path),
                Size = File.Exists(m.Path) ? new FileInfo(m.Path).Length : -1L,
                ModifiedUtc = File.Exists(m.Path) ? File.GetLastWriteTimeUtc(m.Path).Ticks : 0L
            }).ToArray();
        var payload = JsonSerializer.Serialize(new
        {
            Version = 1, Width = 1280, Height = 720, FrameRate = 30,
            Sources = sources,
            Clips = project.Clips.OrderBy(c => c.Id).Select(c => new
            {
                c.Id, c.MediaId, c.TrackIndex, c.StartSeconds,
                c.InSeconds, c.OutSeconds
            }).ToArray()
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        Directory.CreateDirectory(CacheRoot);
        var target = Path.Combine(CacheRoot, hash + ".mp4");

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(target) && new FileInfo(target).Length > 0)
            {
                progress?.Report(1);
                return target;
            }

            // A unique temporary output avoids exposing partial files to MediaPlayer.
            var temporary = Path.Combine(CacheRoot, Guid.NewGuid().ToString("N") + ".mp4");
            try
            {
                await FfmpegService.ExportCompositionAsync(project, temporary, cancellationToken, progress);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, target, overwrite: true);
                return target;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
            }
        }
        finally { Gate.Release(); }
    }

    public static void Clear()
    {
        if (!Directory.Exists(CacheRoot)) return;
        foreach (var file in Directory.EnumerateFiles(CacheRoot, "*.mp4"))
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
