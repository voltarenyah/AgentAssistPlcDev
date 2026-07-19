using App.ViewModels;

namespace App;

public partial class MainWindow : System.Windows.Window
{
    private readonly MainViewModel viewModel;

    public MainWindow()
    {
        InitializeComponent();
        viewModel = new MainViewModel();
        DataContext = viewModel;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        await viewModel.InitializeAsync();
    }

    private async void OnClosed(object sender, System.EventArgs e)
    {
        await viewModel.DisposeAsync();
    }
}
