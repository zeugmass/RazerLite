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
    public const string AppTitle = "RazerLite 0.16";

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
    private bool _batteryBusyLogged;  // arka plan pil okumasinda BUSY spam'ini onle

    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x44, 0xD6, 0x2C));
    private static readonly Brush AmberBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x40));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50));
    private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));

    public MainWindow()
    {
        InitializeComponent();

        _settings = Settings.Load();
        // Kayit defteri gercek durumdur; ayar dosyasini ona esitle.
        _settings.StartWithWindows = Autostart.IsEnabled();

        _batteryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
        _batteryTimer.Tick += BatteryTimer_Tick;

        // USB tak/cikar olaylarini toplayip 1 sn sonra tek seferde degerlendir.
        _deviceChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _deviceChangeTimer.Tick += DeviceChangeTimer_Tick;

        // Windows kapanirken/oturum kapanirken tepsiye kucultme yerine gercekten kapan.
        Application.Current.SessionEnding += (_, _) => _exiting = true;

        InitTrayAndHotkeys();
        SyncSettingsUi();
        RefreshProfileUi();
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

        return null;
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
    }

    /// <summary>Uygun Razer cihazini (kablolu fare > dongle > kablosuz) acar ve okur. Cihaz yoksa bekleme durumuna gecer.</summary>
    private async Task ConnectAsync()
    {
        SetBusy(true);
        try
        {
            _device = await Task.Run(() => HidWin.Open());
            DeviceSubtitle.Text = _device.Info.Title;
            Log($"DEVICE -> {_device.Info.Title} (VID=1532 PID={_device.Pid:X4})");
            Log($"HID CONNECTION -> READY");
            Log($"PATH -> {_device.Path}");

            // Dongle yeni takildiginda fare ile telsiz baglanti birkac saniye sonra kurulur;
            // ilk okuma TIMEOUT/BUSY donerse kisa aralikla tekrar dene.
            const int attempts = 3;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                if (await ReadDeviceAsync(quiet: true) || _device is null)
                {
                    break;
                }

                if (attempt < attempts)
                {
                    StatusText.Text = $"Fare bekleniyor… ({attempt}/{attempts - 1})";
                    Log($"DEVICE GET -> fare yanit vermedi, {attempt}. tekrar 2 sn sonra");
                    await Task.Delay(2000);
                }
            }

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
        DeviceSubtitle.Text = "Cihaz bekleniyor…";
        StatusText.Text = "Cihaz bulunamadı";
        CurrentText.Text = "Dongle'ı veya fare kablosunu tak; otomatik bağlanır.";
        BatteryText.Text = "";
        LinkText.Text = "";
        StagesHeaderText.Text = "DPI Kademeleri (fare DPI tuşu)";
        _lastBattery = null;
        UpdateTrayToolTip();
    }

    // ------------------------------------------------------------------
    // USB tak/cikar izleme (kablolu <-> kablosuz gecisi)
    // ------------------------------------------------------------------

    private const int WmDeviceChange = 0x0219;
    private const int DbtDevNodesChanged = 0x0007;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;

    private void InitDeviceWatcher()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(DeviceChangeHook);
    }

    private IntPtr DeviceChangeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmDeviceChange)
        {
            int ev = wParam.ToInt32();
            if (ev is DbtDevNodesChanged or DbtDeviceArrival or DbtDeviceRemoveComplete)
            {
                _deviceChangeTimer.Stop();
                _deviceChangeTimer.Start();
            }
        }

        return IntPtr.Zero;
    }

    private async void DeviceChangeTimer_Tick(object? sender, EventArgs e)
    {
        _deviceChangeTimer.Stop();
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

        List<string> paths;
        try
        {
            paths = await Task.Run(() => HidWin.ListControlPaths());
        }
        catch (Exception ex)
        {
            Log("DEVICE SCAN ERROR -> " + ex.Message);
            return;
        }

        var best = paths.FirstOrDefault();

        if (_device is not null)
        {
            bool present = paths.Any(p => string.Equals(p, _device.Path, StringComparison.OrdinalIgnoreCase));
            bool betterWired = best is not null
                && HidWin.Describe(HidWin.PidFromPath(best)).Mode == HidWin.LinkMode.Wired
                && _device.Info.Mode != HidWin.LinkMode.Wired;

            if (present && !betterWired)
            {
                Log($"DEVICE SCAN -> {paths.Count} Razer cihazi, {_device.Info.Title} yerinde; degisiklik yok");
                return;
            }

            Log("========================================");
            Log(present
                ? $"DEVICE CHANGE -> kablo takildi, {_device.Info.Title} yerine kablolu fareye geciliyor"
                : $"DEVICE CHANGE -> {_device.Info.Title} ayrildi");
            _batteryTimer.Stop();
            _device.Dispose();
            _device = null;
        }

        if (best is null)
        {
            EnterWaitingState();
            Log("DEVICE CHANGE -> uygun Razer cihazi yok, bekleniyor");
            return;
        }

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
                    Content = $"{i + 1}.  {p.Name}  —  {p.Dpi} DPI / {p.Hz} Hz",
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
            SelectHz(p.Hz);
            Log($"PROFIL SECILDI -> {p.Name} ({p.Dpi} DPI / {p.Hz} Hz). Uygulamak icin UYGULA veya {HotKeys.Label(idx + 1)}.");
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

        // Kural: girilen ad mevcut bir profille aynıysa o profil güncellenir, yeni ad yeni profil olur.
        // Secili profil varsa adi varsayilan olarak gelir (Enter = guncelle); farkli ad yaz = yeni profil.
        int? selectedIdx = ProfileBox.SelectedItem is ComboBoxItem { Tag: int idx } && idx < _settings.Profiles.Count ? idx : null;
        bool full = _settings.Profiles.Count >= Settings.MaxProfiles;

        var initial = selectedIdx is int si
            ? _settings.Profiles[si].Name
            : full ? "" : $"Profil {_settings.Profiles.Count + 1}";

        var hint = full
            ? $"Profil adı ({dpi} DPI / {hz} Hz) — {Settings.MaxProfiles} profil dolu, mevcut bir adı yazarsan güncellenir:"
            : $"Profil adı ({dpi} DPI / {hz} Hz) — yeni ad = yeni profil, mevcut ad = güncelle:";

        var name = PromptWindow.Ask(this, AppTitle, hint, initial);
        if (name is null)
        {
            return;
        }

        int existing = _settings.Profiles.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        int selected;
        if (existing >= 0)
        {
            _settings.Profiles[existing] = new Profile { Name = name, Dpi = dpi, Hz = hz };
            selected = existing;
            Log($"PROFIL GUNCELLENDI -> {selected + 1}. {name}: {dpi} DPI / {hz} Hz");
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

            _settings.Profiles.Add(new Profile { Name = name, Dpi = dpi, Hz = hz });
            selected = _settings.Profiles.Count - 1;
            Log($"PROFIL KAYDEDILDI -> {selected + 1}. {name}: {dpi} DPI / {hz} Hz ({HotKeys.Label(selected + 1)})");
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
        Log($"PROFIL {slot} ({source}) -> {p.Name}: {p.Dpi} DPI / {p.Hz} Hz");

        DpiBox.Text = p.Dpi.ToString();
        SelectHz(p.Hz);
        RefreshProfileUi(slot - 1);

        await ApplyAsync();

        if (_lastDpi == p.Dpi && _lastHz == p.Hz)
        {
            _tray?.Balloon(AppTitle, $"{p.Name} uygulandı: {p.Dpi} DPI · {p.Hz} Hz");
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
                BatteryText.Foreground = pct >= 50 ? GreenBrush : pct >= 20 ? AmberBrush : RedBrush;
                LinkText.Text = b.Charging ? "şarj oluyor" : "fare bağlı";
                Log($"BATTERY -> %{pct}{(b.Charging ? " (sarj)" : "")}");
                UpdateTrayToolTip();
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
            LinkText.Text = "fare yanıt vermiyor";
            Log($"BATTERY -> {Transient(b.Status)}");
        }
        catch (Exception ex)
        {
            BatteryText.Text = "Pil —";
            BatteryText.Foreground = DimBrush;
            LinkText.Text = "okunamadı";
            Log("BATTERY ERROR -> " + ex.Message);
        }

        UpdateTrayToolTip();
        return false;
    }

    private async void BatteryTimer_Tick(object? sender, EventArgs e)
    {
        if (_busy || _device is null)
        {
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

        // Onceki cihaz okumasi fare yanit vermediginden eksik kalmissa, fare artik
        // ulasilabilir oldugu icin DPI/Hz/kademeleri kullaniciya sormadan tamamla.
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
            StagesHeaderText.Text = "DPI Kademeleri (okunamadı)";
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
            ? "DPI Kademeleri (fare DPI tuşu)"
            : $"DPI Kademeleri (fare DPI tuşu)  ·  aktif: {active.X} DPI  ·  {set.Stages.Count} kademe";
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
                    "PollingRateTesterApp_v1.00.01.exe bulunamadi.\nRazerLite.exe ile ayni klasore koy.",
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
            Log("----------------------------------------");
            Log("DEVICE GET BASLATILIYOR");
            StatusText.Text = "Cihaz okunuyor...";

            var poll = await Protocol.GetPollingAsync(_device);
            var dpi = await Protocol.GetDpiAsync(_device);

            Log($"POLLING -> {HzText(poll)}");
            Log($"DPI -> {DpiText(dpi)}");
            UpdatePollingChoices();

            if (poll.Hz is int readHz)
            {
                SelectHz(readHz);
                _lastHz = readHz;
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
                StatusText.Text = "Fare yanıt vermiyor";
                CurrentText.Text = "Fareyi sabit tut (veya uyandır) ve Yenile'ye bas.";
                Log("DEVICE GET INCOMPLETE -> fare yanit vermedi; pil/kademe okumasi atlandi");
                return false;
            }

            CurrentText.Text = $"Mevcut: {dpi.X} DPI · {poll.Hz.Value} Hz";
            StatusText.Text = "Hazır";
            Log("DEVICE GET SUCCESS");
            _needsFullRead = false;

            await ReadBatteryAsync();
            await ReadStagesAsync();
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
