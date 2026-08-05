using System.Windows;

namespace InputTracker;

public partial class MainWindow : Window
{
    private readonly CollectionController _collection;
    private readonly DirectoryProbeResult _directory;

    public MainWindow(CollectionController collection, DirectoryProbeResult directory)
    {
        InitializeComponent();
        _collection = collection;
        _directory = directory;
        _collection.Changed += Collection_Changed;
        Closed += (_, _) => _collection.Changed -= Collection_Changed;
        Render(_collection.Current);
    }

    public event EventHandler? MinimizeToTrayRequested;
    public event EventHandler? ExitRequested;

    private void Collection_Changed(object? sender, CollectionSnapshot snapshot) => Render(snapshot);

    private void Render(CollectionSnapshot snapshot)
    {
        StatusLabel.Text = snapshot.Label;
        StatusExplanation.Text = snapshot.Explanation;
        DirectoryText.Text = $"数据位置：{_directory.DirectoryPath}";
        PauseButton.IsEnabled = snapshot.CanPause;
        ResumeButton.IsEnabled = snapshot.CanResume;
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e) => _collection.Pause();
    private void ResumeButton_Click(object sender, RoutedEventArgs e) => _collection.Resume();
    private void HideButton_Click(object sender, RoutedEventArgs e) => MinimizeToTrayRequested?.Invoke(this, EventArgs.Empty);
    private void ExitButton_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke(this, EventArgs.Empty);

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            MinimizeToTrayRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
