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
        BindProject();
    }

    private void BindProject()
    {
        MediaList.ItemsSource = _project.Media;
        TimelineList.ItemsSource = _project.Clips;
        ProjectNameLabel.Text = _project.Name;
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
        StatusLabel.Text = $"Added {media.Name} to timeline.";
    }

    private void OnTimelineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not TimelineClip clip) return;
        InEntry.Text = clip.InSeconds.ToString(CultureInfo.InvariantCulture);
        OutEntry.Text = clip.OutSeconds.ToString(CultureInfo.InvariantCulture);
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
        StatusLabel.Text = "Trim updated.";
    }

    private void OnRemoveClipClicked(object sender, EventArgs e)
    {
        if (TimelineList.SelectedItem is TimelineClip clip)
        {
            _project.Clips.Remove(clip);
            StatusLabel.Text = "Clip removed.";
        }
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
