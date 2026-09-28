using System.IO;
using System.Windows;

namespace TeXeed.App;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "TeXeed", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var window = new MainWindow(e.Args.FirstOrDefault(a => a.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)));
        MainWindow = window;
        window.Show();
    }
}
