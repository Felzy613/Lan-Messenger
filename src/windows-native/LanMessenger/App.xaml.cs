using LanMessenger.UI;
using Microsoft.UI.Xaml;

namespace LanMessenger;

public partial class App : Application
{
    public MainWindow? MainWindow { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
