using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ChestCounter;

/// <summary>App-styled replacement for MessageBox.</summary>
public partial class ConfirmDialog : Window
{
    ConfirmDialog()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Loaded += (_, _) =>
        {
            // Small pop-in so it feels part of the app.
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
            var grow = new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
            CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        };
    }

    /// <summary>Asks a yes/no question. Returns true if the user confirmed.</summary>
    /// <param name="danger">Shows the confirm button in red for things that can't be undone.</param>
    public static bool Ask(Window? owner, string title, string message, string confirmText, bool danger = false) =>
        Open(owner, title, message, confirmText, showCancel: true, danger);

    /// <summary>Shows a message with a single OK button.</summary>
    public static void Tell(Window? owner, string title, string message) =>
        Open(owner, title, message, "OK", showCancel: false, danger: false);

    static bool Open(Window? owner, string title, string message, string confirmText, bool showCancel, bool danger)
    {
        var dialog = new ConfirmDialog();
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.OkButton.Content = confirmText;
        dialog.CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        if (danger)
        {
            var red = new SolidColorBrush(Color.FromRgb(0xF0, 0x5D, 0x5D));
            dialog.OkButton.Background = red;
            dialog.OkButton.BorderBrush = red;
            dialog.OkButton.Foreground = Brushes.White;
        }

        if (owner != null && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }
        return dialog.ShowDialog() == true;
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
