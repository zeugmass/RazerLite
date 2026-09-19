using System.Threading;
using System.Windows;

namespace RazerLite;

public partial class App : Application
{
    private const string MutexName = @"Local\RazerLite-SingleInstance";
    private const string ShowEventName = @"Local\RazerLite-ShowWindow";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private Thread? _showListener;

    private void App_Startup(object sender, StartupEventArgs e)
    {
        // Beklenmeyen hatalar sessizce kaybolmasin: dosyaya yaz ve goster.
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(Settings.Directory);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(Settings.Directory, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {args.Exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch
            {
                // gunluk yazilamazsa yine de kullaniciya goster
            }

            MessageBox.Show(args.Exception.Message, RazerLite.MainWindow.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Tek ornek: ikinci kopya acilirsa ilk kopyanin penceresini one getir ve cik.
        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out bool isFirst);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!isFirst)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        bool startHidden = e.Args.Any(a => string.Equals(a, Autostart.MinimizedArg, StringComparison.OrdinalIgnoreCase));

        var window = new MainWindow();
        if (startHidden)
        {
            window.StartHidden();
        }
        else
        {
            window.Show();
        }

        _showListener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _showEvent.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                Dispatcher.BeginInvoke(window.ShowFromTray);
            }
        })
        {
            IsBackground = true,
            Name = "RazerLite.ShowListener",
        };
        _showListener.Start();
    }

    private void App_Exit(object sender, ExitEventArgs e)
    {
        _showEvent?.Dispose();
        _singleInstance?.Dispose();
    }
}
