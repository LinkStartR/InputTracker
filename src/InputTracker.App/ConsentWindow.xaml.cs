using System.Windows;

namespace InputTracker;

public partial class ConsentWindow : Window
{
    public ConsentWindow() => InitializeComponent();

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void LaterButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
