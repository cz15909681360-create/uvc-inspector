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
            if (e.Args.Contains("--capture") || e.Args.Contains("--engine-check")) { Shutdown(1); return; }
            MessageBox.Show(eventArgs.Exception.Message, "UVC 检测 · 操作未完成", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        int engineCheck = Array.IndexOf(e.Args, "--engine-check");
        if (engineCheck >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = CheckEngineAndExitAsync();
            return;
        }
        var window = new MainWindow(e.Args);
        MainWindow = window;
        window.Show();

        async Task CheckEngineAndExitAsync()
        {
            if (engineCheck + 1 >= e.Args.Length) { Shutdown(6); return; }
            int fixturesIndex = Array.IndexOf(e.Args, "--fixtures");
            string fixtures = fixturesIndex >= 0 && fixturesIndex + 1 < e.Args.Length ? e.Args[fixturesIndex + 1] : ".tools/engine/fixtures";
            bool success = await EngineChecks.RunAsync(e.Args[engineCheck + 1], fixtures);
            Shutdown(success ? 0 : 6);
        }
    }
}
