using System.IO;
using System.Windows;

namespace SportityGui.Services;

public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;

    public TrayService()
    {
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "SportityGui",
            Icon = LoadIcon(),
            Visible = false
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Show SportityGui", null, (_, _) =>
            Application.Current.Dispatcher.Invoke(RestoreWindow));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown()));
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) =>
            Application.Current.Dispatcher.Invoke(RestoreWindow);
    }

    public void ShowTrayIcon() => _icon.Visible = true;
    public void HideTrayIcon() => _icon.Visible = false;

    public void ShowNotification(string title, string message)
    {
        _icon.Visible = true;
        _icon.ShowBalloonTip(5000, title, message, System.Windows.Forms.ToolTipIcon.Info);
    }

    private static void RestoreWindow()
    {
        var window = Application.Current.MainWindow;
        if (window == null) return;
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            // Prefer the real .ico file
            var icoUri = new Uri("pack://application:,,,/Assets/Sportity.ico");
            var icoRes = System.Windows.Application.GetResourceStream(icoUri);
            if (icoRes != null) return new System.Drawing.Icon(icoRes.Stream);
        }
        catch { }
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/sportitylogo1.png");
            var resource = System.Windows.Application.GetResourceStream(uri);
            if (resource != null)
            {
                using var bmp = new System.Drawing.Bitmap(resource.Stream);
                using var resized = new System.Drawing.Bitmap(bmp, 32, 32);
                using var icoStream = new MemoryStream();
                WriteIco(resized, icoStream);
                icoStream.Position = 0;
                return new System.Drawing.Icon(icoStream);
            }
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private static void WriteIco(System.Drawing.Bitmap bmp, Stream dest)
    {
        using var pngMs = new MemoryStream();
        bmp.Save(pngMs, System.Drawing.Imaging.ImageFormat.Png);
        var png = pngMs.ToArray();

        // Minimal ICO: 6-byte ICONDIR + 16-byte ICONDIRENTRY + PNG payload
        using var w = new BinaryWriter(dest, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write((short)0);                                        // reserved
        w.Write((short)1);                                        // type = ICO
        w.Write((short)1);                                        // image count
        w.Write((byte)(bmp.Width  < 256 ? bmp.Width  : 0));      // width  (0 = 256)
        w.Write((byte)(bmp.Height < 256 ? bmp.Height : 0));      // height
        w.Write((byte)0);                                         // palette colors
        w.Write((byte)0);                                         // reserved
        w.Write((short)1);                                        // color planes
        w.Write((short)32);                                       // bits per pixel
        w.Write(png.Length);                                      // image data size
        w.Write(22);                                              // image data offset (6 + 16)
        w.Flush();
        dest.Write(png, 0, png.Length);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
