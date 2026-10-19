using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;
using NetCut.Controls;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace NetCut.Platforms.Windows;

public sealed class NativeVideoHandler : ViewHandler<NativeVideoView, MediaPlayerElement>
{
    private MediaPlayer? _player;

    public static readonly IPropertyMapper<NativeVideoView, NativeVideoHandler> Mapper =
        new PropertyMapper<NativeVideoView, NativeVideoHandler>(ViewMapper)
        {
            [nameof(NativeVideoView.SourcePath)] = MapSource,
            [nameof(NativeVideoView.PositionSeconds)] = MapPosition
        };

    public static readonly CommandMapper<NativeVideoView, NativeVideoHandler> Commands =
        new(ViewCommandMapper)
        {
            [nameof(NativeVideoView.Play)] = (h, _, _) => h._player?.Play(),
            [nameof(NativeVideoView.Pause)] = (h, _, _) => h._player?.Pause()
        };

    public NativeVideoHandler() : base(Mapper, Commands) { }

    protected override MediaPlayerElement CreatePlatformView()
    {
        _player = new MediaPlayer { AutoPlay = false };
        var view = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform
        };
        view.SetMediaPlayer(_player);
        return view;
    }

    private static void MapSource(NativeVideoHandler handler, NativeVideoView view)
    {
        if (handler._player is null) return;
        var path = view.SourcePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            handler._player.Source = null;
            return;
        }
        handler._player.Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(path)));
    }

    private static void MapPosition(NativeVideoHandler handler, NativeVideoView view)
    {
        if (handler._player is null) return;
        var seconds = Math.Max(0, view.PositionSeconds);
        handler._player.PlaybackSession.Position = TimeSpan.FromSeconds(seconds);
    }

    protected override void DisconnectHandler(MediaPlayerElement platformView)
    {
        platformView.SetMediaPlayer(null);
        _player?.Dispose();
        _player = null;
        base.DisconnectHandler(platformView);
    }
}
