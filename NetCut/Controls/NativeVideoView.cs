namespace NetCut.Controls;

/// <summary>Native Windows video surface (WinUI MediaPlayerElement); no WebView.</summary>
public sealed class NativeVideoView : View
{
    public static readonly BindableProperty SourcePathProperty =
        BindableProperty.Create(nameof(SourcePath), typeof(string), typeof(NativeVideoView), null);

    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public static readonly BindableProperty PositionSecondsProperty =
        BindableProperty.Create(nameof(PositionSeconds), typeof(double), typeof(NativeVideoView), 0d);

    public double PositionSeconds
    {
        get => (double)GetValue(PositionSecondsProperty);
        set => SetValue(PositionSecondsProperty, value);
    }

    public void Play() => Handler?.Invoke(nameof(Play));
    public void Pause() => Handler?.Invoke(nameof(Pause));
}
