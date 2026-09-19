using System.Drawing;
using WF = System.Windows.Forms;

namespace RazerLite;

/// <summary>
/// Sistem tepsisi ikonu ve koyu temali sag tik menusu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly WF.ContextMenuStrip _menu;
    private readonly WF.ToolStripMenuItem _profilesHeader;
    private readonly WF.ToolStripMenuItem _autostartItem;
    private readonly WF.ToolStripMenuItem _minimizeItem;
    private readonly List<WF.ToolStripItem> _profileItems = new();

    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action<int>? ProfileRequested;
    public event Action<bool>? AutostartToggled;
    public event Action<bool>? MinimizeToTrayToggled;

    public TrayIcon(Icon icon, string title)
    {
        _menu = new WF.ContextMenuStrip
        {
            Renderer = new DarkRenderer(),
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9.5f),
        };

        var show = new WF.ToolStripMenuItem("Göster");
        show.Click += (_, _) => ShowRequested?.Invoke();
        _menu.Items.Add(show);
        _menu.Items.Add(new WF.ToolStripSeparator());

        _profilesHeader = new WF.ToolStripMenuItem("Profiller") { Enabled = false };
        _menu.Items.Add(_profilesHeader);
        _menu.Items.Add(new WF.ToolStripSeparator());

        _autostartItem = new WF.ToolStripMenuItem("Windows ile başlat") { CheckOnClick = true };
        _autostartItem.CheckedChanged += (_, _) => AutostartToggled?.Invoke(_autostartItem.Checked);
        _menu.Items.Add(_autostartItem);

        _minimizeItem = new WF.ToolStripMenuItem("Kapatınca tepsiye küçült") { CheckOnClick = true };
        _minimizeItem.CheckedChanged += (_, _) => MinimizeToTrayToggled?.Invoke(_minimizeItem.Checked);
        _menu.Items.Add(_minimizeItem);

        _menu.Items.Add(new WF.ToolStripSeparator());
        var exit = new WF.ToolStripMenuItem("Çıkış");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.Add(exit);

        _icon = new WF.NotifyIcon
        {
            Icon = icon,
            Text = title,
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public void SetToolTip(string text)
    {
        // NotifyIcon.Text en fazla 127 karakter kabul eder.
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    public void SetChecks(bool autostart, bool minimizeToTray)
    {
        _autostartItem.Checked = autostart;
        _minimizeItem.Checked = minimizeToTray;
    }

    public void SetProfiles(IReadOnlyList<Profile> profiles)
    {
        foreach (var item in _profileItems)
        {
            _menu.Items.Remove(item);
            item.Dispose();
        }

        _profileItems.Clear();

        int insertAt = _menu.Items.IndexOf(_profilesHeader) + 1;
        if (profiles.Count == 0)
        {
            var none = new WF.ToolStripMenuItem("(kayıtlı profil yok)") { Enabled = false };
            _menu.Items.Insert(insertAt, none);
            _profileItems.Add(none);
            return;
        }

        for (int i = 0; i < profiles.Count; i++)
        {
            int slot = i + 1;
            var p = profiles[i];
            var item = new WF.ToolStripMenuItem($"{p.Name}  ·  {p.Dpi} DPI / {p.Hz} Hz")
            {
                ShortcutKeyDisplayString = HotKeys.Label(slot),
            };
            item.Click += (_, _) => ProfileRequested?.Invoke(slot);
            _menu.Items.Insert(insertAt + i, item);
            _profileItems.Add(item);
        }
    }

    public void Balloon(string title, string text)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    private sealed class DarkRenderer : WF.ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors())
        {
        }

        protected override void OnRenderItemText(WF.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.White : Color.FromArgb(140, 140, 140);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(WF.ToolStripItemImageRenderEventArgs e)
        {
            using var pen = new Pen(Color.White, 2f);
            var r = e.ImageRectangle;
            e.Graphics.DrawLines(pen, new[]
            {
                new Point(r.Left + 3, r.Top + r.Height / 2),
                new Point(r.Left + r.Width / 2 - 1, r.Bottom - 4),
                new Point(r.Right - 3, r.Top + 3),
            });
        }
    }

    private sealed class DarkColors : WF.ProfessionalColorTable
    {
        private static readonly Color Bg = Color.FromArgb(0x1E, 0x1E, 0x1E);
        private static readonly Color Hover = Color.FromArgb(0x38, 0x38, 0x38);
        private static readonly Color Line = Color.FromArgb(0x44, 0x44, 0x44);

        public override Color ToolStripDropDownBackground => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Bg;
        public override Color CheckBackground => Hover;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}
