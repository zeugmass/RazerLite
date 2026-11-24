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
        // Guncelleme (--updated) eski surec kilidi birakana kadar bekler. Kilit
        // createdNew ile degil WaitOne ile alinir; birakilan kilit hemen devralinir.
        bool afterUpdate = e.Args.Any(a => string.Equals(a, Updater.UpdatedArg, StringComparison.OrdinalIgnoreCase));
        _singleInstance = new Mutex(initiallyOwned: false, MutexName);
        bool owned;
        try
        {
            owned = _singleInstance.WaitOne(afterUpdate ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!owned)
        {
            _showEvent.Set();
            Shutdown();
            return;
        }

        Updater.CleanupOldExe();

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

    /// <summary>Yeni surum sureci acilmadan hemen once cagrilir. Kilit bizde kalirsa yeni kopya acilamaz.</summary>
    public static void ReleaseSingleInstance()
    {
        if (Current is not App app)
        {
            return;
        }

        var mutex = app._singleInstance;
        app._singleInstance = null;
        mutex?.Dispose();
    }

    /// <summary>Yeni surec acilamazsa kilidi geri al; pencere acik kalsin.</summary>
    public static void ReacquireSingleInstance()
    {
        if (Current is not App app || app._singleInstance is not null)
        {
            return;
        }

        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName);
            if (!mutex.WaitOne(TimeSpan.Zero))
            {
                mutex.Dispose();
                return;
            }

            app._singleInstance = mutex;
        }
        catch (AbandonedMutexException)
        {
            app._singleInstance = mutex;
        }
    }

    private void App_Exit(object sender, ExitEventArgs e)
    {
        _showEvent?.Dispose();
        _singleInstance?.Dispose();
        _singleInstance = null;
    }
}
