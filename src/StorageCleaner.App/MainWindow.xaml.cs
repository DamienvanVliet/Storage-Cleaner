using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using StorageCleaner.App.ViewModels;

namespace StorageCleaner.App;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    public MainWindow(MainViewModel mainViewModel)
    {
        InitializeComponent();
        TrySetWindowIcon();
        DataContext = mainViewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableDarkTitleBar();
    }

    private void TryEnableDarkTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
    }

    private void TrySetWindowIcon()
    {
        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/storage-cleaner-icon.png", UriKind.Absolute));
        }
        catch
        {
            // Icon loading should never block app startup.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
