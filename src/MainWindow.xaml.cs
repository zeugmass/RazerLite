using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RazerLite;

public partial class MainWindow : Window
{
    /// <summary>"RazerLite 0.16"; surum RazerLite.csproj &lt;Version&gt;'dan gelir (AppInfo).</summary>
    public static readonly string AppTitle = "RazerLite " + AppInfo.VersionText;

    private HidWin.Device? _device;
    private bool _busy;

    // Ek ozellikler
    private readonly Settings _settings;
    private TrayIcon? _tray;
    private HotKeys? _hotkeys;
    private readonly DispatcherTimer _batteryTimer;
    private readonly DispatcherTimer _deviceChangeTimer;
    private HwndSource? _hwndSource;
    private bool _started;
    private bool _exiting;
    private bool _syncingUi;
    private bool _trayHintShown;
    private int _lastDpi;
    private int _lastHz;
    private Protocol.BatteryInfo? _lastBattery;
    private bool _needsFullRead;      // son cihaz okumasi eksik kaldi; fare yanit verince tamamla
    private bool _linkReady;          // bu oturumda DPI ve Hz bir kez okundu
    private bool _connectWaitLogged;  // ilk acilista telsiz beklerken logu tekrarlama
    private bool _batteryBusyLogged;  // arka plan pil okumasinda BUSY spam'ini onle
    private bool _forceReconnect;     // uyku/uyanma sonrasi handle olmus olabilir; path ayni olsa bile yeniden ac
    private int _resumeRetries;       // uyanma sonrasi cihaz henuz yoksa kac kez daha denenecek
    private int? _lowBatteryWarnedAt; // son uyarinin yapildigi %; sarj/yukselince sifirlanir
    private DeviceProbe.Result? _probe; // son baglantidaki salt-okuma tarama (tanilama raporu icin)
    private bool _lowBatteryDeferLogged;

    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x6C, 0xC2, 0x5C));
    private static readonly Brush AmberBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x40));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50));
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));

    public MainWindow()
    {
        InitializeComponent();
        Title = AppTitle;
        SidebarVersionText.Text = "v" + AppInfo.VersionText;
        AboutVersionText.Text = "Sürüm " + AppInfo.VersionText;

        _settings = Settings.Load();
        // Kayit defteri gercek durumdur; ayar dosyasini ona esitle.
        _settings.StartWithWindows = Autostart.IsEnabled();

        _batteryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
        _batteryTimer.Tick += BatteryTimer_Tick;

        // USB tak/cikar olaylarini toplayip 1 sn sonra tek seferde degerlendir.
        _deviceChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _deviceChangeTimer.Tick += DeviceChangeTimer_Tick;

        _firmwareStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _firmwareStatusTimer.Tick += (_, _) =>
        {
            _firmwareStatusTimer.Stop();
            ShowFirmwareStatus("");
        };

        // Windows kapanirken/oturum kapanirken tepsiye kucultme yerine gercekten kapan.
        Application.Current.SessionEnding += (_, _) => _exiting = true;

        InitTrayAndHotkeys();
        SyncSettingsUi();
        RefreshProfileUi();

        // Sag sutun: gorunen son cihaz karti kalan yuksekligi doldursun (kablolu modda LED karti gizlenir).
        var visibility = DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement));
        foreach (var card in DeviceCards)
        {
            visibility.AddValueChanged(card, (_, _) => UpdateDeviceCardFill());
        }

        UpdateDeviceCardFill();
    }

    private void Log(string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}";
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() =>
            {
                DebugBox.AppendText(line);
                DebugBox.ScrollToEnd();
            });
            return;
        }

        DebugBox.AppendText(line);
        DebugBox.ScrollToEnd();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ApplyButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        DpiBox.IsEnabled = !busy;
        PollingBox.IsEnabled = !busy;
        ProfileBox.IsEnabled = !busy;
        SaveProfileButton.IsEnabled = !busy;
        DeleteProfileButton.IsEnabled = !busy;
        WriteStagesButton.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : Cursors.Arrow;
    }

    /// <summary>Kablo aktif baglantiyken yalnizca kablolu Hz okunur ve yazilir.</summary>
    private bool WiredLink => _device?.ExtendedPolling == false;

    private int SelectedHz()
    {
        if (PollingBox.SelectedItem is ComboBoxItem item && item.Tag is not null)
        {
            return Convert.ToInt32(item.Tag);
        }

        return 1000;
    }

    /// <summary>Cihazin bu baglantida destekledigi Hz secenekleri disindakileri pasiflestirir.</summary>
    private void UpdatePollingChoices()
    {
        var supported = _device is null
            ? (IReadOnlyCollection<int>)Protocol.PollToCode.Keys
            : Protocol.SupportedRates(_device);

        foreach (ComboBoxItem item in PollingBox.Items)
        {
            int hz = Convert.ToInt32(item.Tag);
            bool ok = supported.Contains(hz);
            item.IsEnabled = ok;
            item.Content = ok ? $"{hz} Hz" : $"{hz} Hz   (bu bağlantıda yok)";
        }

        if (_device is not null && _device.ExtendedPolling == false)
        {
            Log($"POLLING CAPS -> klasik komut: {string.Join("/", supported.OrderBy(h => h))} Hz");
        }
    }

    /// <summary>Istenen Hz bu baglantida yoksa en yakin desteklenen degeri verir.</summary>
    private int ClampHz(int hz, out int wanted)
    {
        wanted = hz;
        if (_device is null)
        {
            return hz;
        }

        var supported = Protocol.SupportedRates(_device);
        if (supported.Contains(hz))
        {
            return hz;
        }

        return supported.OrderBy(h => Math.Abs(h - hz)).ThenByDescending(h => h).First();
    }

    private void SelectHz(int hz)
    {
        foreach (ComboBoxItem item in PollingBox.Items)
        {
            if (Convert.ToInt32(item.Tag) == hz)
            {
                PollingBox.SelectedItem = item;
                return;
            }
        }
    }

    private static string? FindPollingTester()
    {
        var names = new[]
        {
            "PollingRateTesterApp_v1.00.01.exe",
            "PollingRateTesterApp_v1.00.01",
        };

        var dirs = new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath) ?? "",
            Directory.GetCurrentDirectory(),
        };

        foreach (var dir in dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct())
        {
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return ExtractEmbeddedTester();
    }

    /// <summary>Exe'ye gomulu test aracini %TEMP%\RazerLite'a cikarir; ayni boyutta kopya varsa onu kullanir.</summary>
    private static string? ExtractEmbeddedTester()
    {
        const string name = "PollingRateTesterApp_v1.00.01.exe";
        using var res = typeof(MainWindow).Assembly.GetManifestResourceStream(name);
        if (res is null)
        {
            return null;
        }

        var dir = Path.Combine(Path.GetTempPath(), "RazerLite");
        var path = Path.Combine(dir, name);
        if (File.Exists(path) && new FileInfo(path).Length == res.Length)
        {
            return path;
        }

        Directory.CreateDirectory(dir);
        using (var file = File.Create(path))
        {
            res.CopyTo(file);
        }
        return path;
    }

    // ------------------------------------------------------------------
    // Baslatma
    // ------------------------------------------------------------------

    /// <summary>--minimized ile acildiginda: pencere gosterilmez, cihaz tepsiden acilir.</summary>
    public void StartHidden()
    {
        WindowState = WindowState.Minimized;
        Log("Tepside baslatildi (--minimized).");
        Dispatcher.BeginInvoke(DispatcherPriority.Background, StartupAsync);
    }

    private async void Window_ContentRendered(object sender, EventArgs e)
    {
        await StartupAsync();
    }

    private async Task StartupAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Log("========================================");
        Log($"{AppTitle} baslatiliyor.");
        Log("Mode -> WINDOWS HID");
        Log("Desteklenen: " + string.Join(", ", HidWin.KnownDevices.Values.Select(d => d.Title).Distinct()));
        Log("========================================");

        await ConnectAsync();
        MaybeAutoCheckForUpdate();
    }

    /// <summary>Uygun Razer cihazini (kablolu fare > dongle > kablosuz) acar ve okur. Cihaz yoksa bekleme durumuna gecer.</summary>
    private async Task ConnectAsync()
    {
        SetBusy(true);
        try
        {
            _device = await Task.Run(() => HidWin.Open());
            DeviceSubtitle.Text = _device.Info.Title;
            SetLinkIcon(_device.Info.Mode == HidWin.LinkMode.Wired);
            Log($"DEVICE -> {_device.Info.Title} (VID=1532 PID={_device.Pid:X4})");
            Log($"HID CONNECTION -> READY");
            Log($"PATH -> {_device.Path}");

            // Salt-okuma tarama: transaction ID / arayuz dogru mu (bilinen cihazlarda tek okuma).
            _probe = await DeviceProbe.RunAsync(_device, Log);
            if (_probe.UsedFallback)
            {
                Log($"PATH -> {_device.Path}");
            }

            // Ilk okuma. Windows acilisinda dongle hemen gorunur, fare telsizi birkac saniye sonra gelir.
            // Synapse bu arada bekleyip kendisi tekrar dener; kullanicidan Yenile istemez.
            _linkReady = false;
            await ReadDeviceAsync(quiet: true);
            _batteryTimer.Interval = _linkReady ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(3);
            _batteryTimer.Start();
        }
        catch (Exception ex)
        {
            _device?.Dispose();
            _device = null;
            EnterWaitingState();
            Log("CONNECT -> " + ex.Message);
            Log("Dongle veya fare kablosu takildiginda otomatik baglanilacak.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void EnterWaitingState()
    {
        _batteryTimer.Stop();
        _probe = null;
        DeviceSubtitle.Text = "Cihaz bekleniyor…";
        SetLinkIcon(false);
        StatusText.Text = "Cihaz bulunamadı";
        CurrentText.Text = "Dongle'ı veya fare kablosunu tak; otomatik bağlanır.";
        BatteryText.Text = "";
        SetBatteryRing(null);
        LinkText.Text = "";
        _linkReady = false;
        _connectWaitLogged = false;
        _needsFullRead = false;
        StagesHeaderText.Text = "—";
        SleepPanel.Visibility = Visibility.Collapsed;
        _sleepMinutes = null;
        LowPowerPanel.Visibility = Visibility.Collapsed;
        _lowPowerPercent = null;
        SurfacePanel.Visibility = Visibility.Collapsed;
        DongleLedPanel.Visibility = Visibility.Collapsed;
        _firmwareGeneration++;
        _mouseFirmware = null;
        _dongleFirmware = null;
        FirmwareMouseText.Text = "—";
        FirmwareDongleText.Text = "—";
        ShowFirmwareStatus("");
        ResetFirmwareAction();
        FirmwareButton.IsEnabled = false;
        _lastBattery = null;
        _lowBatteryWarnedAt = null;
        UpdateTrayToolTip();
    }

    // ------------------------------------------------------------------
    // USB tak/cikar + uyku/uyanma izleme
    // ------------------------------------------------------------------

    private const int WmDeviceChange = 0x0219;
    private const int WmPowerBroadcast = 0x0218;
    private const int DbtDevNodesChanged = 0x0007;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeCritical = 0x0006;
    private const int PbtApmResumeSuspend = 0x0007;
    private const int PbtApmResumeAutomatic = 0x0012;

    private void InitDeviceWatcher()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(DeviceChangeHook);
    }

    private IntPtr DeviceChangeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmPowerBroadcast)
        {
            int ev = wParam.ToInt32();
            if (ev == PbtApmSuspend)
            {
                // Uykuya girerken handle'i hemen birak; uyaninca taze acilir.
                Log("POWER -> uyku / bekletme");
                _batteryTimer.Stop();
                if (_device is not null)
                {
                    _device.Dispose();
                    _device = null;
                }

                return IntPtr.Zero;
            }

            if (ev is PbtApmResumeAutomatic or PbtApmResumeSuspend or PbtApmResumeCritical)
            {
                // USB yigini uyanmadan hemen sonra hazir olmayabilir; 2 sn sonra zorla yeniden baglan.
                Log("POWER -> uyanma, cihaz yeniden baglanacak");
                _resumeRetries = 0;
                ScheduleDeviceScan(forceReconnect: true, delayMs: 2000);
            }

            return IntPtr.Zero;
        }

        if (msg == WmDeviceChange)
        {
            int ev = wParam.ToInt32();
            if (ev is DbtDevNodesChanged or DbtDeviceArrival or DbtDeviceRemoveComplete)
            {
                ScheduleDeviceScan(forceReconnect: false, delayMs: 1000);
            }
        }

        return IntPtr.Zero;
    }

    private void ScheduleDeviceScan(bool forceReconnect, int delayMs)
    {
        if (forceReconnect)
        {
            _forceReconnect = true;
        }

        _deviceChangeTimer.Stop();
        _deviceChangeTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _deviceChangeTimer.Start();
    }

    private async void DeviceChangeTimer_Tick(object? sender, EventArgs e)
    {
        _deviceChangeTimer.Stop();
        _deviceChangeTimer.Interval = TimeSpan.FromSeconds(1);
        if (!_started || _exiting)
        {
            return;
        }

        if (_busy)
        {
            // Hz degisimi vb. surerken gelen olaylar: bitince tekrar bak.
            _deviceChangeTimer.Start();
            return;
        }

        bool force = _forceReconnect;
        _forceReconnect = false;

        List<string> paths;
        try
        {
            paths = await Task.Run(() => HidWin.ListControlPaths());
        }
        catch (Exception ex)
        {
            Log("DEVICE SCAN ERROR -> " + ex.Message);
            if (force && _resumeRetries < 3)
            {
                _resumeRetries++;
                ScheduleDeviceScan(forceReconnect: true, delayMs: 2000);
            }

            return;
        }

        var best = paths.FirstOrDefault();

        if (_device is not null)
        {
            bool present = paths.Any(p => string.Equals(p, _device.Path, StringComparison.OrdinalIgnoreCase));
            bool betterWired = best is not null
                && HidWin.Describe(HidWin.PidFromPath(best)).Mode == HidWin.LinkMode.Wired
                && _device.Info.Mode != HidWin.LinkMode.Wired;

            if (!force && present && !betterWired)
            {
                Log($"DEVICE SCAN -> {paths.Count} Razer cihazi, {_device.Info.Title} yerinde; degisiklik yok");
                return;
            }

            Log("========================================");
            if (force)
            {
                Log($"DEVICE CHANGE -> uyanma sonrasi yeniden baglaniyor ({_device.Info.Title})");
            }
            else
            {
                Log(present
                    ? $"DEVICE CHANGE -> kablo takildi, {_device.Info.Title} yerine kablolu fareye geciliyor"
                    : $"DEVICE CHANGE -> {_device.Info.Title} ayrildi");
            }

            _batteryTimer.Stop();
            _device.Dispose();
            _device = null;
        }

        if (best is null)
        {
            EnterWaitingState();
            Log(force
                ? "DEVICE CHANGE -> uyanma: henuz Razer cihazi yok, bekleniyor"
                : "DEVICE CHANGE -> uygun Razer cihazi yok, bekleniyor");
            if (force && _resumeRetries < 3)
            {
                _resumeRetries++;
                ScheduleDeviceScan(forceReconnect: true, delayMs: 3000);
            }

            return;
        }

        _resumeRetries = 0;
        Log($"DEVICE CHANGE -> baglaniyor: {HidWin.Describe(HidWin.PidFromPath(best)).Title}");
        await ConnectAsync();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting && _settings.MinimizeToTray && _tray is not null)
        {
            // X'e basildi: kapatma, tepsiye kucult.
            e.Cancel = true;
            HideToTray();
            return;
        }

        Log("GUI kapatiliyor...");
        _batteryTimer.Stop();
        _deviceChangeTimer.Stop();
        _hwndSource?.RemoveHook(DeviceChangeHook);
        _hotkeys?.Dispose();
        _hotkeys = null;
        _tray?.Dispose();
        _tray = null;
        _device?.Dispose();
        _device = null;
        ToastWindow.CloseCurrent();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _settings.MinimizeToTray && _tray is not null && IsVisible)
        {
            HideToTray();
        }
    }

    // ------------------------------------------------------------------
    // Tepsi
    // ------------------------------------------------------------------

    private void InitTrayAndHotkeys()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/razerlite.ico"));
            using var stream = res!.Stream;
            var icon = new System.Drawing.Icon(stream);

            _tray = new TrayIcon(icon, AppTitle);
            _tray.ShowRequested += ShowFromTray;
            _tray.ExitRequested += ExitFromTray;
            _tray.ProfileRequested += slot => _ = ApplyProfileAsync(slot, "tepsi");
            _tray.AutostartToggled += on => SetAutostart(on);
            _tray.MinimizeToTrayToggled += on => SetMinimizeToTray(on);
        }
        catch (Exception ex)
        {
            Log("TRAY ERROR -> " + ex.Message);
        }

        try
        {
            _hotkeys = new HotKeys(this, slot => _ = ApplyProfileAsync(slot, HotKeys.Label(slot)));
        }
        catch (Exception ex)
        {
            Log("HOTKEY ERROR -> " + ex.Message);
        }

        try
        {
            InitDeviceWatcher();
        }
        catch (Exception ex)
        {
            Log("DEVICE WATCHER ERROR -> " + ex.Message);
        }
    }

    private void HideToTray()
    {
        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.Balloon(AppTitle, "Tepside çalışmaya devam ediyor. Çift tık: göster · Sağ tık: profiller / çıkış.");
        }
    }

    public void ShowFromTray()
    {
        // Once Normal: Minimized durumda Show() cagrilirsa StateChanged tetiklenir ve pencere tekrar gizlenir.
        WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void ExitFromTray()
    {
        _exiting = true;
        Close();
    }

    private void UpdateTrayToolTip()
    {
        if (_tray is null)
        {
            return;
        }

        var parts = new List<string> { AppTitle };
        if (_device is not null)
        {
            parts.Add(_device.Info.Title);
        }
        else
        {
            parts.Add("cihaz bekleniyor");
        }

        if (_device is not null && _lastDpi > 0)
        {
            parts.Add($"{_lastDpi} DPI");
        }

        if (_device is not null && _lastHz > 0)
        {
            parts.Add($"{_lastHz} Hz");
        }

        if (_device is not null && _lastBattery?.Percent is int pct)
        {
            parts.Add($"Pil %{pct}" + (_lastBattery.Charging ? " (şarj)" : ""));
        }

        _tray.SetToolTip(string.Join(" · ", parts));
    }

    // ------------------------------------------------------------------
    // Ayarlar (Windows ile baslat / tepsiye kucult)
    // ------------------------------------------------------------------

    private void SyncSettingsUi()
    {
        _syncingUi = true;
        try
        {
            AutostartCheck.IsChecked = _settings.StartWithWindows;
            TrayCheck.IsChecked = _settings.MinimizeToTray;
            LowBatteryCheck.IsChecked = _settings.LowBatteryWarn;
            AutoUpdateCheck.IsChecked = _settings.AutoCheckUpdates;
            DebugLogSwitch.IsChecked = _settings.ShowDebugLog;
            ApplyDebugLogVisibility(_settings.ShowDebugLog);
            _tray?.SetChecks(_settings.StartWithWindows, _settings.MinimizeToTray);
        }
        finally
        {
            _syncingUi = false;
        }
    }

    private void SetAutostart(bool on)
    {
        if (_syncingUi)
        {
            return;
        }

        try
        {
            Autostart.Set(on);
            _settings.StartWithWindows = on;
            _settings.Save();
            Log($"AUTOSTART -> {(on ? "ACIK" : "KAPALI")}");
        }
        catch (Exception ex)
        {
            Log("AUTOSTART ERROR -> " + ex.Message);
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            _settings.StartWithWindows = Autostart.IsEnabled();
        }

        SyncSettingsUi();
    }

    private void SetMinimizeToTray(bool on)
    {
        if (_syncingUi)
        {
            return;
        }

        _settings.MinimizeToTray = on;
        _settings.Save();
        Log($"TEPSIYE KUCULT -> {(on ? "ACIK" : "KAPALI")}");
        SyncSettingsUi();
    }

    private void AutostartCheck_Changed(object sender, RoutedEventArgs e)
    {
        SetAutostart(AutostartCheck.IsChecked == true);
    }

    private void TrayCheck_Changed(object sender, RoutedEventArgs e)
    {
        SetMinimizeToTray(TrayCheck.IsChecked == true);
    }

    private void LowBatteryCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingUi)
        {
            return;
        }

        _settings.LowBatteryWarn = LowBatteryCheck.IsChecked == true;
        _settings.Save();
        if (!_settings.LowBatteryWarn)
        {
            _lowBatteryWarnedAt = null;
        }

        Log($"DUSUK PIL UYARISI -> {(_settings.LowBatteryWarn ? "ACIK" : "KAPALI")} (esik %{_settings.LowBatteryPercent})");
    }

    private void DebugLogSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingUi)
        {
            return;
        }

        bool on = DebugLogSwitch.IsChecked == true;
        _settings.ShowDebugLog = on;
        _settings.Save();
        ApplyDebugLogVisibility(on);
        Log($"DEBUG LOG -> {(on ? "ACIK" : "KAPALI")}");
    }

    /// <summary>Ayarlar sayfasindaki log karti: acikken kalan yuksekligi doldurur.</summary>
    private void ApplyDebugLogVisibility(bool show)
    {
        DebugLogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DebugSwitchLabel.Text = show ? "Açık" : "Kapalı";
        DebugGapRow.Height = show ? new GridLength(18) : new GridLength(0);
        DebugBodyRow.Height = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    private int? _sleepMinutes;
    private int _sleepWriteGeneration;
    private int? _lowPowerPercent;
    private int _lowPowerWriteGeneration;
    private string? _mouseFirmware;
    private string? _dongleFirmware;
    private bool _firmwareBusy;
    private int _firmwareGeneration;
    private readonly DispatcherTimer _firmwareStatusTimer;
    private FirmwareNext _firmwareNext = FirmwareNext.Check;
    private string? _firmwareFileUrl;
    private string? _firmwarePageUrl;
    private string? _firmwareFileName;

    private enum FirmwareNext
    {
        Check,
        Download,
        OpenPage,
    }
    private int _surfaceMm = 2;
    private bool _surfaceAsymmetric;
    private int _surfaceLift = 13;
    private int _surfaceLand = 12;
    private int _surfaceWriteGeneration;

    /// <summary>
    /// Pencere boyutu sabittir (sayfalar arasinda ziplamaz); sadece kucuk ekranda calisma alanina sigdirilir.
    /// </summary>
    private void FitWindowToContent()
    {
        double available = SystemParameters.WorkArea.Height - 24;
        if (MinHeight > available)
        {
            MinHeight = available;
        }

        if (Height > available)
        {
            Height = available;
        }
    }

    /// <summary>
    /// Sayfa icerigi buyudugunde (uyari satiri, asimetrik kaydiricilar, sayfa degisimi) pencere calisma alanina
    /// kadar uzar. Yalnizca icerik buyuyunce tepki verir; kullanici pencereyi elle kucultuyorsa onunla
    /// cekismez, o durumda kaydirma cubugu cikar.
    /// </summary>
    private void PageScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0 && PageScroller.ScrollableHeight > 0.5 && WindowState == WindowState.Normal)
        {
            GrowToFitPage();
        }
    }

    private void GrowToFitPage()
    {
        var work = SystemParameters.WorkArea;
        double target = Math.Min(Math.Ceiling(ActualHeight + PageScroller.ScrollableHeight), work.Height - 24);
        if (target <= ActualHeight + 0.5)
        {
            return;
        }

        Height = target;
        if (Top + Height > work.Bottom)
        {
            Top = Math.Max(work.Top, work.Bottom - Height);
        }
    }

    private FrameworkElement[] DeviceCards => [SleepPanel, LowPowerPanel, SurfacePanel, DongleLedPanel];

    /// <summary>
    /// Sag sutundaki kartlardan gorunen son kart kalan yuksekligi doldurur; boylece hangi kartlar
    /// gizli olursa olsun iki sutunun alt kenari ayni cizgide biter.
    /// </summary>
    private void UpdateDeviceCardFill()
    {
        var cards = DeviceCards;
        int last = Array.FindLastIndex(cards, c => c.Visibility == Visibility.Visible);
        for (int i = 0; i < cards.Length; i++)
        {
            bool fill = i == last;
            DeviceCardColumn.RowDefinitions[i].Height = fill ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            cards[i].VerticalAlignment = fill ? VerticalAlignment.Stretch : VerticalAlignment.Top;
            cards[i].Margin = fill ? new Thickness(0) : new Thickness(0, 0, 0, 16);
        }
    }

    /// <summary>Cihaz durumu kartindaki ikon: kabloluda fis, digerlerinde kablosuz sinyal.</summary>
    private void SetLinkIcon(bool wired)
    {
        LinkIconWired.Visibility = wired ? Visibility.Visible : Visibility.Collapsed;
        LinkIconWireless.Visibility = wired ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------
    // Tek parca ust cubuk
    // ------------------------------------------------------------------

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Windows 11: ozel ust cubuklu pencerede de yuvarlak kose (eski surumlerde etkisiz).</summary>
    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
        FitWindowToContent();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Normal kapatma: Window_Closing tepsiye kucultme ayarini uygular.</summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------
    // Tanilama raporu (GitHub issue'ya yapistirmak icin)
    // ------------------------------------------------------------------

    private string BuildDiagnosticReport()
    {
        static string Shown(UIElement e) => e.Visibility == Visibility.Visible ? "var" : "yok";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("### RazerLite tanılama raporu");
        sb.AppendLine("```");
        sb.AppendLine($"Sürüm      : {AppTitle}");
        sb.AppendLine($"Windows    : {Environment.OSVersion.VersionString} ({(Environment.Is64BitOperatingSystem ? "64" : "32")}-bit)");
        sb.AppendLine($"Zaman      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        if (_device is null)
        {
            sb.AppendLine("Cihaz      : bağlı değil");
        }
        else
        {
            var known = HidWin.KnownDevices.ContainsKey(_device.Pid) ? "listede" : "listede değil";
            sb.AppendLine($"Cihaz      : {_device.Info.Title} (PID {_device.Pid:X4}, {_device.Info.ModeText}, {known})");
            sb.AppendLine($"Yol        : {_device.Path}");
            sb.AppendLine($"Arayüz     : {HidWin.InterfaceOf(_device.Path)}");
            sb.AppendLine($"Tx ID      : 0x{_device.TransactionId:X2}");
            sb.AppendLine($"Firmware   : {_probe?.Firmware ?? "okunamadı"}");
            sb.AppendLine($"Polling    : {(_device.ExtendedPolling switch { true => "genişletilmiş (0x00/0xC0)", false => "klasik (0x00/0x85)", _ => "bilinmiyor" })}");
            sb.AppendLine($"Sürücü modu: {(_device.DriverModeApplied ? "uygulandı" : "uygulanmadı")}");
        }

        sb.AppendLine($"Durum      : {StatusText.Text} | {CurrentText.Text}");
        sb.AppendLine($"Pil        : {BatteryText.Text} {LinkText.Text}".TrimEnd());
        sb.AppendLine($"Özellikler : uyku {Shown(SleepPanel)}, düşük güç {Shown(LowPowerPanel)}, yüzey {Shown(SurfacePanel)}, dongle LED {Shown(DongleLedPanel)}");

        sb.AppendLine();
        sb.AppendLine("Tarama (salt-okuma, firmware 0x00/0x81):");
        if (_probe is null || _probe.Attempts.Count == 0)
        {
            sb.AppendLine("  yapılmadı");
        }
        else
        {
            foreach (var a in _probe.Attempts)
            {
                sb.AppendLine($"  {a.Interface,-6} tx 0x{a.TransactionId:X2} -> {a.Result}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Razer HID yolları (VID 1532):");
        try
        {
            foreach (var path in HidWin.ListRazerPaths())
            {
                sb.AppendLine("  " + path);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  listelenemedi: " + ex.Message);
        }

        sb.AppendLine();
        sb.AppendLine("Son log satırları:");
        var lines = DebugBox.Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(Math.Max(0, lines.Length - 60)))
        {
            sb.AppendLine("  " + line);
        }

        sb.AppendLine("```");
        return sb.ToString();
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var report = BuildDiagnosticReport();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(report);
                StatusText.Text = "Tanılama raporu panoya kopyalandı";
                Log("DIAGNOSTICS -> panoya kopyalandi");
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(80); // pano baska bir uygulamada kilitli olabilir
            }
        }

        StatusText.Text = "Pano kullanılamadı; tekrar dene";
        Log("DIAGNOSTICS ERROR -> pano kilitli");
    }

    // ------------------------------------------------------------------
    // Program guncellemesi (GitHub Releases, bkz. Updater)
    // ------------------------------------------------------------------

    private Updater.Release? _availableUpdate;
    private bool _updateBusy;

    /// <summary>Acilista: ayar aciksa ve son denetimden 20 saat gectiyse sessizce denetle.</summary>
    private void MaybeAutoCheckForUpdate()
    {
        var last = _settings.LastUpdateCheckUtc;
        if (_settings.AutoCheckUpdates && (last is null || DateTime.UtcNow - last.Value > TimeSpan.FromHours(20)))
        {
            _ = CheckForUpdateAsync(manual: false);
        }
    }

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_updateBusy)
        {
            return;
        }

        _updateBusy = true;
        UpdateCheckButton.IsEnabled = false;
        if (manual)
        {
            UpdateStatusText.Text = "Denetleniyor…";
        }

        try
        {
            var release = await Updater.GetLatestAsync();
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            _settings.Save();

            if (release is null)
            {
                Log("UPDATE -> yayinlanmis surum yok");
                ShowUpdate(null, "Henüz yayınlanmış bir sürüm yok.");
                return;
            }

            Log($"UPDATE -> son surum {release.Tag}, bu surum {AppInfo.VersionText}");
            if (!Updater.IsNewer(release.Version))
            {
                ShowUpdate(null, $"Güncel: {AppInfo.VersionText} (son sürüm {release.Tag})");
                return;
            }

            ShowUpdate(release, release.AssetUrl is null
                ? $"Yeni sürüm {release.Tag} var, ama release'e exe eklenmemiş."
                : $"Yeni sürüm mevcut: {release.Tag} (bu sürüm {AppInfo.VersionText})");
            if (!manual)
            {
                _tray?.Balloon(AppTitle, $"Yeni sürüm {release.Tag} mevcut. Hakkında sayfasından güncelleyebilirsin.");
            }
        }
        catch (Exception ex)
        {
            Log("UPDATE ERROR -> " + ex.Message);
            if (manual)
            {
                UpdateStatusText.Text = "Denetlenemedi: " + ex.Message;
            }
        }
        finally
        {
            _updateBusy = false;
            UpdateCheckButton.IsEnabled = true;
        }
    }

    private void ShowUpdate(Updater.Release? release, string status)
    {
        _availableUpdate = release;
        UpdateStatusText.Text = status;
        var shown = release is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateDot.Visibility = shown;
        UpdateActions.Visibility = shown;
        UpdateInstallButton.IsEnabled = release?.AssetUrl is not null;

        var notes = release?.Notes.Trim() ?? "";
        UpdateNotesText.Text = notes.Length > 600 ? notes[..600] + "…" : notes;
        UpdateNotesText.Visibility = notes.Length > 0 ? shown : Visibility.Collapsed;
    }

    private async void UpdateCheckButton_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdateAsync(manual: true);
    }

    private async void UpdateInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not { } release || _updateBusy)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"RazerLite {release.Tag} indirilip kurulacak ve program yeniden açılacak.\n\nDevam edilsin mi?",
            AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _updateBusy = true;
        UpdateInstallButton.IsEnabled = false;
        UpdateCheckButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(p => UpdateInstallText.Text = $"İndiriliyor… %{p}");
            var path = await Updater.DownloadAsync(release, progress);
            Log($"UPDATE -> {release.Tag} indirildi ve dogrulandi, yeniden baslatiliyor");
            UpdateInstallText.Text = "Yeniden başlatılıyor…";
            Updater.ReplaceAndRestart(path);
            _exiting = true;
            Close();
        }
        catch (Exception ex)
        {
            Log("UPDATE ERROR -> " + ex.Message);
            UpdateStatusText.Text = "Güncelleme yapılamadı: " + ex.Message;
            UpdateInstallText.Text = "İndir ve yeniden başlat";
            UpdateInstallButton.IsEnabled = true;
            UpdateCheckButton.IsEnabled = true;
            _updateBusy = false;
        }
    }

    private void UpdatePageButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = _availableUpdate?.PageUrl ?? AppInfo.RepoUrl + "/releases";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("LINK ERROR -> " + ex.Message);
        }
    }

    private void AutoUpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingUi)
        {
            return;
        }

        _settings.AutoCheckUpdates = AutoUpdateCheck.IsChecked == true;
        _settings.Save();
        Log($"OTOMATIK GUNCELLEME DENETIMI -> {(_settings.AutoCheckUpdates ? "ACIK" : "KAPALI")}");
        MaybeAutoCheckForUpdate();
    }

    private void ProjectLinkButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppInfo.RepoUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("LINK ERROR -> " + ex.Message);
        }
    }

    private void StagesExpander_Expanded(object sender, RoutedEventArgs e) => FitWindowToContent();

    private void StagesExpander_Collapsed(object sender, RoutedEventArgs e) => FitWindowToContent();

    // ------------------------------------------------------------------
    // Profiller
    // ------------------------------------------------------------------

    private void RefreshProfileUi(int? selectIndex = null)
    {
        _syncingUi = true;
        try
        {
            ProfileBox.Items.Clear();
            for (int i = 0; i < _settings.Profiles.Count; i++)
            {
                var p = _settings.Profiles[i];
                ProfileBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{i + 1}.  {p.Name}  —  {p.Dpi} DPI / {p.RatesText}",
                    Tag = i,
                    ToolTip = HotKeys.Label(i + 1),
                });
            }

            if (selectIndex is int idx && idx >= 0 && idx < ProfileBox.Items.Count)
            {
                ProfileBox.SelectedIndex = idx;
            }
            else
            {
                ProfileBox.SelectedIndex = -1;
            }

            DeleteProfileButton.IsEnabled = !_busy && ProfileBox.SelectedIndex >= 0;

            _tray?.SetProfiles(_settings.Profiles);
            if (_hotkeys is not null)
            {
                var failed = _hotkeys.RegisterAll(_settings.Profiles.Count);
                if (failed.Count > 0)
                {
                    Log("HOTKEY -> kayit edilemedi: " + string.Join(", ", failed.Select(HotKeys.Label)));
                }
            }
        }
        finally
        {
            _syncingUi = false;
        }
    }

    private void ProfileBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingUi)
        {
            return;
        }

        DeleteProfileButton.IsEnabled = !_busy && ProfileBox.SelectedIndex >= 0;

        if (ProfileBox.SelectedItem is ComboBoxItem { Tag: int idx } && idx < _settings.Profiles.Count)
        {
            var p = _settings.Profiles[idx];
            DpiBox.Text = p.Dpi.ToString();
            SelectHz(p.RateFor(WiredLink));
            Log($"PROFIL SECILDI -> {p.Name} ({p.Dpi} DPI / {p.RatesText}). Uygulamak icin UYGULA veya {HotKeys.Label(idx + 1)}.");
        }
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DpiBox.Text.Trim(), out var dpi) || dpi is < 100 or > 45000)
        {
            MessageBox.Show("DPI 100 ile 45000 arasinda olmalidir.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var hz = SelectedHz();
        bool wired = WiredLink;
        int activeHz = ClampHz(hz, out _);

        // Kural: girilen ad mevcut bir profille aynıysa o profil güncellenir, yeni ad yeni profil olur.
        // Secili profil varsa adi varsayilan olarak gelir (Enter = guncelle); farkli ad yaz = yeni profil.
        int? selectedIdx = ProfileBox.SelectedItem is ComboBoxItem { Tag: int idx } && idx < _settings.Profiles.Count ? idx : null;
        bool full = _settings.Profiles.Count >= Settings.MaxProfiles;

        var initial = selectedIdx is int si
            ? _settings.Profiles[si].Name
            : full ? "" : $"Profil {_settings.Profiles.Count + 1}";

        string linkText = wired ? $"kablo {activeHz} Hz" : $"kablosuz {activeHz} Hz";
        var hint = full
            ? $"Profil adı ({dpi} DPI / {linkText}) — {Settings.MaxProfiles} profil dolu, mevcut bir adı yazarsan güncellenir:"
            : $"Profil adı ({dpi} DPI / {linkText}) — yeni ad = yeni profil, mevcut ad = güncelle:";

        var name = PromptWindow.Ask(this, AppTitle, hint, initial);
        if (name is null)
        {
            return;
        }

        int existing = _settings.Profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        int selected;
        if (existing >= 0)
        {
            var kept = _settings.Profiles[existing];
            var updated = new Profile
            {
                Name = name,
                Dpi = dpi,
                Hz = wired ? activeHz : kept.Hz,
                HzWireless = wired ? kept.HzWireless : activeHz,
            };
            _settings.Profiles[existing] = updated;
            selected = existing;
            Log($"PROFIL GUNCELLENDI -> {selected + 1}. {name}: {dpi} DPI / {updated.RatesText}");
        }
        else
        {
            if (full)
            {
                MessageBox.Show(
                    $"En fazla {Settings.MaxProfiles} profil kaydedilebilir. Once bir profili silin veya mevcut bir profilin adini yazarak guncelleyin.",
                    AppTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var created = new Profile
            {
                Name = name,
                Dpi = dpi,
                Hz = wired ? activeHz : activeHz <= 1000 ? activeHz : 1000,
                HzWireless = wired ? 1000 : activeHz,
            };
            _settings.Profiles.Add(created);
            selected = _settings.Profiles.Count - 1;
            Log($"PROFIL KAYDEDILDI -> {selected + 1}. {name}: {dpi} DPI / {created.RatesText} ({HotKeys.Label(selected + 1)})");
        }

        _settings.Save();
        RefreshProfileUi(selected);
    }

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not ComboBoxItem { Tag: int idx } || idx >= _settings.Profiles.Count)
        {
            return;
        }

        var p = _settings.Profiles[idx];
        var answer = MessageBox.Show(
            $"\"{p.Name}\" profili silinsin mi?",
            AppTitle, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _settings.Profiles.RemoveAt(idx);
        _settings.Save();
        Log($"PROFIL SILINDI -> {p.Name}");
        RefreshProfileUi();
    }

    private async Task ApplyProfileAsync(int slot, string source)
    {
        if (slot < 1 || slot > _settings.Profiles.Count)
        {
            return;
        }

        if (_device is null || _busy)
        {
            Log($"PROFIL {slot} ({source}) -> mesgul, atlandi");
            return;
        }

        var p = _settings.Profiles[slot - 1];
        int profileHz = p.RateFor(WiredLink);
        Log($"PROFIL {slot} ({source}) -> {p.Name}: {p.Dpi} DPI / {p.RatesText}");

        DpiBox.Text = p.Dpi.ToString();
        SelectHz(profileHz);
        RefreshProfileUi(slot - 1);

        await ApplyAsync();

        if (_lastDpi == p.Dpi && _lastHz == profileHz)
        {
            _tray?.Balloon(AppTitle, $"{p.Name} uygulandı: {p.Dpi} DPI · {profileHz} Hz");
        }
    }

    // ------------------------------------------------------------------
    // Pil / baglanti
    // ------------------------------------------------------------------

    /// <param name="background">Zamanlayici okumasi: kisa butce, BUSY'de son deger korunur, log tekrarlanmaz.</param>
    private async Task<bool> ReadBatteryAsync(bool background = false)
    {
        if (_device is null)
        {
            return false;
        }

        try
        {
            var b = await Protocol.GetBatteryAsync(_device, background ? 800 : Protocol.DefaultBudgetMs);
            if (b.Percent is int pct)
            {
                _lastBattery = b;
                _batteryBusyLogged = false;
                BatteryText.Text = $"Pil %{pct}" + (b.Charging ? "  ⚡" : "");
                BatteryText.Foreground = pct >= 50 ? GreenBrush : pct >= _settings.LowBatteryPercent ? AmberBrush : RedBrush;
                SetBatteryRing(pct);
                LinkText.Text = b.Charging ? "Şarj oluyor" : "Bağlı";
                Log($"BATTERY -> %{pct}{(b.Charging ? " (sarj)" : "")}");
                UpdateTrayToolTip();
                MaybeWarnLowBattery(pct, b.Charging);
                return true;
            }

            if (background && _lastBattery?.Percent is int keep)
            {
                // Fare o an hareket ediyor / uykuda: bilinen son degeri koru, bir kez logla.
                if (!_batteryBusyLogged)
                {
                    _batteryBusyLogged = true;
                    Log($"BATTERY -> {Protocol.StatusText(b.Status)}; fare mesgul, son deger (%{keep}) korunuyor");
                }

                return false;
            }

            _lastBattery = b;
            BatteryText.Text = "Pil —";
            BatteryText.Foreground = DimBrush;
            SetBatteryRing(null);
            LinkText.Text = "Yanıt vermiyor";
            Log($"BATTERY -> {Transient(b.Status)}");
        }
        catch (Exception ex)
        {
            BatteryText.Text = "Pil —";
            BatteryText.Foreground = DimBrush;
            SetBatteryRing(null);
            LinkText.Text = "Okunamadı";
            Log("BATTERY ERROR -> " + ex.Message);
        }

        UpdateTrayToolTip();
        return false;
    }

    /// <summary>
    /// Baslik karti pil halkasi: yay 12 yonunden saat yonunde yuzde kadar dolar; null = bos halka, "—".
    /// Rengi XAML'da BatteryText.Foreground'a bagli.
    /// </summary>
    private void SetBatteryRing(int? percent)
    {
        const double size = 42, stroke = 4;
        double r = (size - stroke) / 2, c = size / 2;
        BatteryRingText.Text = percent is int p ? p.ToString() : "—";

        if (percent is not int pct || pct <= 0)
        {
            BatteryRingArc.Data = Geometry.Empty;
            return;
        }

        if (pct >= 100)
        {
            BatteryRingArc.Data = new EllipseGeometry(new Point(c, c), r, r);
            return;
        }

        double angle = pct / 100.0 * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(c, c - r) };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, pct > 50, SweepDirection.Clockwise, true));
        BatteryRingArc.Data = new PathGeometry(new[] { figure });
    }

    /// <summary>
    /// Esik alti + sarj degil: esikte uyar, sonra her %5 dususte tekrar uyar (or. 20, 15, 10, 5).
    /// Windows bildirimleri kapali olabildigi icin kendi penceremiz kullanilir; tam ekran
    /// oyun/sunumda bir sonraki pil okumasina ertelenir. Sarj baslayinca veya esigin ustune
    /// cikinca sifirlanir.
    /// </summary>
    private void MaybeWarnLowBattery(int percent, bool charging)
    {
        int threshold = _settings.LowBatteryPercent;
        if (charging || percent > threshold)
        {
            _lowBatteryWarnedAt = null;
            return;
        }

        if (!_settings.LowBatteryWarn || (_lowBatteryWarnedAt is int last && percent > last - 5))
        {
            return;
        }

        if (!ToastWindow.CanShowNow())
        {
            if (!_lowBatteryDeferLogged)
            {
                _lowBatteryDeferLogged = true;
                Log($"BATTERY WARN -> %{percent}; tam ekran/kilit ekrani, uyari ertelendi");
            }

            return;
        }

        _lowBatteryDeferLogged = false;
        _lowBatteryWarnedAt = percent;
        Log($"BATTERY WARN -> %{percent} (esik %{threshold})");
        StatusText.Text = $"Düşük pil: %{percent}";
        BatteryText.Foreground = RedBrush;
        ToastWindow.Show(
            $"{AppTitle} · Düşük pil",
            $"Fare pili %{percent}. Şarja takmanı öneririm.");
    }

    private async Task ReadTimeToSleepAsync()
    {
        if (_device is null)
        {
            SleepPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var sleep = await Protocol.GetTimeToSleepAsync(_device);
        if (sleep.Seconds is not int seconds)
        {
            Log($"SLEEP -> {Protocol.StatusText(sleep.Status)}");
            SleepPanel.Visibility = Visibility.Collapsed;
            return;
        }

        int minutes = (int)Math.Round(seconds / 60.0);
        minutes = Math.Clamp(minutes, Protocol.SleepMinMinutes, Protocol.SleepMaxMinutes);
        Log(seconds == minutes * 60
            ? $"SLEEP -> {minutes} dk ({seconds} sn)"
            : $"SLEEP -> {seconds} sn (kaydirici {minutes} dk)");
        ShowSleep(minutes);
    }

    private void ShowSleep(int minutes)
    {
        bool wasHidden = SleepPanel.Visibility != Visibility.Visible;
        SleepPanel.Visibility = Visibility.Visible;
        _sleepMinutes = minutes;
        _syncingUi = true;
        try
        {
            SleepSlider.Value = minutes;
            SleepValueText.Text = $"{minutes} dk";
        }
        finally
        {
            _syncingUi = false;
        }

        if (wasHidden)
        {
            FitWindowToContent();
        }
    }

    private async void SleepSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int minutes = (int)Math.Round(SleepSlider.Value);
        if (SleepValueText is not null)
        {
            SleepValueText.Text = $"{minutes} dk";
        }

        if (_syncingUi || _device is null || _sleepMinutes == minutes)
        {
            return;
        }

        int generation = ++_sleepWriteGeneration;
        try
        {
            var started = Environment.TickCount64;
            while (generation == _sleepWriteGeneration)
            {
                await Task.Delay(250);
                if (generation != _sleepWriteGeneration || _syncingUi || _device is null)
                {
                    return;
                }

                if (_busy)
                {
                    if (Environment.TickCount64 - started > 4000)
                    {
                        return;
                    }

                    continue;
                }

                minutes = (int)Math.Round(SleepSlider.Value);
                if (_sleepMinutes == minutes)
                {
                    return;
                }

                await WriteSleepAsync(minutes);
                return;
            }
        }
        catch (Exception ex)
        {
            Log("SLEEP ERROR -> " + ex.Message);
            StatusText.Text = "Boşta uyku yazılamadı";
        }
    }

    private async Task WriteSleepAsync(int minutes)
    {
        if (_device is null)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await Protocol.EnsureDriverModeAsync(_device, Log);
            var status = await Protocol.SetTimeToSleepAsync(_device, minutes);
            Log($"SLEEP SET -> {minutes} dk {Protocol.StatusText(status)}");
            if (!Protocol.IsSuccess(status))
            {
                StatusText.Text = "Boşta uyku yazılamadı";
                await ReadTimeToSleepAsync();
                return;
            }

            var read = await Protocol.GetTimeToSleepAsync(_device);
            if (read.Seconds is int seconds)
            {
                int confirmed = Math.Clamp((int)Math.Round(seconds / 60.0), Protocol.SleepMinMinutes, Protocol.SleepMaxMinutes);
                Log($"SLEEP READBACK -> {seconds} sn");
                ShowSleep(confirmed);
            }
            else
            {
                _sleepMinutes = minutes;
            }

            StatusText.Text = "Boşta uyku uygulandı";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReadLowPowerAsync()
    {
        if (_device is null)
        {
            LowPowerPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var power = await Protocol.GetLowPowerAsync(_device);
        if (power.Percent is not int percent)
        {
            Log($"LOW POWER -> {Protocol.StatusText(power.Status)}");
            LowPowerPanel.Visibility = Visibility.Collapsed;
            return;
        }

        int shown = SnapLowPower(percent);
        Log(shown == percent
            ? $"LOW POWER -> %{percent} (bayt {power.Raw})"
            : $"LOW POWER -> %{percent} (bayt {power.Raw}, kaydirici %{shown})");
        ShowLowPower(shown);
    }

    private static int SnapLowPower(int percent) =>
        Math.Clamp(
            (int)Math.Round(percent / (double)Protocol.LowPowerStep) * Protocol.LowPowerStep,
            Protocol.LowPowerMin,
            Protocol.LowPowerMax);

    private void ShowLowPower(int percent)
    {
        bool wasHidden = LowPowerPanel.Visibility != Visibility.Visible;
        LowPowerPanel.Visibility = Visibility.Visible;
        _lowPowerPercent = percent;
        _syncingUi = true;
        try
        {
            LowPowerSlider.Value = percent;
            LowPowerValueText.Text = $"%{percent}";
        }
        finally
        {
            _syncingUi = false;
        }

        UpdateLowPowerGate();
        if (wasHidden)
        {
            FitWindowToContent();
        }
    }

    private void UpdateLowPowerGate()
    {
        if (LowPowerPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        bool enabled = _lastHz <= 1000;
        LowPowerSlider.IsEnabled = enabled;
        LowPowerWarning.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void LowPowerSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int percent = SnapLowPower((int)Math.Round(LowPowerSlider.Value));
        if (LowPowerValueText is not null)
        {
            LowPowerValueText.Text = $"%{percent}";
        }

        if (_syncingUi || _device is null || !LowPowerSlider.IsEnabled || _lowPowerPercent == percent)
        {
            return;
        }

        int generation = ++_lowPowerWriteGeneration;
        try
        {
            var started = Environment.TickCount64;
            while (generation == _lowPowerWriteGeneration)
            {
                await Task.Delay(250);
                if (generation != _lowPowerWriteGeneration || _syncingUi || _device is null || !LowPowerSlider.IsEnabled)
                {
                    return;
                }

                if (_busy)
                {
                    if (Environment.TickCount64 - started > 4000)
                    {
                        return;
                    }

                    continue;
                }

                percent = SnapLowPower((int)Math.Round(LowPowerSlider.Value));
                if (_lowPowerPercent == percent)
                {
                    return;
                }

                await WriteLowPowerAsync(percent);
                return;
            }
        }
        catch (Exception ex)
        {
            Log("LOW POWER ERROR -> " + ex.Message);
            StatusText.Text = "Düşük güç eşiği yazılamadı";
        }
    }

    private async Task WriteLowPowerAsync(int percent)
    {
        if (_device is null)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await Protocol.EnsureDriverModeAsync(_device, Log);
            var status = await Protocol.SetLowPowerAsync(_device, percent);
            Log($"LOW POWER SET -> %{percent} {Protocol.StatusText(status)}");
            if (!Protocol.IsSuccess(status))
            {
                StatusText.Text = "Düşük güç eşiği yazılamadı";
                await ReadLowPowerAsync();
                return;
            }

            var read = await Protocol.GetLowPowerAsync(_device);
            if (read.Percent is int confirmed)
            {
                Log($"LOW POWER READBACK -> %{confirmed} (bayt {read.Raw})");
                ShowLowPower(SnapLowPower(confirmed));
            }
            else
            {
                _lowPowerPercent = percent;
            }

            StatusText.Text = "Düşük güç eşiği uygulandı";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ReadFirmwareAsync()
    {
        var device = _device;
        if (device is null)
        {
            return;
        }

        int generation = _firmwareGeneration;
        var mouse = await Protocol.GetExtendedFirmwareAsync(device);
        if (generation != _firmwareGeneration)
        {
            return;
        }

        var dock = await Protocol.GetDockFirmwareAsync(device);
        if (generation != _firmwareGeneration)
        {
            return;
        }

        _mouseFirmware = mouse.Version is Protocol.FirmwareVersion mouseVersion ? mouseVersion.Text : null;
        _dongleFirmware = dock.Version is Protocol.FirmwareVersion dockVersion ? dockVersion.Text : null;
        Log(_mouseFirmware is null
            ? $"FIRMWARE -> fare {Protocol.StatusText(mouse.Status)}"
            : $"FIRMWARE -> fare {_mouseFirmware}");
        Log(_dongleFirmware is null
            ? $"FIRMWARE -> dongle {Protocol.StatusText(dock.Status)}"
            : $"FIRMWARE -> dongle {_dongleFirmware}");

        FirmwareMouseText.Text = _mouseFirmware ?? "—";
        FirmwareDongleText.Text = _dongleFirmware ?? "—";
        FirmwareButton.IsEnabled = !_firmwareBusy && (_mouseFirmware is not null || _dongleFirmware is not null);
    }

    private async void FirmwareButton_Click(object sender, RoutedEventArgs e)
    {
        if (_firmwareBusy)
        {
            return;
        }

        if (_firmwareNext == FirmwareNext.Download)
        {
            await DownloadFirmwareAsync();
            return;
        }

        if (_firmwareNext == FirmwareNext.OpenPage)
        {
            OpenFirmwarePage();
            return;
        }

        if (_mouseFirmware is null && _dongleFirmware is null)
        {
            return;
        }

        int generation = _firmwareGeneration;
        var mouse = _mouseFirmware;
        var dock = _dongleFirmware;
        var device = _device;
        _firmwareBusy = true;
        FirmwareButton.IsEnabled = false;
        ShowFirmwareStatus("Güncelleme listesi okunuyor…", autoHide: false);
        try
        {
            var offer = await FindFirmwareOfferAsync(device, mouse, dock);
            if (generation != _firmwareGeneration)
            {
                return;
            }

            ApplyFirmwareOffer(mouse, dock, offer);
        }
        catch (Exception ex)
        {
            Log("FIRMWARE -> " + ex.Message);
            if (generation == _firmwareGeneration)
            {
                ResetFirmwareAction();
                ShowFirmwareStatus("Güncelleme listesi okunamadı.", autoHide: true);
            }
        }
        finally
        {
            _firmwareBusy = false;
            if (generation == _firmwareGeneration)
            {
                FirmwareButton.IsEnabled = _mouseFirmware is not null || _dongleFirmware is not null;
            }
        }
    }

    private static async Task<Protocol.FirmwareOffer?> FindFirmwareOfferAsync(
        HidWin.Device? device, string? mouse, string? dock)
    {
        if (device is null)
        {
            return null;
        }

        if (Protocol.FirmwareProductForPid(device.Pid) is int direct)
        {
            return await Protocol.GetOfficialFirmwareAsync(direct);
        }

        if (device.Pid != 0x00B3)
        {
            return null;
        }

        foreach (int product in Protocol.DongleMouseProducts)
        {
            var offer = await Protocol.GetOfficialFirmwareAsync(product);
            if (offer is null)
            {
                continue;
            }

            if (mouse is not null && Protocol.SameFirmwareLine(offer.Version, mouse))
            {
                return offer;
            }

            if (mouse is null && dock is not null && Protocol.SameFirmwareLine(offer.Version, dock))
            {
                return offer;
            }
        }

        return null;
    }

    private void ApplyFirmwareOffer(string? mouse, string? dock, Protocol.FirmwareOffer? offer)
    {
        ResetFirmwareAction();
        string? current = mouse;
        if (current is null && dock is not null && offer is not null
            && Protocol.SameFirmwareLine(offer.Version, dock))
        {
            current = dock;
        }

        if (offer is null || current is null)
        {
            Log("FIRMWARE -> yayinlanmis kurulum yok");
            ShowFirmwareStatus("Bu cihaz için sitede kurulum dosyası yok.", autoHide: true);
            return;
        }

        Log($"FIRMWARE -> cihaz {current} / kurulum {offer.Version} sayfa={(offer.SupportPageRead ? "okundu" : "yok")}");
        bool newer = Protocol.FirmwareIsNewer(offer.Version, current);
        if (!offer.SupportPageRead && !newer)
        {
            _firmwareNext = FirmwareNext.OpenPage;
            _firmwarePageUrl = offer.PageUrl;
            FirmwareButtonText.Text = "Destek sayfasını aç";
            ShowFirmwareStatus(
                $"Cihaz {current}. Synapse listesi {offer.Version}. Destek sayfası okunamadı; sitede daha yeni dosya olabilir.",
                autoHide: false);
            return;
        }

        if (!newer)
        {
            ShowFirmwareStatus($"Cihaz {current}. Sitedeki kurulum {offer.Version}, güncel.", autoHide: true);
            return;
        }

        if (offer.FileUrl is null)
        {
            _firmwareNext = FirmwareNext.OpenPage;
            _firmwarePageUrl = offer.PageUrl;
            FirmwareButtonText.Text = "Destek sayfasını aç";
            ShowFirmwareStatus($"Cihaz {current}. Sitedeki kurulum {offer.Version}. Dosya adresi sayfada.", autoHide: false);
            return;
        }

        _firmwareNext = FirmwareNext.Download;
        _firmwareFileUrl = offer.FileUrl;
        _firmwareFileName = FirmwareFileName(offer.FileUrl);
        FirmwareButtonText.Text = "İndirilecek yeri seç";
        ShowFirmwareStatus(
            $"Cihaz {current}. Sitedeki kurulum {offer.Version}. İndirmeden önce kayıt yerini seçeceksin.",
            autoHide: false);
    }

    private async Task DownloadFirmwareAsync()
    {
        if (_firmwareFileUrl is null || _firmwareFileName is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Firmware kurulumunu kaydet",
            FileName = _firmwareFileName,
            Filter = "Kurulum dosyası|*.exe;*.zip",
            InitialDirectory = FirmwareSaveDirectory(),
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string? folder = Path.GetDirectoryName(dialog.FileName);
        if (!string.IsNullOrEmpty(folder))
        {
            _settings.FirmwareDirectory = folder;
            _settings.Save();
        }

        int generation = _firmwareGeneration;
        string url = _firmwareFileUrl;
        _firmwareBusy = true;
        FirmwareButton.IsEnabled = false;
        ShowFirmwareStatus("İndiriliyor…", autoHide: false);
        try
        {
            await Protocol.DownloadFirmwarePackageAsync(url, dialog.FileName);
            Log("FIRMWARE -> kaydedildi " + Path.GetFileName(dialog.FileName));
            if (generation != _firmwareGeneration)
            {
                return;
            }

            ResetFirmwareAction();
            ShowFirmwareStatus("Kurulum kaydedildi. Dosya çalıştırılmadı: " + dialog.FileName, autoHide: true);
        }
        catch (Exception ex)
        {
            Log("FIRMWARE -> " + ex.Message);
            if (generation == _firmwareGeneration)
            {
                ShowFirmwareStatus("Kurulum indirilemedi.", autoHide: true);
            }
        }
        finally
        {
            _firmwareBusy = false;
            if (generation == _firmwareGeneration)
            {
                FirmwareButton.IsEnabled = _mouseFirmware is not null || _dongleFirmware is not null;
            }
        }
    }

    private void OpenFirmwarePage()
    {
        if (_firmwarePageUrl is not null
            && Uri.TryCreate(_firmwarePageUrl, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && (uri.Host.Equals("mysupport.razer.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("rzr.to", StringComparison.OrdinalIgnoreCase)))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private void ResetFirmwareAction()
    {
        _firmwareNext = FirmwareNext.Check;
        _firmwareFileUrl = null;
        _firmwarePageUrl = null;
        _firmwareFileName = null;
        FirmwareButtonText.Text = "Firmware güncellemesini kontrol et";
    }

    private string FirmwareSaveDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_settings.FirmwareDirectory)
            && System.IO.Directory.Exists(_settings.FirmwareDirectory))
        {
            return _settings.FirmwareDirectory;
        }

        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return System.IO.Directory.Exists(downloads)
            ? downloads
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string FirmwareFileName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string name = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrEmpty(name) && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            {
                return name;
            }
        }

        return "FirmwareUpdater.exe";
    }

    private void ShowFirmwareStatus(string text, bool autoHide = false)
    {
        _firmwareStatusTimer.Stop();
        FirmwareStatusText.Text = text;
        FirmwareStatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (autoHide && text.Length > 0)
        {
            _firmwareStatusTimer.Start();
        }
    }

    private async Task ReadSurfaceAsync()
    {
        if (_device is null)
        {
            SurfacePanel.Visibility = Visibility.Collapsed;
            return;
        }

        var surface = await Protocol.GetSurfaceAsync(_device);
        if (surface.LiftMode is not byte mode || surface.LiftHeight is not byte height)
        {
            Log($"SURFACE -> {Protocol.StatusText(surface.Status)}");
            SurfacePanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (mode == Protocol.SurfaceLiftSmart && height <= Protocol.SurfaceMaxMm - 1)
        {
            int lift = surface.LiftOff ?? 13;
            int land = surface.Landing ?? 12;
            if (lift == 1 && land == 1)
            {
                lift = 13;
                land = 12;
            }

            Log($"SURFACE -> akilli {height + 1} mm");
            ShowSurface(height + 1, asymmetric: false, lift, land);
            return;
        }

        if (mode == Protocol.SurfaceLiftAsymmetric)
        {
            int lift = ClampStep(surface.LiftOff ?? _surfaceLift);
            int land = ClampStep(surface.Landing ?? _surfaceLand);
            Log($"SURFACE -> asimetrik kaldirma {lift} inis {land}");
            ShowSurface(_surfaceMm, asymmetric: true, lift, land);
            return;
        }

        Log($"SURFACE -> mod {mode} yukseklik {height}");
        SurfacePanel.Visibility = Visibility.Collapsed;
    }

    private static int ClampStep(int value) => Math.Clamp(value, Protocol.SurfaceMinStep, Protocol.SurfaceMaxStep);

    private void ShowSurface(int millimeters, bool asymmetric, int liftOff, int landing)
    {
        bool wasHidden = SurfacePanel.Visibility != Visibility.Visible;
        SurfacePanel.Visibility = Visibility.Visible;
        _surfaceMm = millimeters;
        _surfaceAsymmetric = asymmetric;
        _surfaceLift = ClampStep(liftOff);
        _surfaceLand = ClampStep(landing);
        _syncingUi = true;
        try
        {
            SurfaceLow.IsChecked = !asymmetric && millimeters == 1;
            SurfaceMid.IsChecked = !asymmetric && millimeters == 2;
            SurfaceHigh.IsChecked = !asymmetric && millimeters == 3;
            SurfaceDistanceRow.Visibility = asymmetric ? Visibility.Collapsed : Visibility.Visible;
            SurfaceAsymmetric.IsChecked = asymmetric;
            SurfaceAsymmetricPanel.Visibility = asymmetric ? Visibility.Visible : Visibility.Collapsed;
            SurfaceLiftSlider.Value = _surfaceLift;
            SurfaceLandSlider.Value = _surfaceLand;
            SurfaceLiftText.Text = _surfaceLift.ToString();
            SurfaceLandText.Text = _surfaceLand.ToString();
            SurfaceValueText.Text = asymmetric ? "Asimetrik" : SurfaceMmText(millimeters);
            SurfaceHint.Text = asymmetric
                ? "Kaldırma ve iniş ayrı. Fare bu eşiklerde izlemeyi bırakır ve yeniden başlar."
                : "Düşükte fare yüzeyden az kalkınca izlemeyi bırakır. Yüksekte daha yukarıda da izler.";
        }
        finally
        {
            _syncingUi = false;
        }

        if (wasHidden)
        {
            FitWindowToContent();
        }
    }

    private static string SurfaceMmText(int millimeters) => millimeters switch
    {
        1 => "1 mm",
        3 => "3 mm",
        _ => "2 mm",
    };

    private async void SurfaceDistance_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || _busy || _device is null || _surfaceAsymmetric)
        {
            return;
        }

        int millimeters = sender == SurfaceHigh ? 3 : sender == SurfaceLow ? 1 : 2;
        if (_surfaceMm == millimeters)
        {
            return;
        }

        SurfaceValueText.Text = SurfaceMmText(millimeters);
        await WriteSurfaceAsync(millimeters, asymmetric: false, _surfaceLift, _surfaceLand);
    }

    private async void SurfaceAsymmetric_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || _busy || _device is null || SurfaceAsymmetric.IsChecked is not bool asymmetric)
        {
            return;
        }

        if (asymmetric == _surfaceAsymmetric)
        {
            return;
        }

        await WriteSurfaceAsync(_surfaceMm, asymmetric, _surfaceLift, _surfaceLand);
    }

    private async void SurfaceStep_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Kaydirici olusurken deger ayari olayi tetikler; ikinci kaydirici henuz yoktur.
        if (SurfaceLiftSlider is null || SurfaceLandSlider is null)
        {
            return;
        }

        int lift = (int)Math.Round(SurfaceLiftSlider.Value);
        int land = (int)Math.Round(SurfaceLandSlider.Value);
        if (SurfaceLiftText is not null)
        {
            SurfaceLiftText.Text = lift.ToString();
        }

        if (SurfaceLandText is not null)
        {
            SurfaceLandText.Text = land.ToString();
        }

        if (_syncingUi || _device is null || !_surfaceAsymmetric || (lift == _surfaceLift && land == _surfaceLand))
        {
            return;
        }

        int generation = ++_surfaceWriteGeneration;
        try
        {
            var started = Environment.TickCount64;
            while (generation == _surfaceWriteGeneration)
            {
                await Task.Delay(250);
                if (generation != _surfaceWriteGeneration || _syncingUi || _device is null)
                {
                    return;
                }

                if (_busy)
                {
                    if (Environment.TickCount64 - started > 4000)
                    {
                        return;
                    }

                    continue;
                }

                lift = (int)Math.Round(SurfaceLiftSlider.Value);
                land = (int)Math.Round(SurfaceLandSlider.Value);
                if (!_surfaceAsymmetric || (lift == _surfaceLift && land == _surfaceLand))
                {
                    return;
                }

                await WriteSurfaceAsync(_surfaceMm, asymmetric: true, lift, land);
                return;
            }
        }
        catch (Exception ex)
        {
            Log("SURFACE ERROR -> " + ex.Message);
            StatusText.Text = "Yüzey kalibrasyonu yazılamadı";
        }
    }

    private async Task WriteSurfaceAsync(int millimeters, bool asymmetric, int liftOff, int landing)
    {
        if (_device is null)
        {
            return;
        }

        SetBusy(true);
        try
        {
            await Protocol.EnsureDriverModeAsync(_device, Log);
            byte status = asymmetric
                ? await Protocol.SetSurfaceAsymmetricAsync(_device, liftOff, landing)
                : await Protocol.SetSurfaceSmartAsync(_device, millimeters);
            Log(asymmetric
                ? $"SURFACE SET -> asimetrik kaldirma {liftOff} inis {landing} {Protocol.StatusText(status)}"
                : $"SURFACE SET -> {millimeters} mm {Protocol.StatusText(status)}");
            if (!Protocol.IsSuccess(status))
            {
                StatusText.Text = "Yüzey kalibrasyonu yazılamadı";
                await ReadSurfaceAsync();
                return;
            }

            StatusText.Text = "Yüzey kalibrasyonu uygulandı";
            await ReadSurfaceAsync();
        }
        catch (Exception ex)
        {
            Log("SURFACE ERROR -> " + ex.Message);
            StatusText.Text = "Yüzey kalibrasyonu yazılamadı";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static string LedHintText(byte mode) => mode switch
    {
        Protocol.LedConnection => "Cihaz bağlıyken sürekli beyaz yanar.",
        Protocol.LedBattery => "Pile göre yeşil (%100), sarı (%66), turuncu (%33), kırmızı (%0).",
        Protocol.LedWarning => "Normalde kapalı. Şarj gerekirken kırmızı yanıp söner.",
        _ => "",
    };

    private async Task ReadIndicatorLedAsync()
    {
        if (_device is null || _device.Pid != 0x00B3)
        {
            DongleLedPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var led = await Protocol.GetIndicatorLedAsync(_device);
        Log($"DONGLE LED -> {(led.Mode is byte m ? m.ToString() : Protocol.StatusText(led.Status))}");
        if (led.Mode is not byte mode)
        {
            DongleLedPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ShowDongleLed(mode);
    }

    private void ShowDongleLed(byte mode)
    {
        bool wasHidden = DongleLedPanel.Visibility != Visibility.Visible;
        DongleLedPanel.Visibility = Visibility.Visible;
        _syncingUi = true;
        try
        {
            LedConnection.IsChecked = mode == Protocol.LedConnection;
            LedBattery.IsChecked = mode == Protocol.LedBattery;
            LedWarning.IsChecked = mode == Protocol.LedWarning;
            LedHint.Text = LedHintText(mode);
        }
        finally
        {
            _syncingUi = false;
        }

        if (wasHidden)
        {
            FitWindowToContent();
        }
    }

    private async void DongleLed_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || _busy || _device is null || _device.Pid != 0x00B3)
        {
            return;
        }

        byte mode = sender == LedWarning ? Protocol.LedWarning
            : sender == LedBattery ? Protocol.LedBattery
            : Protocol.LedConnection;

        SetBusy(true);
        try
        {
            await Protocol.EnsureDriverModeAsync(_device, Log);
            var status = await Protocol.SetIndicatorLedAsync(_device, mode);
            Log($"DONGLE LED SET -> {mode} {Protocol.StatusText(status)}");
            if (!Protocol.IsSuccess(status))
            {
                StatusText.Text = "Dongle LED yazılamadı";
                await ReadIndicatorLedAsync();
                return;
            }

            LedHint.Text = LedHintText(mode);
            StatusText.Text = "Dongle LED uygulandı";
        }
        catch (Exception ex)
        {
            Log("DONGLE LED ERROR -> " + ex.Message);
            StatusText.Text = "Dongle LED yazılamadı";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void BatteryTimer_Tick(object? sender, EventArgs e)
    {
        if (_busy || _device is null)
        {
            return;
        }

        // Ilk acilista telsiz henuz yokken pil okumasi da dusuyor. Yenile beklemeden
        // birkac saniyede bir tum okumayi tekrarla; baglaninca normal pil temposuna don.
        if (!_linkReady)
        {
            _busy = true;
            try
            {
                await ReadDeviceAsync(quiet: true);
            }
            finally
            {
                _busy = false;
            }

            _batteryTimer.Interval = _linkReady ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(3);
            return;
        }

        // Kisa arka plan okumasi; UI kilitlenmez, sadece cakismayi onlemek icin bayrak kullanilir.
        _busy = true;
        bool ok;
        try
        {
            ok = await ReadBatteryAsync(background: true);
        }
        finally
        {
            _busy = false;
        }

        // Dusuk pilde daha sik bak (2 dk -> 45 sn); normalde 2 dk.
        bool low = _lastBattery is { Percent: int p, Charging: false } && p <= _settings.LowBatteryPercent;
        _batteryTimer.Interval = TimeSpan.FromSeconds(low ? 45 : 120);

        // Bagli oturumda bir okuma eksik kaldiysa, fare yeniden ulasilinca tamamla.
        if (ok && _needsFullRead && _device is not null)
        {
            Log("DEVICE GET -> fare tekrar ulasilabilir, eksik okuma tamamlaniyor");
            await ReadDeviceAsync(quiet: true);
        }
    }

    // ------------------------------------------------------------------
    // DPI kademeleri
    // ------------------------------------------------------------------

    private TextBox[] StageBoxes => [Stage1Box, Stage2Box, Stage3Box, Stage4Box, Stage5Box];

    private RadioButton[] StageRadios => [Stage1Active, Stage2Active, Stage3Active, Stage4Active, Stage5Active];

    private async Task ReadStagesAsync()
    {
        if (_device is null)
        {
            return;
        }

        try
        {
            var set = await Protocol.GetDpiStagesAsync(_device);
            FillStagesUi(set);
            Log($"DPI STAGES -> aktif {set.Active}: [{string.Join(", ", set.Stages.Select(s => s.X))}]");
        }
        catch (Exception ex)
        {
            StagesHeaderText.Text = "okunamadı";
            Log("DPI STAGES ERROR -> " + ex.Message);
        }
    }

    private void FillStagesUi(Protocol.DpiStageSet set)
    {
        var boxes = StageBoxes;
        var radios = StageRadios;
        _syncingUi = true;
        try
        {
            for (int i = 0; i < boxes.Length; i++)
            {
                var stage = set.Stages.FirstOrDefault(s => s.Index == i + 1);
                boxes[i].Text = stage is null ? "" : stage.X.ToString();
                radios[i].IsChecked = set.Active == i + 1;
            }
        }
        finally
        {
            _syncingUi = false;
        }

        var active = set.Stages.FirstOrDefault(s => s.Index == set.Active);
        StagesHeaderText.Text = active is null
            ? $"{set.Stages.Count} Kademe"
            : $"Aktif: {active.X} DPI  ·  {set.Stages.Count} Kademe";
    }

    private async void WriteStagesButton_Click(object sender, RoutedEventArgs e)
    {
        await WriteStagesAsync("buton");
    }

    /// <summary>
    /// Aktif kademe radyo dugmesi: secim aninda fareye uygulanir. Razer protokolunde
    /// "yalnizca aktif kademeyi degistir" komutu yok; tablo (0x04/0x06) butun olarak
    /// yazilir, degerler ekranda gorunenlerdir. Kullanici sadece secim yapiyorsa bu,
    /// farenin zaten sahip oldugu tabloyu farkli aktif indeksle yazmak demektir.
    /// </summary>
    private async void StageActive_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || _busy || _device is null)
        {
            return;
        }

        await WriteStagesAsync("secim");
    }

    private async Task WriteStagesAsync(string source)
    {
        if (_device is null || _busy)
        {
            return;
        }

        var dpis = new List<int>();
        var boxes = StageBoxes;
        for (int i = 0; i < boxes.Length; i++)
        {
            var text = boxes[i].Text.Trim();
            if (text.Length == 0)
            {
                break;
            }

            if (!int.TryParse(text, out var v) || v is < 100 or > 45000)
            {
                MessageBox.Show($"Kademe {i + 1} gecersiz. 100 ile 45000 arasinda bir sayi girin.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            dpis.Add(v);
        }

        if (dpis.Count == 0)
        {
            MessageBox.Show("En az bir kademe girilmelidir (Kademe 1'den baslayarak).", AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        for (int i = dpis.Count; i < boxes.Length; i++)
        {
            if (boxes[i].Text.Trim().Length > 0)
            {
                MessageBox.Show("Kademeler bosluk birakilmadan sirayla doldurulmalidir.", AppTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        int active = Array.FindIndex(StageRadios, r => r.IsChecked == true) + 1;
        if (active < 1 || active > dpis.Count)
        {
            active = Math.Min(Math.Max(active, 1), dpis.Count);
        }

        SetBusy(true);
        try
        {
            Log("----------------------------------------");
            Log($"DPI STAGES SET ({source}) -> aktif {active}: [{string.Join(", ", dpis)}]");
            await Protocol.EnsureDriverModeAsync(_device, Log);
            var result = await Protocol.SetDpiStagesAsync(_device, active, dpis);
            FillStagesUi(result);
            Log($"DPI STAGES READBACK -> aktif {result.Active}: [{string.Join(", ", result.Stages.Select(s => s.X))}]");

            // Aktif kademe degistiyse canli DPI da degisebilir; guncel degeri goster.
            var dpi = await Protocol.GetDpiAsync(_device);
            if (Protocol.IsSuccess(dpi.Status))
            {
                _lastDpi = dpi.X;
                DpiBox.Text = dpi.X.ToString();
                CurrentText.Text = $"Mevcut: {dpi.X} DPI · {_lastHz} Hz";
                UpdateTrayToolTip();
            }
            Log($"DPI -> {DpiText(dpi)}");
            StatusText.Text = source == "secim" ? $"Aktif kademe {result.Active} uygulandı" : "Kademeler yazıldı";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Hata";
            Log("DPI STAGES ERROR -> " + ex.Message);
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ------------------------------------------------------------------
    // Mevcut islevler
    // ------------------------------------------------------------------

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await ReadDeviceAsync();
    }

    private void ClearDebugButton_Click(object sender, RoutedEventArgs e)
    {
        DebugBox.Clear();
        Log("Debug log temizlendi.");
    }

    private void TestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = FindPollingTester();
            if (path is null)
            {
                MessageBox.Show(
                    "PollingRateTesterApp_v1.00.01.exe bu surume gomulu degil.\nRazerLite.exe ile ayni klasore koyabilirsin.",
                    AppTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Log("TEST ERROR -> PollingRateTesterApp bulunamadi");
                return;
            }

            Log($"TEST -> {path}");
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path)!,
            });
        }
        catch (Exception ex)
        {
            Log("TEST ERROR -> " + ex.Message);
            MessageBox.Show(ex.Message, AppTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DpiBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await ApplyAsync();
        }
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyAsync();
    }

    /// <summary>Kablosuz baglantida BUSY/TIMEOUT: fare hareket ediyor veya uykuda. Log/durum metni icin.</summary>
    private static string Transient(byte status) =>
        $"{Protocol.StatusText(status)} (fare hareket ediyor / uykuda)";

    private static string DpiText((int X, int Y, byte Status) d) =>
        Protocol.IsSuccess(d.Status) ? $"{d.X} x {d.Y}" : Transient(d.Status);

    private static string HzText((int? Hz, byte Raw, byte Status) p) =>
        Protocol.IsSuccess(p.Status) && p.Hz is int hz ? $"{hz} Hz" : Transient(p.Status);

    /// <summary>
    /// Cihazi okur. Dongle uzerinden fare hareket ederken/uykudayken komutlar BUSY/TIMEOUT
    /// doner; bu durumda ekrana 0 yazilmaz, mevcut degerler korunur ve false doner.
    /// </summary>
    private async Task<bool> ReadDeviceAsync(bool quiet = false)
    {
        if (_device is null)
        {
            return false;
        }

        SetBusy(true);
        try
        {
            bool announce = _linkReady || !_connectWaitLogged;
            if (announce)
            {
                Log("----------------------------------------");
                Log("DEVICE GET BASLATILIYOR");
            }

            StatusText.Text = !_linkReady && _connectWaitLogged ? "Fare bağlanıyor…" : "Cihaz okunuyor...";

            await Protocol.EnsureDriverModeAsync(_device, Log);
            var poll = await Protocol.GetPollingAsync(_device);
            var dpi = await Protocol.GetDpiAsync(_device);

            if (announce)
            {
                Log($"POLLING -> {HzText(poll)}");
                Log($"DPI -> {DpiText(dpi)}");
            }
            UpdatePollingChoices();

            if (poll.Hz is int readHz)
            {
                SelectHz(readHz);
                _lastHz = readHz;
                UpdateLowPowerGate();
            }

            bool dpiOk = Protocol.IsSuccess(dpi.Status);
            if (dpiOk)
            {
                DpiBox.Text = dpi.X.ToString();
                _lastDpi = dpi.X;
            }

            if (!dpiOk || poll.Hz is null)
            {
                _needsFullRead = true;
                if (_linkReady)
                {
                    StatusText.Text = "Fare yanıt vermiyor";
                    CurrentText.Text = "Fareyi sabit tut (veya uyandır) ve Yenile'ye bas.";
                    Log("DEVICE GET INCOMPLETE -> fare yanit vermedi; pil/kademe okumasi atlandi");
                }
                else
                {
                    StatusText.Text = "Fare bağlanıyor…";
                    CurrentText.Text = "Dongle hazır. Fare telsizi birkaç saniye sürebilir.";
                    if (!_connectWaitLogged)
                    {
                        _connectWaitLogged = true;
                        Log("DEVICE GET -> fare telsizi henuz yok; baglaninca kendiliginden devam eder");
                    }
                }

                return false;
            }

            CurrentText.Text = $"Mevcut: {dpi.X} DPI · {poll.Hz.Value} Hz";
            StatusText.Text = "Hazır";
            Log("DEVICE GET SUCCESS");
            _needsFullRead = false;
            _linkReady = true;
            _connectWaitLogged = false;

            await ReadBatteryAsync();
            await ReadStagesAsync();
            await ReadTimeToSleepAsync();
            await ReadLowPowerAsync();
            await ReadSurfaceAsync();
            await ReadIndicatorLedAsync();
            await ReadFirmwareAsync();
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Hata";
            CurrentText.Text = "Cihaz okunamadi.";
            Log("DEVICE GET ERROR -> " + ex.Message);
            if (!quiet)
            {
                MessageBox.Show(
                    ex.Message + "\n\nDetaylari Debug / Device Log alaninda gorebilirsiniz.",
                    AppTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ApplyAsync()
    {
        if (_device is null || _busy)
        {
            return;
        }

        if (!int.TryParse(DpiBox.Text.Trim(), out var dpi) || dpi is < 100 or > 45000)
        {
            MessageBox.Show(
                "DPI 100 ile 45000 arasinda olmalidir.",
                AppTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var hz = ClampHz(SelectedHz(), out var wantedHz);
        SetBusy(true);

        try
        {
            Log("========================================");
            Log("APPLY BASLATILDI");
            if (hz != wantedHz)
            {
                Log($"POLLING -> {wantedHz} Hz bu baglantida ({_device.Info.ModeText}) desteklenmiyor, {hz} Hz uygulanacak");
            }
            Log($"TARGET -> DPI={dpi} / POLLING={hz} Hz");
            await Protocol.EnsureDriverModeAsync(_device, Log);

            StatusText.Text = $"DPI {dpi} uygulaniyor...";
            Log($"DPI SET -> {dpi}");
            var dpiResult = await Protocol.SetDpiAsync(_device, dpi);
            Log($"DPI READBACK -> {DpiText(dpiResult)}");

            Log("DEVICE SETTLE -> 150 ms");
            await Task.Delay(150);

            StatusText.Text = $"Polling {hz} Hz uygulaniyor...";
            Log($"POLLING SET -> {hz} Hz");
            var pollResult = await Protocol.SetPollingAsync(_device, hz, Log);
            Log($"POLLING READBACK -> {HzText(pollResult)}");

            if (Protocol.IsSuccess(pollResult.Status) && pollResult.Hz is int got && got != hz)
            {
                // Dongle komutu kabul etti ama deger degismedi (genelde fare o an hareket ediyordu): bir kez daha dene.
                StatusText.Text = $"Polling {hz} Hz tekrar deneniyor...";
                Log($"POLLING RETRY -> okunan {got} Hz, hedef {hz} Hz; komut tekrarlaniyor");
                await Task.Delay(300);
                pollResult = await Protocol.SetPollingAsync(_device, hz, Log);
                Log($"POLLING READBACK -> {HzText(pollResult)}");
            }

            StatusText.Text = "Dogrulaniyor...";
            Log("========================================");
            Log("VERIFICATION BASLATILDI");
            // Hz yazimi dongle'i yeniden numaralandirir; mod bu tutamakta tekrar yazilir.
            await Protocol.EnsureDriverModeAsync(_device, Log);

            var verifyPoll = await Protocol.GetPollingAsync(_device);
            var verifyDpi = await Protocol.GetDpiAsync(_device);
            Log($"VERIFY READBACK -> DPI={DpiText(verifyDpi)} / Polling={HzText(verifyPoll)}");

            bool dpiVerified = Protocol.IsSuccess(verifyDpi.Status);
            bool pollVerified = Protocol.IsSuccess(verifyPoll.Status) && verifyPoll.Hz is not null;

            if (dpiVerified)
            {
                DpiBox.Text = verifyDpi.X.ToString();
                _lastDpi = verifyDpi.X;
            }

            if (pollVerified)
            {
                SelectHz(verifyPoll.Hz!.Value);
                _lastHz = verifyPoll.Hz.Value;
                UpdateLowPowerGate();
            }

            if (dpiVerified && pollVerified)
            {
                CurrentText.Text = $"Mevcut: {verifyDpi.X} DPI · {verifyPoll.Hz} Hz";
                bool matches = verifyDpi.X == dpi && verifyPoll.Hz == hz;
                StatusText.Text = matches ? "Basariyla uygulandi" : "Uygulanamadı — fare meşguldü, tekrar dene";
                Log("========================================");
                Log(matches ? "APPLY SUCCESS" : "APPLY FAILED -> hedef ile okunan deger farkli (fare hareket ediyordu?)");
                Log($"FINAL -> DPI={verifyDpi.X} / POLLING={verifyPoll.Hz} Hz");
            }
            else
            {
                // Yazma basarili (aksi halde exception firlardi); yalnizca son okuma fare mesgul oldugu icin alinamadi.
                _needsFullRead = true;
                CurrentText.Text = "Uygulandı; fare sabitken Yenile ile doğrula.";
                StatusText.Text = "Uygulandı (doğrulama bekliyor)";
                Log("========================================");
                Log("APPLY DONE -> dogrulama okumasi fare mesgul oldugu icin alinamadi");
            }

            Log("HID CONNECTION -> STILL OPEN");
            Log("========================================");
            UpdateTrayToolTip();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Hata";
            Log("========================================");
            Log("APPLY ERROR");
            Log(ex.Message);
            Log("========================================");
            MessageBox.Show(
                ex.Message + "\n\nDetaylari Debug / Device Log alaninda gorebilirsiniz.",
                AppTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }
}
