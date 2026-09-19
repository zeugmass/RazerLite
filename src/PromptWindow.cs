using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RazerLite;

/// <summary>
/// Koyu temali tek satirlik metin sorusu (profil adi icin).
/// </summary>
internal sealed class PromptWindow : Window
{
    private readonly TextBox _input;

    public string Value => _input.Text.Trim();

    private PromptWindow(Window owner, string title, string message, string initial)
    {
        Owner = owner;
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x15, 0x15, 0x15));
        ShowInTaskbar = false;
        Icon = owner.Icon;

        // Ana pencerenin stillerini (TextBox, Button) bu pencerede de kullan.
        Resources.MergedDictionaries.Add(owner.Resources);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        _input = new TextBox
        {
            Text = initial,
            Height = 40,
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = 24,
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Accept();
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
            }
        };
        root.Children.Add(_input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var ok = new Button { Content = "Tamam", Width = 84, Height = 32, IsDefault = true };
        ok.Click += (_, _) => Accept();
        var cancel = new Button { Content = "İptal", Width = 84, Height = 32, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    private void Accept()
    {
        if (Value.Length == 0)
        {
            _input.Focus();
            return;
        }

        DialogResult = true;
    }

    public static string? Ask(Window owner, string title, string message, string initial = "")
    {
        var dlg = new PromptWindow(owner, title, message, initial);
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }
}
