using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace NetCut.WinUI;

/// <summary>Windows platform entrypoint used by the generated WinUI Main method.</summary>
public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
