using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FsAnalyser.Core.Workarounds;

namespace FsAnalyser.App;

/// <summary>
/// The app's settings (SL-095): one checkbox per precaution against a defect outside the app, every one listed from
/// Core's <see cref="KnownWorkarounds"/> with its outside defect and where it lives, what the app does while it is on,
/// and what turning it off brings back. The window names no entry. Each is ticked at every launch; unticking one asks
/// first, in a <see cref="ConfirmTurnOffWindow"/> whose question states the consequence, and No leaves it ticked. What
/// is set here lasts until the app is closed: nothing is written to disk. The next scan takes it.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly HashSet<string> _enabled;

    public SettingsWindow(IReadOnlySet<string> enabled)
    {
        _enabled = new HashSet<string>(enabled, StringComparer.Ordinal);
        Title = "Settings";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "What this app does to avoid defects that are not its own: in Windows, a driver or another program it depends on. " +
                "Each is on every time the app starts; turning one off lasts until the app is closed.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });
        int index = 0;
        foreach (KnownWorkaround workaround in KnownWorkarounds.All)
            panel.Children.Add(Entry(workaround, index++));

        var close = new Button { Name = "SettingsClose", Content = "Close", IsDefault = true, IsCancel = true, Padding = new Thickness(16, 4, 16, 4), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        Content = panel;
    }

    /// <summary>The ids of the precautions left on.</summary>
    public IReadOnlySet<string> Enabled => _enabled;

    private UIElement Entry(KnownWorkaround workaround, int index)
    {
        var box = new CheckBox
        {
            Name = $"WorkaroundBox{index}",
            Tag = workaround.Id,
            Content = new TextBlock { Text = workaround.Title, FontWeight = FontWeights.SemiBold },
            IsChecked = _enabled.Contains(workaround.Id),
        };
        box.Checked += (_, _) => _enabled.Add(workaround.Id);
        box.Unchecked += (_, _) =>
        {
            var confirm = new ConfirmTurnOffWindow(workaround) { Owner = this };
            if (confirm.ShowDialog() == true)
                _enabled.Remove(workaround.Id);
            else
                box.IsChecked = true;
        };

        var entry = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        entry.Children.Add(box);
        entry.Children.Add(Paragraph("The defect: " + workaround.Defect));
        entry.Children.Add(Paragraph("Where it lives: " + workaround.WhereItLives));
        entry.Children.Add(Paragraph("What the app does: " + workaround.Precaution));
        entry.Children.Add(Paragraph("If you turn it off: " + workaround.IfTurnedOff));
        return entry;
    }

    internal static TextBlock Paragraph(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(20, 4, 0, 0),
        Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)),
    };
}

/// <summary>
/// The question asked before a precaution is turned off: what will happen, in the entry's own words. Yes turns it off
/// (DialogResult true); No, Escape or closing the window keeps it on. A WPF window, not a message box, so the window
/// cases' startup hook can answer it from inside the process.
/// </summary>
internal sealed class ConfirmTurnOffWindow : Window
{
    public ConfirmTurnOffWindow(KnownWorkaround workaround)
    {
        Title = "Turn off a precaution";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Name = "ConfirmQuestion",
            Text = $"Turn off '{workaround.Title}'? {workaround.IfTurnedOff}",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(SettingsWindow.Paragraph("Why it is on: " + workaround.Defect));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var yes = new Button { Name = "ConfirmYes", Content = "Yes, turn it off", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        yes.Click += (_, _) => DialogResult = true;
        var no = new Button { Name = "ConfirmNo", Content = "No, keep it on", IsDefault = true, IsCancel = true, Padding = new Thickness(12, 4, 12, 4) };
        no.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(yes);
        buttons.Children.Add(no);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
