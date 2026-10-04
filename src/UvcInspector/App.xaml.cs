using System.Windows;

namespace UvcInspector;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            try
            {
                System.IO.Directory.CreateDirectory(Settings.DirectoryPath);
                System.IO.File.AppendAllText(System.IO.Path.Combine(Settings.DirectoryPath, "errors.log"), $"{DateTimeOffset.Now:O}\n{eventArgs.Exception}\n");
            }
            catch { }
            if (e.Args.Contains("--capture")) { Shutdown(1); return; }
            MessageBox.Show(eventArgs.Exception.Message, "UVC 检测 · 操作未完成", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();
    }
}
