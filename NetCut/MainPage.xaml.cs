using System.Globalization;
using NetCut.Models;
using NetCut.Services;

namespace NetCut;

public partial class MainPage : ContentPage
{
    private ProjectDocument _project = new();
    private string? _projectPath;

    public MainPage()
    {
        InitializeComponent();
        TimelineCanvas.StartInteraction += (_, e) => {
            if (e.Touches.Length > 0) TimelineCanvas.BeginPointer(e.Touches[0].X, e.Touches[0].Y);
        };
        TimelineCanvas.DragInteraction += (_, e) => {
            if (e.Touches.Length > 0) TimelineCanvas.MovePointer(e.Touches[0].X);
        };
        TimelineCanvas.EndInteraction += (_, _) => TimelineCanvas.EndPointer();
        TimelineCanvas.ClipSelected += clip => {
            TimelineList.SelectedItem = clip;
            PreviewClip(clip);
        };
        TimelineCanvas.ClipMoved += (clip, seconds) => {
            clip.StartSeconds = seconds;
            StatusLabel.Text = $"Moved {clip.Name} to {seconds:F2}s";
        };
        BindProject();
    }

    private void BindProject()
    {
        MediaList.ItemsSource = _project.Media;
        TimelineList.ItemsSource = _project.Clips;
        ProjectNameLabel.Text = _project.Name;
        RefreshTimeline();
    }

    private async void OnNewClicked(object sender, EventArgs e)
    {
        var name = await DisplayPromptAsync("New project", "Project name", initialValue: "Untitled Project");
        if (name is null) return;
        _project = new ProjectDocument { Name = string.IsNullOrWhiteSpace(name) ? "Untitled Project" : name.Trim() };
        _projectPath = null;
        BindProject();
        StatusLabel.Text = "Created a new project.";
    }

    private async void OnOpenClicked(object sender, EventArgs e)
    {
        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Open NetCut JSON project" });
            if (file is null) return;
            _project = await ProjectService.LoadAsync(file.FullPath);
            _projectPath = file.FullPath;
            BindProject();
            StatusLabel.Text = $"Opened {_project.Name}.";
        }
        catch (Exception ex) { await ShowError(ex); }
    }

    private async void OnSaveClicked(object sender, EventArgs e)
    {
        try
        {
            _projectPath ??= Path.Combine(FileSystem.AppDataDirectory, "projects", SanitizeFileName(_project.Name) + ".json");
            await ProjectService.SaveAsync(_project, _projectPath);
            StatusLabel.Text = $"Saved: {_projectPath}";
        }
        catch (Exception ex) { await ShowError(ex); }
    }

    private async void OnImportClicked(object sender, EventArgs e)
    {
        try
        {
            var files = await FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = "Import media" });
            if (files is null) return;
            foreach (var file in files)
            {
                var duration = await FfmpegService.ProbeDurationAsync(file.FullPath);
                _project.Media.Add(new MediaItem
                {
                    Name = file.FileName,
                    Path = file.FullPath,
                    DurationSeconds = duration
                });
            }
            StatusLabel.Text = $"Imported {_project.Media.Count} media item(s) in project.";
        }
        catch (Exception ex) { await ShowError(ex); }
    }

    private async void OnAddClipClicked(object sender, EventArgs e)
    {
        if (MediaList.SelectedItem is not MediaItem media)
        {
            await DisplayAlert("Media", "Select one imported media item first.", "OK");
            return;
        }
        _project.Clips.Add(new TimelineClip
        {
            MediaId = media.Id,
            Name = media.Name,
            StartSeconds = _project.DurationSeconds,
            InSeconds = 0,
            OutSeconds = media.DurationSeconds
        });
        RefreshTimeline();
        StatusLabel.Text = $"Added {media.Name} to timeline.";
    }

    private void OnTimelineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not TimelineClip clip) return;
        InEntry.Text = clip.InSeconds.ToString(CultureInfo.InvariantCulture);
        OutEntry.Text = clip.OutSeconds.ToString(CultureInfo.InvariantCulture);
        TrackEntry.Text = clip.TrackIndex.ToString(CultureInfo.InvariantCulture);
        PreviewClip(clip);
    }

    private async void OnApplyTrimClicked(object sender, EventArgs e)
    {
        if (TimelineList.SelectedItem is not TimelineClip clip) return;
        if (!double.TryParse(InEntry.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ||
            !double.TryParse(OutEntry.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var end))
        {
            await DisplayAlert("Trim", "Enter valid seconds using a dot as decimal separator.", "OK");
            return;
        }
        var media = _project.Media.FirstOrDefault(m => m.Id == clip.MediaId);
        if (media is null || start < 0 || end <= start || end > media.DurationSeconds)
        {
            await DisplayAlert("Trim", "Trim range must be within media duration.", "OK");
            return;
        }
        clip.InSeconds = start;
        clip.OutSeconds = end;
        TimelineList.ItemsSource = null;
        TimelineList.ItemsSource = _project.Clips;
        RefreshTimeline();
        StatusLabel.Text = "Trim updated.";
    }

    private void OnRemoveClipClicked(object sender, EventArgs e)
    {
        if (TimelineList.SelectedItem is TimelineClip clip)
        {
            _project.Clips.Remove(clip);
            RefreshTimeline();
            StatusLabel.Text = "Clip removed.";
        }
    }

    private void RefreshTimeline()
    {
        TimelineCanvas.Clips = _project.Clips.ToArray();
        TimelineCanvas.Refresh();
    }

    private void PreviewClip(TimelineClip clip)
    {
        var media = _project.Media.FirstOrDefault(m => m.Id == clip.MediaId);
        if (media is null || !File.Exists(media.Path)) return;
        VideoPreview.SourcePath = media.Path;
        VideoPreview.PositionSeconds = clip.InSeconds;
        TimelineCanvas.SelectedId = clip.Id;
        TimelineCanvas.Refresh();
    }

    private void OnPlayClicked(object sender, EventArgs e) => VideoPreview.Play();
    private void OnPauseClicked(object sender, EventArgs e) => VideoPreview.Pause();

    private async void OnMoveTrackClicked(object sender, EventArgs e)
    {
        if (TimelineList.SelectedItem is not TimelineClip clip) return;
        if (!int.TryParse(TrackEntry.Text, out var track) || track < 0 || track > 8)
        {
            await DisplayAlert("Track", "Enter a track index from 0 to 8.", "OK");
            return;
        }
        clip.TrackIndex = track;
        RefreshTimeline();
    }

    private async void OnExportClicked(object sender, EventArgs e)
    {
        if (_project.Clips.Count != 1)
        {
            await DisplayAlert("Export", "This first milestone exports exactly one selected timeline clip. Multi-clip rendering is next.", "OK");
            return;
        }
        var clip = _project.Clips[0];
        var media = _project.Media.FirstOrDefault(m => m.Id == clip.MediaId);
        if (media is null || !File.Exists(media.Path))
        {
            await DisplayAlert("Export", "Source media is missing.", "OK");
            return;
        }
        var output = Path.Combine(FileSystem.AppDataDirectory, "exports", SanitizeFileName(_project.Name) + ".mp4");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            StatusLabel.Text = "Exporting MP4...";
            await FfmpegService.ExportClipAsync(media, clip, output);
            StatusLabel.Text = $"Export complete: {output}";
            await DisplayAlert("Export complete", output, "OK");
        }
        catch (Exception ex) { await ShowError(ex); }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "Untitled" : safe;
    }

    private async Task ShowError(Exception ex)
    {
        StatusLabel.Text = ex.Message;
        await DisplayAlert("NetCut error", ex.Message, "OK");
    }
}
