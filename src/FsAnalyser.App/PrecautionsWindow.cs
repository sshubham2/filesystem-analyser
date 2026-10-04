using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FsAnalyser.Core;

namespace FsAnalyser.App;

/// <summary>
/// The precautions the app takes against defects outside it (Core's <see cref="Workarounds"/>), one switch each, every one
/// listed from Core's list with its outside defect and where it lives, what the app does while it is on, and what turning
/// it off brings back. Each is on until the person turns it off, and turning one off asks first, with that consequence in
/// the question. Off restores the plain behaviour exactly; the next scan takes the switches as they are when this closes.
/// </summary>
internal sealed class PrecautionsWindow : Window
{
    public PrecautionsWindow(WorkaroundSwitches switches)
    {
        Switches = switches;
        Title = "Precautions";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "What this app does to avoid defects that are not its own: in Windows, a driver or another program it depends on. " +
                "Each is on unless you turn it off here; turning one off lasts until the app is closed.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });
        foreach (Workaround precaution in Workarounds.All)
            panel.Children.Add(Entry(precaution));

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, Padding = new Thickness(16, 4, 16, 4), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        Content = panel;
    }

    /// <summary>The switches as the person left them.</summary>
    public WorkaroundSwitches Switches { get; private set; }

    private UIElement Entry(Workaround precaution)
    {
        var box = new CheckBox
        {
            Content = new TextBlock { Text = precaution.Title, FontWeight = FontWeights.SemiBold },
            IsChecked = Switches.IsOn(precaution.Id),
        };
        box.Checked += (_, _) => Switches = Switches.With(precaution.Id, on: true);
        box.Unchecked += (_, _) =>
        {
            MessageBoxResult answer = MessageBox.Show(this,
                $"Turn off '{precaution.Title}'?\n\n{precaution.WhenOff}\n\nWhy it is on: {precaution.Defect}",
                "Turn off a precaution", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer == MessageBoxResult.Yes)
                Switches = Switches.With(precaution.Id, on: false);
            else
                box.IsChecked = true;
        };

        var entry = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        entry.Children.Add(box);
        entry.Children.Add(Paragraph("The defect: " + precaution.Defect));
        entry.Children.Add(Paragraph("What the app does: " + precaution.Precaution));
        entry.Children.Add(Paragraph("If you turn it off: " + precaution.WhenOff));
        return entry;
    }

    private static TextBlock Paragraph(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(20, 4, 0, 0),
        Foreground = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)),
    };
}
