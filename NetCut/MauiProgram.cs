using Microsoft.Extensions.Logging;

namespace NetCut;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
#if WINDOWS
        builder.ConfigureMauiHandlers(handlers => handlers.AddHandler<NetCut.Controls.NativeVideoView, NetCut.Platforms.Windows.NativeVideoHandler>());
#endif
#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
