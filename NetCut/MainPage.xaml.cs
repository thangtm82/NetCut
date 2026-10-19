using System.Globalization;
using NetCut.Models;
using NetCut.Services;

namespace NetCut;

public partial class MainPage : ContentPage
{
    private ProjectDocument _project = new();
    private string? _projectPath;
    private readonly ProjectHistory _history = new();
    private double? _dragStart;
    private double? _trimStartIn;
    private double? _trimStartOut;
    private CancellationTokenSource? _exportCancellation;
    private CancellationTokenSource? _frameCancellation;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _continuousCancellation;
    private readonly ChunkPreviewCache _chunkCache = new();
    private CancellationTokenSource? _chunkCancellation;
    private int _activeChunk = -1;
    private bool _chunkPlayback;
    private double _chunkOffset;
    private bool _shortcutsAttached;
    private bool _previewPlaying;

    public MainPage()
    {
        InitializeComponent();
#if WINDOWS
        HandlerChanged += (_, _) =>
        {
            if (_shortcutsAttached || Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement root) return;
            _shortcutsAttached = true;
            NetCut.Platforms.Windows.TimelineShortcuts.Attach(root,
                () => OnUndoClicked(this, EventArgs.Empty),
                () => OnRedoClicked(this, EventArgs.Empty),
                () => OnSaveClicked(this, EventArgs.Empty),
                () => { if (_previewPlaying) OnPauseClicked(this, EventArgs.Empty); else OnPlayClicked(this, EventArgs.Empty); },
                () => OnRemoveClipClicked(this, EventArgs.Empty),
                () => OnSplitClicked(this, EventArgs.Empty));
        };
#endif
        TimelineCanvas.StartInteraction += (_, e) => {
            if (e.Touches.Length > 0) TimelineCanvas.BeginPointer(e.Touches[0].X, e.Touches[0].Y);
        };
        TimelineCanvas.DragInteraction += (_, e) => {
            if (e.Touches.Length > 0) TimelineCanvas.MovePointer(e.Touches[0].X);
        };
        TimelineCanvas.EndInteraction += (_, _) => TimelineCanvas.EndPointer();
        TimelineCanvas.ClipDragStarted += clip => {
            _dragStart = clip.StartSeconds;
            _trimStartIn = clip.InSeconds;
            _trimStartOut = clip.OutSeconds;
            _history.Record(_project);
        };
        TimelineCanvas.ClipDragEnded += clip => {
            if (_dragStart is double before && Math.Abs(before - clip.StartSeconds) < 0.001 &&
                _trimStartIn is double initialIn && Math.Abs(initialIn - clip.InSeconds) < 0.001 &&
                _trimStartOut is double initialOut && Math.Abs(initialOut - clip.OutSeconds) < 0.001)
                _history.DiscardLast();
            _trimStartIn = null;
            _trimStartOut = null;
            _dragStart = null;
            RefreshTimeline();
        };
        TimelineCanvas.ClipSelected += clip => {
            TimelineList.SelectedItem = clip;
            PreviewClip(clip);
        };
        TimelineCanvas.PlayheadRequested += seconds => PlayheadSlider.Value = Math.Clamp(seconds, 0, PlayheadSlider.Maximum);
        TimelineCanvas.ClipTrimmed += (clip, left, seconds) =>
        {
            try
            {
                if (left) AdvancedTimelineEditor.TrimLeft(_project, clip, seconds, RippleCheckBox.IsChecked);
                else AdvancedTimelineEditor.TrimRight(_project, clip, seconds, RippleCheckBox.IsChecked);
                RefreshTimeline();
            }
            catch (ArgumentException) { }
        };
        TimelineCanvas.ClipMoved += (clip, seconds) => {
            clip.StartSeconds = TimelineEditor.Snap(seconds, _project.Clips, clip.Id);
            StatusLabel.Text = $"Moved {clip.Name} to {seconds:F2}s";
        };
        VideoPreview.PlaybackPositionChanged += (_, seconds) =>
        {
            if (_chunkPlayback) PlayheadSlider.Value = Math.Clamp(_chunkOffset + seconds, 0, _project.DurationSeconds);
        };
        VideoPreview.PlaybackEnded += async (_, _) =>
        {
            if (!_chunkPlayback || _chunkCancellation is null) return;
            try { await PlayChunkAsync(_activeChunk + 1, 0, _chunkCancellation.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { StopChunks(); await ShowError(ex); }
        };
        BindProject();
    }

    private void BindProject()
    {
        MediaList.ItemsSource = _project.Media;
        TimelineList.ItemsSource = _project.Clips;
        ProjectNameLabel.Text = _project.Name;
        RefreshTimeline();
        PlayheadSlider.Maximum = Math.Max(1, _project.DurationSeconds);
    }

    private async void OnNewClicked(object sender, EventArgs e)
    {
        var name = await DisplayPromptAsync("New project", "Project name", initialValue: "Untitled Project");
        if (name is null) return;
        StopChunks();
        _project = new ProjectDocument { Name = string.IsNullOrWhiteSpace(name) ? "Untitled Project" : name.Trim() };
        _projectPath = null;
        _history.Clear();
        BindProject();
        StatusLabel.Text = "Created a new project.";
    }

    private async void OnOpenClicked(object sender, EventArgs e)
    {
        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Open NetCut JSON project" });
            if (file is null) return;
            StopChunks();
            _project = await ProjectService.LoadAsync(file.FullPath);
            _projectPath = file.FullPath;
            _history.Clear();
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
        _history.Record(_project);
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
        _history.Record(_project);
        TimelineEditor.Trim(_project, clip.Id, start, end);
        TimelineList.ItemsSource = null;
        TimelineList.ItemsSource = _project.Clips;
        RefreshTimeline();
        StatusLabel.Text = "Trim updated.";
    }

    private void OnRemoveClipClicked(object sender, EventArgs e)
    {
        if (TimelineList.SelectedItem is TimelineClip clip)
        {
            _history.Record(_project);
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

    private void OnPlayClicked(object sender, EventArgs e) { _previewPlaying = true; VideoPreview.Play(); }
    private void OnPauseClicked(object sender, EventArgs e) { _previewPlaying = false; VideoPreview.Pause(); }

    private async void OnMoveTrackClicked(object sender, EventArgs e)
    {
        if (TimelineList.SelectedItem is not TimelineClip clip) return;
        if (!int.TryParse(TrackEntry.Text, out var track) || track < 0 || track > 8)
        {
            await DisplayAlert("Track", "Enter a track index from 0 to 8.", "OK");
            return;
        }
        _history.Record(_project);
        TimelineEditor.Move(clip, clip.StartSeconds, track);
        RefreshTimeline();
    }

    private async void OnSplitClicked(object sender, EventArgs e)
    {
        if (TimelineList.SelectedItem is not TimelineClip clip)
        {
            await DisplayAlert("Split", "Select a timeline clip first.", "OK");
            return;
        }
        var entered = await DisplayPromptAsync("Split clip", "Timeline time in seconds:",
            initialValue: (clip.StartSeconds + clip.DurationSeconds / 2).ToString(CultureInfo.InvariantCulture));
        if (entered is null) return;
        if (!double.TryParse(entered, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            await DisplayAlert("Split", "Invalid timestamp.", "OK");
            return;
        }
        try
        {
            _history.Record(_project);
            var (_, right) = TimelineEditor.Split(_project, clip.Id, seconds);
            RefreshTimeline();
            TimelineList.SelectedItem = right;
            StatusLabel.Text = $"Split {clip.Name} at {seconds:F2}s";
        }
        catch (Exception ex) { await ShowError(ex); }
    }

    private void OnUndoClicked(object sender, EventArgs e)
    {
        if (!_history.CanUndo) return;
        _project = _history.Undo(_project);
        BindProject();
        StatusLabel.Text = "Undo complete.";
    }

    private void OnRedoClicked(object sender, EventArgs e)
    {
        if (!_history.CanRedo) return;
        _project = _history.Redo(_project);
        BindProject();
        StatusLabel.Text = "Redo complete.";
    }

    private void OnPlayheadChanged(object sender, ValueChangedEventArgs e)
    {
        TimelineCanvas.PlayheadSeconds = e.NewValue;
        TimelineCanvas.Refresh();
        PlayheadLabel.Text = TimeSpan.FromSeconds(e.NewValue).ToString(@"mm\:ss") +
            " / " + TimeSpan.FromSeconds(_project.DurationSeconds).ToString(@"mm\:ss");
    }

    private void OnSeekClicked(object sender, EventArgs e)
    {
        var time = PlayheadSlider.Value;
        var clip = _project.Clips.Where(c => c.StartSeconds <= time &&
            time < c.StartSeconds + c.DurationSeconds)
            .OrderByDescending(c => c.TrackIndex).FirstOrDefault();
        if (clip is null) { VideoPreview.Pause(); StatusLabel.Text = "No clip at playhead."; return; }
        var media = _project.Media.FirstOrDefault(m => m.Id == clip.MediaId);
        if (media is null || !File.Exists(media.Path)) return;
        CompositedFrame.IsVisible = false;
        VideoPreview.IsVisible = true;
        VideoPreview.Pause();
        VideoPreview.SourcePath = media.Path;
        VideoPreview.PositionSeconds = clip.InSeconds + time - clip.StartSeconds;
        StatusLabel.Text = "Source-only seek: " + clip.Name + " (not composited preview)";
    }

    private async void OnRenderFrameClicked(object sender, EventArgs e)
    {
        _frameCancellation?.Cancel();
        _frameCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _frameCancellation = cancellation;
        var framePath = Path.Combine(FileSystem.CacheDirectory, "netcut-preview-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            StatusLabel.Text = "Rendering composite frame...";
            await TimelineFramePreview.RenderAsync(_project, PlayheadSlider.Value, framePath, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            VideoPreview.Pause();
            VideoPreview.IsVisible = false;
            CompositedFrame.Source = ImageSource.FromFile(framePath);
            CompositedFrame.IsVisible = true;
            StatusLabel.Text = "Composite frame rendered (still image, not real-time playback).";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await ShowError(ex); }
        finally
        {
            if (ReferenceEquals(_frameCancellation, cancellation)) _frameCancellation = null;
            cancellation.Dispose();
        }
    }

    private async void OnPreviewSegmentClicked(object sender, EventArgs e)
    {
        if (_previewCancellation is not null) return;
        _previewCancellation = new CancellationTokenSource();
        PreviewSegmentButton.IsEnabled = false;
        try
        {
            StatusLabel.Text = "Rendering 5-second composed preview...";
            var proxy = await PreviewSegmentService.RenderAsync(_project,
                PlayheadSlider.Value, ct: _previewCancellation.Token);
            CompositedFrame.IsVisible = false;
            VideoPreview.IsVisible = true;
            VideoPreview.Pause();
            VideoPreview.SourcePath = proxy;
            VideoPreview.PositionSeconds = 0;
            VideoPreview.Play();
            StatusLabel.Text = "Playing composed preview segment (pre-rendered proxy).";
        }
        catch (Exception ex) { await ShowError(ex); }
        finally
        {
            _previewCancellation?.Dispose();
            _previewCancellation = null;
            PreviewSegmentButton.IsEnabled = true;
        }
    }

    private async void OnContinuousPreviewClicked(object sender, EventArgs e)
    {
        if (_continuousCancellation is not null) return;
        var cancellation = new CancellationTokenSource();
        _continuousCancellation = cancellation;
        ContinuousPreviewButton.IsEnabled = false;
        try
        {
            StatusLabel.Text = "Preparing full composed preview (cached render)...";
            var progress = new Progress<double>(fraction =>
            {
                StatusLabel.Text = $"Preparing preview... {fraction:P0}";
            });
            var proxy = await ContinuousPreviewCache.GetOrRenderAsync(_project, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            CompositedFrame.IsVisible = false;
            VideoPreview.IsVisible = true;
            VideoPreview.Pause();
            VideoPreview.SourcePath = proxy;
            VideoPreview.PositionSeconds = Math.Clamp(PlayheadSlider.Value, 0, _project.DurationSeconds);
            VideoPreview.Play();
            StatusLabel.Text = "Playing cached full-timeline proxy. Use native transport controls to seek.";
        }
        catch (OperationCanceledException) { StatusLabel.Text = "Preview render cancelled."; }
        catch (Exception ex) { await ShowError(ex); }
        finally
        {
            _continuousCancellation = null;
            cancellation.Dispose();
            ContinuousPreviewButton.IsEnabled = true;
        }
    }

    private void OnStopContinuousPreviewClicked(object sender, EventArgs e)
    {
        _continuousCancellation?.Cancel();
        VideoPreview.Pause();
        StatusLabel.Text = "Preview stopped.";
    }

    private async void OnPlayChunksClicked(object sender, EventArgs e)
    {
        StopChunks();
        _chunkCancellation = new CancellationTokenSource();
        _chunkPlayback = true;
        var index = ChunkPreviewCache.Index(PlayheadSlider.Value);
        try { await PlayChunkAsync(index, PlayheadSlider.Value - ChunkPreviewCache.Start(index), _chunkCancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StopChunks(); await ShowError(ex); }
    }

    private async Task PlayChunkAsync(int index, double offset, CancellationToken token)
    {
        if (ChunkPreviewCache.Start(index) >= _project.DurationSeconds)
        {
            StopChunks();
            StatusLabel.Text = "Timeline preview complete.";
            return;
        }
        StatusLabel.Text = $"Preparing chunk {index + 1}...";
        var proxy = await _chunkCache.GetAsync(_project, index, token);
        token.ThrowIfCancellationRequested();
        _activeChunk = index;
        _chunkOffset = ChunkPreviewCache.Start(index);
        CompositedFrame.IsVisible = false;
        VideoPreview.IsVisible = true;
        VideoPreview.Pause();
        VideoPreview.SourcePath = proxy;
        VideoPreview.PositionSeconds = offset;
        VideoPreview.Play();
        StatusLabel.Text = $"Playing chunk {index + 1}";
        _ = PrefetchChunkAsync(index + 1, token);
    }

    private async Task PrefetchChunkAsync(int index, CancellationToken token)
    {
        if (ChunkPreviewCache.Start(index) >= _project.DurationSeconds) return;
        try { await _chunkCache.GetAsync(_project, index, token); }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private void StopChunks()
    {
        _chunkPlayback = false;
        _chunkCancellation?.Cancel();
        _chunkCancellation = null;
        VideoPreview.Pause();
    }

    private void OnStopChunksClicked(object sender, EventArgs e)
    {
        StopChunks();
        StatusLabel.Text = "Chunk preview stopped.";
    }

    private void OnTimelineZoomChanged(object sender, ValueChangedEventArgs e)
    {
        if (TimelineCanvas is null) return;
        TimelineCanvas.PixelsPerSecond = e.NewValue;
        TimelineCanvas.Refresh();
    }

    private void OnRippleDeleteClicked(object sender, EventArgs e)
    {
        if (TimelineList.SelectedItem is not TimelineClip clip) return;
        _history.Record(_project);
        AdvancedTimelineEditor.RippleDelete(_project, clip);
        RefreshTimeline();
    }

    private void OnMediaDragStarting(object sender, DragStartingEventArgs e)
    {
        if (sender is BindableObject view && view.BindingContext is MediaItem media)
            e.Data.Properties["NetCutMediaId"] = media.Id;
    }

    private void OnMediaDropped(object sender, DropEventArgs e)
    {
        if (!e.Data.Properties.TryGetValue("NetCutMediaId", out var id)) return;
        var media = _project.Media.FirstOrDefault(m => m.Id == id as string);
        if (media is null) return;
        _history.Record(_project);
        _project.Clips.Add(new TimelineClip {
            MediaId = media.Id, Name = media.Name,
            StartSeconds = PlayheadSlider.Value, InSeconds = 0,
            OutSeconds = media.DurationSeconds,
            TrackIndex = int.TryParse(TrackEntry.Text, out var index) ? Math.Clamp(index, 0, 8) : 0
        });
        RefreshTimeline();
        StatusLabel.Text = "Dropped " + media.Name + " at playhead.";
    }

    private async void OnBuildWaveformsClicked(object sender, EventArgs e)
    {
        foreach (var media in _project.Media.Where(m => _project.Clips.Any(c => c.MediaId == m.Id)))
        {
            try
            {
                TimelineCanvas.Waveforms[media.Id] = await AudioWaveformService.ExtractAsync(media.Path);
            }
            catch (Exception) { } // Media without audio has no waveform.
        }
        TimelineCanvas.Refresh();
        StatusLabel.Text = "Waveforms generated for available audio tracks.";
    }

    private void OnCancelExportClicked(object sender, EventArgs e) => _exportCancellation?.Cancel();

    private async void OnExportClicked(object sender, EventArgs e)
    {
        if (_project.Clips.Count == 0)
        {
            await DisplayAlert("Export", "No timeline clips to render.", "OK");
            return;
        }
        var output = Path.Combine(FileSystem.AppDataDirectory, "exports", SanitizeFileName(_project.Name) + ".mp4");
        if (_exportCancellation is not null) return;
        _exportCancellation = new CancellationTokenSource();
        ExportButton.IsEnabled = false;
        CancelExportButton.IsEnabled = true;
        ExportProgressBar.Progress = 0;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            StatusLabel.Text = "Exporting MP4...";
            var progress = new Progress<double>(value =>
            {
                ExportProgressBar.Progress = value;
                StatusLabel.Text = $"Exporting MP4... {value:P0}";
            });
            await FfmpegService.ExportCompositionAsync(_project, output, _exportCancellation.Token, progress);
            StatusLabel.Text = $"Export complete: {output}";
            await DisplayAlert("Export complete", output, "OK");
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "Export cancelled.";
            try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }
        }
        catch (Exception ex) { await ShowError(ex); }
        finally
        {
            _exportCancellation?.Dispose();
            _exportCancellation = null;
            ExportButton.IsEnabled = true;
            CancelExportButton.IsEnabled = false;
        }
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
