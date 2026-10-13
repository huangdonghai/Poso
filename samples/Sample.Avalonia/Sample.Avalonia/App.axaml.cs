using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Sample.Avalonia.ViewModels;
using Sample.Avalonia.Views;

namespace Sample.Avalonia;

public partial class App : Application
{
    /// <summary>Document path passed on the command line (desktop only); null when none.</summary>
    public static string? StartupDocumentPath { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // The graphics backend is injected once and shared by the viewer and thumbnails
        var graphicsFactory = new Poso.Skia.SkiaGraphicsFactory();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A file given on the command line opens instead of the bundled sample document
            StartupDocumentPath = desktop.Args?.FirstOrDefault(
                argument => argument.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && System.IO.File.Exists(argument));

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(graphicsFactory)
            };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime singleViewFactoryApplicationLifetime)
        {
            singleViewFactoryApplicationLifetime.MainViewFactory = () => new MainView { DataContext = new MainViewModel(graphicsFactory) };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView
            {
                DataContext = new MainViewModel(graphicsFactory)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
