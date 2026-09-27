using System.Windows;

namespace InputTracker;

public partial class GamepadConfirmationWindow : Window
{
    public GamepadConfirmationWindow(string title, string message, string confirmText, string cancelText)
    {
        InitializeComponent();
        DialogTitleText.Text = title;
        DialogMessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
