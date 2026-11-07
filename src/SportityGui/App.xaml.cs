using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SportityGui.Services;
using SportityGui.ViewModels;

namespace SportityGui;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // After a self-update: wait for the previous version to exit (it saves state on exit) and
        // remove the exe it left behind, before loading state.
        bool afterUpdate = UpdateService.FinishPendingInstall(e.Args);

        // One instance per install folder: a second launch brings the running one to the front.
        // (Not when started with --minimized, e.g. at login: then just exit quietly.)
        bool minimizedArg = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        if (!SingleInstance.TryAcquire(afterUpdate ? TimeSpan.FromSeconds(10) : TimeSpan.Zero,
                                       signalExisting: !minimizedArg))
        {
            Shutdown();
            return;
        }
        SingleInstance.ListenForShowRequests(() => Dispatcher.Invoke(TrayService.RestoreWindow));

        var collection = new ServiceCollection();
        ConfigureServices(collection);
        Services = collection.BuildServiceProvider();

        var stateService = Services.GetRequiredService<StateService>();
        stateService.Load();

        var window = new MainWindow
        {
            DataContext = Services.GetRequiredService<MainViewModel>()
        };
        MainWindow = window;

        bool startMinimized = stateService.Preferences.StartMinimizedToTray
                           || e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        if (startMinimized)
        {
            Services.GetRequiredService<TrayService>().ShowTrayIcon();
        }
        else
        {
            window.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services is null) { base.OnExit(e); return; } // second instance that exited during startup
        Services.GetRequiredService<TrayService>().Dispose();
        Services.GetRequiredService<DownloadService>().Dispose();
        Services.GetRequiredService<StateService>().Save();
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddHttpClient("sportity", client =>
        {
            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<StateService>();
        services.AddSingleton<ScraperService>();
        services.AddSingleton<DownloadService>();
        services.AddSingleton<TrayService>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddTransient<MainViewModel>();
    }
}
