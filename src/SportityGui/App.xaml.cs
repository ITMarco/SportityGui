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

        bool startMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
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
        Services.GetRequiredService<TrayService>().Dispose();
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
        services.AddTransient<MainViewModel>();
    }
}
