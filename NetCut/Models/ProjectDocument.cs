using System.Collections.ObjectModel;

namespace NetCut.Models;

public sealed class MediaItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public double DurationSeconds { get; set; }
    public override string ToString() => $"{Name} ({TimeSpan.FromSeconds(DurationSeconds):hh\\:mm\\:ss})";
}

public sealed class TimelineClip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string MediaId { get; set; } = "";
    public string Name { get; set; } = "";
    public double StartSeconds { get; set; }
    public double InSeconds { get; set; }
    public double OutSeconds { get; set; }
    public double DurationSeconds => Math.Max(0, OutSeconds - InSeconds);
    public override string ToString() => $"{Name} | {TimeSpan.FromSeconds(StartSeconds):hh\\:mm\\:ss} → {TimeSpan.FromSeconds(StartSeconds + DurationSeconds):hh\\:mm\\:ss}";
}

public sealed class SubtitleCue
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
}

public sealed class ProjectDocument
{
    public string Name { get; set; } = "Untitled Project";
    public ObservableCollection<MediaItem> Media { get; set; } = new();
    public ObservableCollection<TimelineClip> Clips { get; set; } = new();
    public ObservableCollection<SubtitleCue> Subtitles { get; set; } = new();
    public double DurationSeconds => Clips.Count == 0 ? 0 : Clips.Max(c => c.StartSeconds + c.DurationSeconds);
}
