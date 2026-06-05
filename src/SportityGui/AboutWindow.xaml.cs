using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace SportityGui;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"Version {AppInfo.Version}";
        SportityLink.NavigateUri = new Uri(AppInfo.SportityUrl);
        GitHubLink.NavigateUri   = new Uri(AppInfo.GitHubUrl);
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
