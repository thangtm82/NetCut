using System.Globalization;
using NetCut.Models;

namespace NetCut.Services;

/// <summary>
/// Multi-layer native FFmpeg composer. Each timeline clip is decoded as an input,
/// aligned to absolute timeline seconds and overlaid in ascending TrackIndex order.
/// Higher index is drawn above lower. Source audio streams are mixed.
/// </summary>
public static class CompositionPlan
{
    public sealed record Input(TimelineClip Clip, MediaItem Media, bool HasVideo, bool HasAudio);

    public static IReadOnlyList<Input> Validate(ProjectDocument project, IReadOnlyDictionary<string, (bool Video, bool Audio)> streams)
    {
        if (project.Clips.Count == 0) throw new InvalidOperationException("Timeline has no clips.");
        var result = new List<Input>();
        foreach (var clip in project.Clips.OrderBy(c => c.TrackIndex).ThenBy(c => c.StartSeconds))
        {
            var media = project.Media.FirstOrDefault(m => m.Id == clip.MediaId)
                ?? throw new InvalidDataException($"Missing media reference: {clip.Name}");
            if (!File.Exists(media.Path)) throw new FileNotFoundException("Missing media file.", media.Path);
            if (!streams.TryGetValue(media.Id, out var kind) || (!kind.Video && !kind.Audio))
                throw new InvalidDataException($"No playable streams: {clip.Name}");
            if (!double.IsFinite(clip.StartSeconds) || clip.StartSeconds < 0 ||
                !double.IsFinite(clip.InSeconds) || clip.InSeconds < 0 ||
                !double.IsFinite(clip.OutSeconds) || clip.OutSeconds <= clip.InSeconds ||
                clip.OutSeconds > media.DurationSeconds + 0.05 || clip.TrackIndex < 0)
                throw new InvalidDataException($"Invalid clip timing or track: {clip.Name}");
            result.Add(new Input(clip, media, kind.Video, kind.Audio));
        }
        return result;
    }

    public static string Seconds(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    public static string BuildFilter(IReadOnlyList<Input> inputs, double duration, int width = 1280, int height = 720)
    {
        if (inputs.Count == 0 || duration <= 0 || width <= 0 || height <= 0)
            throw new ArgumentException("Invalid composition parameters.");
        var d = Seconds(duration);
        var graph = new List<string>
        {
            $"color=c=black:s={width}x{height}:r=30:d={d},format=yuv420p[bg0]"
        };
        var previous = "bg0";
        int videoNumber = 0;
        var audioLabels = new List<string>();
        for (var i = 0; i < inputs.Count; i++)
        {
            var item = inputs[i];
            var clip = item.Clip;
            var start = Seconds(clip.StartSeconds);
            var end = Seconds(clip.StartSeconds + clip.DurationSeconds);
            if (item.HasVideo)
            {
                var label = $"v{videoNumber++}";
                graph.Add($"[{i}:v:0]setpts=PTS-STARTPTS,scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,setpts=PTS+{start}/TB[{label}]");
                var next = $"ov{videoNumber}";
                graph.Add($"[{previous}][{label}]overlay=x=0:y=0:eof_action=pass:repeatlast=0:enable='between(t,{start},{end})'[{next}]");
                previous = next;
            }
            if (item.HasAudio)
            {
                var label = $"a{i}";
                var delay = Math.Round(clip.StartSeconds * 1000);
                graph.Add($"[{i}:a:0]asetpts=PTS-STARTPTS,aresample=48000,aformat=sample_rates=48000:channel_layouts=stereo,adelay={delay}|{delay}[{label}]");
                audioLabels.Add($"[{label}]");
            }
        }
        graph.Add($"[{previous}]trim=duration={d},setpts=PTS-STARTPTS[vout]");
        graph.Add($"anullsrc=r=48000:cl=stereo,atrim=duration={d},asetpts=PTS-STARTPTS[asilence]");
        audioLabels.Insert(0, "[asilence]");
        graph.Add($"{string.Concat(audioLabels)}amix=inputs={audioLabels.Count}:duration=longest:normalize=0,atrim=duration={d},asetpts=PTS-STARTPTS[aout]");
        return string.Join(";", graph);
    }
}
