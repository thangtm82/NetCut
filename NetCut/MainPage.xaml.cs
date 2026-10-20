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
    private bool _shortcutsAttached;
    private bool _previewPlaying;
    private CancellationTokenSource? _exportCancellation;
    private CancellationTokenSource? _frameCancellation;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _continuousCancellation;
    private readonly ChunkPreviewCache _chunkCache = new();
    private CancellationTokenSource? _chunkCancellation;
    private int _activeChunk = -1;
    private bool _chunkPlayback;
    private double _chunkOffset;

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
        TimelineCanvas.PlayheadScrubStarted += _ => { _previewPlaying = false; VideoPreview.Pause(); };
        TimelineCanvas.PlayheadScrubEnded += _ => OnSeekClicked(this, EventArgs.Empty);
        TimelineCanvas.ScrubEdgePanRequested += deltaPixels =>
        {
            _viewStartSeconds = Math.Clamp(_viewStartSeconds + deltaPixels / Math.Max(.01, TimelineCanvas.PixelsPerSecond), 0, MaxViewStart);
            UpdateViewport();
        };
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

    private void ShowSidebar(string title, bool showLibrary = false)
    {
        SidebarTitle.Text = title;
        bool media = title == "Media";
        ProjectFilesTab.IsVisible = media;
        LibraryTab.IsVisible = media;
        SidebarImportButton.IsVisible = media;
        MediaList.IsVisible = media && !showLibrary;
        LibraryPlaceholder.IsVisible = !media || showLibrary;
        LibraryPlaceholder.Text = media
            ? "Library is reserved for reusable assets. Select Project Files to use imported media."
            : title + " tools are available in the editor panels.";
        ProjectFilesTab.BackgroundColor = Color.FromArgb(showLibrary ? "#F2F3F6" : "#E8EBF1");
        LibraryTab.BackgroundColor = Color.FromArgb(showLibrary ? "#E8EBF1" : "#F2F3F6");
    }

    private void OnShowElementsPanelClicked(object sender, EventArgs e) => ShowSidebar("Elements");
    private void OnShowEffectsPanelClicked(object sender, EventArgs e) => ShowSidebar("Effects");
    private void OnShowTransitionsPanelClicked(object sender, EventArgs e) => ShowSidebar("Transitions");
    private void OnShowVideoPanelClicked(object sender, EventArgs e) => ShowSidebar("Video");
    private void OnShowImagePanelClicked(object sender, EventArgs e) => ShowSidebar("Image");
    private void OnShowSubtitlesPanelClicked(object sender, EventArgs e) => ShowSidebar("Subtitles");
    private void OnShowDetailsPanelClicked(object sender, EventArgs e) => ShowSidebar("Details");
    private void OnShowMediaPanelClicked(object sender, EventArgs e) => ShowSidebar("Media");
    private void OnShowTextPanelClicked(object sender, EventArgs e) => ShowSidebar("Text");
    private void OnShowAudioPanelClicked(object sender, EventArgs e) => ShowSidebar("Audio");
    private void OnShowSettingsPanelClicked(object sender, EventArgs e) => ShowSidebar("Tools");
    private void OnProjectFilesTabClicked(object sender, EventArgs e) => ShowSidebar("Media");
    private void OnLibraryTabClicked(object sender, EventArgs e) => ShowSidebar("Media", true);

    private void BindProject()
    {
        MediaList.ItemsSource = _project.Media;
        TimelineList.ItemsSource = _project.Clips;
        ProjectNameLabel.Text = _project.Name;
        ProjectNameHeader.Text = _project.Name;
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
        _ = PopulateWaveformAsync(media);
        _ = PopulateFilmstripAsync(media);
        StatusLabel.Text = $"Added {media.Name} to timeline.";
    }

    private void OnMediaDragStarting(object sender, DragStartingEventArgs e)
    {
        if (sender is BindableObject view && view.BindingContext is MediaItem media)
            e.Data.Properties["NetCutMediaId"] = media.Id;
    }

    private void OnMediaDropped(object sender, DropEventArgs e)
    {
        if (!e.Data.Properties.TryGetValue("NetCutMediaId", out var value)) return;
        var media = _project.Media.FirstOrDefault(m => m.Id == value as string);
        if (media is null) return;
        // Track and timestamp are controlled explicitly by the inspector and playhead.
        var track = int.TryParse(TrackEntry.Text, out var selectedTrack) ?
            Math.Clamp(selectedTrack, 0, 8) : 0;
        _history.Record(_project);
        var clip = new TimelineClip {
            MediaId = media.Id, Name = media.Name, TrackIndex = track,
            StartSeconds = PlayheadSlider.Value, InSeconds = 0,
            OutSeconds = media.DurationSeconds
        };
        _project.Clips.Add(clip);
        RefreshTimeline();
        TimelineList.SelectedItem = clip;
        _ = PopulateWaveformAsync(media);
        StatusLabel.Text = $"Dropped {media.Name} onto track {track + 1} at {clip.StartSeconds:F2}s.";
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

    private double _viewStartSeconds;
    private double ViewportWidth => Math.Max(1, TimelineViewport?.Width > 1 ? TimelineViewport.Width : 900);
    private double VisibleSeconds => ViewportWidth / Math.Max(0.01, TimelineCanvas.PixelsPerSecond);
    private double MaxViewStart => Math.Max(0, _project.DurationSeconds - VisibleSeconds);

    private void UpdateViewport()
    {
        if (TimelineCanvas is null || TimelinePanSlider is null) return;
        _viewStartSeconds = Math.Clamp(_viewStartSeconds, 0, MaxViewStart);
        TimelineCanvas.ViewStartSeconds = _viewStartSeconds;
        TimelineCanvas.ProjectDurationSeconds = _project.DurationSeconds;
        TimelineCanvas.ViewportWidth = ViewportWidth;
        TimelinePanSlider.Maximum = Math.Max(1, MaxViewStart);
        TimelinePanSlider.Value = _viewStartSeconds;
        TimelineCanvas.Refresh();
    }

    private void OnTimelineViewportSizeChanged(object sender, EventArgs e) => UpdateViewport();

    private void OnTimelinePanChanged(object sender, ValueChangedEventArgs e)
    {
        if (TimelineCanvas is null) return;
        _viewStartSeconds = Math.Clamp(e.NewValue, 0, MaxViewStart);
        TimelineCanvas.ViewStartSeconds = _viewStartSeconds;
        TimelineCanvas.Refresh();
    }

    private void RefreshTimeline()
    {
        TimelineCanvas.Clips = _project.Clips.ToArray();
        TimelineCanvas.Subtitles = _project.Subtitles.ToArray();
        UpdateViewport();
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
        if (TimelineTimeLabel is not null) TimelineTimeLabel.Text = FormatTimelineTime(e.NewValue) + " / " + FormatTimelineTime(_project.DurationSeconds);
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

    private static string FormatTimelineTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.Hours > 0 ? ts.ToString(@"h\:mm\:ss\.ff") : ts.ToString(@"m\:ss\.ff");
    }

    private void OnTimelineBeginningClicked(object sender, EventArgs e)
    {
        PlayheadSlider.Value = 0;
        OnSeekClicked(sender, e);
    }

    private void OnTimelinePlayPauseClicked(object sender, EventArgs e)
    {
        if (_previewPlaying) OnPauseClicked(sender, e);
        else OnPlayClicked(sender, e);
        TimelinePlayButton.Text = _previewPlaying ? "Ⅱ" : "▶";
    }

    private void OnTimelineFitClicked(object sender, EventArgs e)
    {
        double targetScale = Math.Max(0.05, (ViewportWidth - 20) / Math.Max(1, _project.DurationSeconds));
        TimelineZoomSlider.Value = Math.Clamp(1 + 79 * Math.Log(targetScale / 0.05) / Math.Log(800 / 0.05), 1, 80);
        _viewStartSeconds = 0;
        UpdateViewport();
    }

    private async void OnSubtitleToolClicked(object sender, EventArgs e)
    {
        var text = await DisplayPromptAsync("Subtitle", "Subtitle text at the playhead:");
        if (string.IsNullOrWhiteSpace(text)) return;
        _history.Record(_project);
        var start = PlayheadSlider.Value;
        _project.Subtitles.Add(new SubtitleCue { Start = start, End = Math.Min(Math.Max(start + 3, _project.DurationSeconds), start + 3), Text = text });
        RefreshTimeline();
    }

    private void OnSplitAtPlayheadClicked(object sender, EventArgs e)
    {
        var pos = PlayheadSlider.Value;
        var clip = _project.Clips.Where(c => c.StartSeconds < pos && pos < c.StartSeconds + c.DurationSeconds)
            .OrderByDescending(c => c.TrackIndex).FirstOrDefault();
        if (clip is null) return;
        try
        {
            _history.Record(_project);
            TimelineEditor.Split(_project, clip.Id, pos);
            RefreshTimeline();
        }
        catch (ArgumentException ex) { StatusLabel.Text = ex.Message; }
    }

    private void OnTimelineZoomChanged(object sender, ValueChangedEventArgs e)
    {
        if (TimelineCanvas is null || TimelinePanSlider is null) return;
        double oldScale = Math.Max(0.01, TimelineCanvas.PixelsPerSecond);
        double anchorTime = PlayheadSlider.Value;
        double anchorX = (anchorTime - _viewStartSeconds) * oldScale;
        if (anchorX < 0 || anchorX > ViewportWidth)
        {
            anchorX = ViewportWidth / 2;
            anchorTime = _viewStartSeconds + anchorX / oldScale;
        }
        // Logarithmic control: position 1..80 maps to a wide range of px/sec.
        double newScale = Math.Exp(Math.Log(0.05) + (e.NewValue - 1) / 79 * Math.Log(800 / 0.05));
        TimelineCanvas.PixelsPerSecond = newScale;
        _viewStartSeconds = anchorTime - anchorX / newScale;
        UpdateViewport();
    }

    private void OnRippleDeleteClicked(object sender, EventArgs e)
    {
        if (TimelineList.SelectedItem is not TimelineClip clip) return;
        _history.Record(_project);
        AdvancedTimelineEditor.RippleDelete(_project, clip);
        RefreshTimeline();
    }

    private async Task PopulateFilmstripAsync(MediaItem media)
    {
        if (TimelineCanvas.Filmstrips.ContainsKey(media.Id)) return;
        try
        {
            var frames = await TimelineThumbnailService.ExtractAsync(media.Path, media.DurationSeconds);
            if (frames.Count == 0) return;
            TimelineCanvas.Filmstrips[media.Id] = frames;
            TimelineCanvas.Refresh();
        }
        catch (Exception) { /* Missing FFmpeg or unsupported video: keep placeholder tiles. */ }
    }

    private async Task PopulateWaveformAsync(MediaItem media)
    {
        if (TimelineCanvas.Waveforms.ContainsKey(media.Id)) return;
        try
        {
            var peaks = await AudioWaveformService.ExtractAsync(media.Path, buckets: 1200);
            TimelineCanvas.Waveforms[media.Id] = peaks;
            TimelineCanvas.Refresh();
        }
        catch (Exception) { /* Silent media or unavailable FFmpeg: show empty audio lane. */ }
    }

    private async void OnBuildWaveformsClicked(object sender, EventArgs e)
    {
        foreach (var media in _project.Media.Where(m => _project.Clips.Any(c => c.MediaId == m.Id)))
        {
            try { TimelineCanvas.Waveforms[media.Id] = await AudioWaveformService.ExtractAsync(media.Path); }
            catch (Exception) { } // No waveform for sources without audio.
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
