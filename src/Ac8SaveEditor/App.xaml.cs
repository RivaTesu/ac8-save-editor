using System.Windows;

namespace Ac8SaveEditor;

public partial class App : Application
{
    void App_Startup(object sender, StartupEventArgs e)
    {
        Window w = e.Args.Contains("--zero") ? new ZeroWindow(e.Args.FirstOrDefault(a => a.EndsWith(".sav", StringComparison.OrdinalIgnoreCase))) : new MainWindow();
        MainWindow = w;
        w.Show();
    }
}
