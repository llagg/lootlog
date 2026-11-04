using System.ComponentModel;
using System.Windows;

namespace LootLogger;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MainViewModel _viewModel;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);
        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        _viewModel = new MainViewModel(settings);
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.InitializeAsync(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowWidth = ActualWidth;
            _settings.WindowHeight = ActualHeight;
        }

        _viewModel.Shutdown();
        base.OnClosing(e);
    }
}
