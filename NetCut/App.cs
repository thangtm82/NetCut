namespace NetCut;

public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        return new Window(new MainPage())
        {
            Title = "NetCut — Native Video Editor",
            Width = 1400,
            Height = 900,
            MinimumWidth = 960,
            MinimumHeight = 600
        };
    }
}
