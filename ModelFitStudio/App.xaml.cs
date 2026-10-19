using System.Windows;
using ModelFitStudio.Core;

namespace ModelFitStudio;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
// Qualified because the WinForms SDK's implicit usings are in scope too, and a bare
// "Application" is ambiguous between System.Windows and System.Windows.Forms.
public partial class App : System.Windows.Application
{
    private readonly AppInstance _instance = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var first = await _instance.AcquireAsync(() => Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is MainWindow window) window.RestoreFromTray();
        }));
        if (!first) { Shutdown(); return; }
        this.Exit += (_, _) => _instance.Dispose();

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
