using System.Windows;

namespace SportityGui;

public partial class RemoveChannelDialog : Window
{
    public bool DeleteFiles => DeleteFilesRadio.IsChecked == true;

    public RemoveChannelDialog(string channelName)
    {
        InitializeComponent();
        HeaderText.Text = $"Remove \"{channelName}\"?";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
