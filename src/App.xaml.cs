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

        if (HandleHelperArgs(e.Args))
        {
            return;
        }

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

        if (Autostart.RelocateAndExitCurrent(e.Args))
        {
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

    /// <summary>Kurulum ve yonetici guncellemesi tek-ornek kilidinden once biter; yoksa kopyalama ikinci kopya sanilip atlanir.</summary>
    private static bool HandleHelperArgs(string[] args)
    {
        var installAt = Array.FindIndex(args, a => string.Equals(a, Autostart.InstallArg, StringComparison.OrdinalIgnoreCase));
        if (installAt >= 0)
        {
            var source = installAt + 1 < args.Length ? args[installAt + 1] : Environment.ProcessPath;
            try
            {
                if (string.IsNullOrWhiteSpace(source))
                {
                    throw new InvalidOperationException("Kopyalanacak program bulunamadı.");
                }

                Autostart.CopyToInstallFolder(source);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, RazerLite.MainWindow.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(1);
            }

            return true;
        }

        var updateAt = Array.FindIndex(args, a => string.Equals(a, Updater.ApplyUpdateArg, StringComparison.OrdinalIgnoreCase));
        if (updateAt >= 0 && updateAt + 1 < args.Length)
        {
            try
            {
                Updater.ReplaceAndRestart(args[updateAt + 1], allowElevate: false);
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, RazerLite.MainWindow.AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                Environment.Exit(1);
            }

            return true;
        }

        return false;
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
        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Bu is parcacigi sahibi degil; kapatmak yine de tutamagi birakir.
        }

        mutex.Dispose();
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
