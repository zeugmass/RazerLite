using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RazerLite;

/// <summary>
/// Sag altta odak calmayan kucuk uyari penceresi. Windows bildirimleri kapali olsa da gorunur.
/// </summary>
internal sealed class ToastWindow : Window
{
    private static ToastWindow? _current;
    private readonly DispatcherTimer _closeTimer;

    private ToastWindow(string title, string message, int seconds)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Focusable = false;
        Width = 340;
        SizeToContent = SizeToContent.Height;

        var accent = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50));
        var close = new TextBlock
        {
            Text = "✕",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
        };

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = accent,
        });

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6)),
        });

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)),
            BorderBrush = accent,
            BorderThickness = new Thickness(1, 1, 1, 1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 14),
            Child = body,
        };

        MouseLeftButtonUp += (_, _) => Close();

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _closeTimer.Tick += (_, _) => Close();
        // Fare ustundeyken kapanmasin; okumaya zaman tani.
        MouseEnter += (_, _) => _closeTimer.Stop();
        MouseLeave += (_, _) => _closeTimer.Start();

        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
            _closeTimer.Start();
        };
        Closed += (_, _) =>
        {
            _closeTimer.Stop();
            if (_current == this)
            {
                _current = null;
            }
        };
    }

    public static void Show(string title, string message, int seconds = 15)
    {
        _current?.Close();
        _current = new ToastWindow(title, message, seconds);
        _current.Show();
    }

    public static void CloseCurrent() => _current?.Close();

    // QUERY_USER_NOTIFICATION_STATE: 2 BUSY, 3 D3D tam ekran, 4 sunum, 7 tam ekran UWP, 1 kilit ekrani.
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>Tam ekran oyun/sunum veya kilit ekrani yoksa true.</summary>
    public static bool CanShowNow()
    {
        if (SHQueryUserNotificationState(out int state) != 0)
        {
            return true;
        }

        return state is not (1 or 2 or 3 or 4 or 7);
    }
}
