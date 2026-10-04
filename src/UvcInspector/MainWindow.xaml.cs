using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Diagnostics;
using System.Text.Json;

namespace UvcInspector;

public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    private bool mayClose;
    public MainWindow(string[] arguments)
    {
        InitializeComponent();
        bool demo = arguments.Contains("--demo");
        model = new MainViewModel(demo);
        DataContext = model;
        int captureIndex = Array.IndexOf(arguments, "--capture");
        int smokeIndex = Array.IndexOf(arguments, "--smoke-report");
        InitializeWorkspaceLayout(!demo && captureIndex < 0 && smokeIndex < 0);
        StringWriter? bindingErrors = null;
        TextWriterTraceListener? bindingListener = null;
        if (captureIndex >= 0)
        {
            if (!demo || captureIndex + 1 >= arguments.Length) throw new ArgumentException("截图需要 --demo --capture <png路径>。");
            WindowStartupLocation = WindowStartupLocation.Manual; Left = -20000; Top = -20000; ShowInTaskbar = false;
            int widthIndex = Array.IndexOf(arguments, "--width"), heightIndex = Array.IndexOf(arguments, "--height");
            if (widthIndex >= 0 && widthIndex + 1 < arguments.Length) Width = int.Parse(arguments[widthIndex + 1]);
            if (heightIndex >= 0 && heightIndex + 1 < arguments.Length) Height = int.Parse(arguments[heightIndex + 1]);
        }
        if (smokeIndex >= 0)
        {
            if (smokeIndex + 1 >= arguments.Length) throw new ArgumentException("--smoke-report requires a JSON path.");
            WindowStartupLocation = WindowStartupLocation.Manual; Left = -20000; Top = -20000; ShowInTaskbar = false;
        }
        if (captureIndex >= 0 || smokeIndex >= 0)
        {
            bindingErrors = new StringWriter();
            bindingListener = new TextWriterTraceListener(bindingErrors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingListener);
        }
        Loaded += async (_, _) =>
        {
            await model.InitializeAsync();
            if (arguments.Contains("--empty")) model.LoadEmptyDemo(false);
            if (arguments.Contains("--error-demo")) model.LoadEmptyDemo(true);
            if (smokeIndex >= 0)
            {
                UiSmokeResult? actions = arguments.Contains("--smoke-actions") ? await model.RunSmokeActionsAsync() : null;
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                bindingListener?.Flush();
                var errors = bindingErrors?.ToString() ?? "";
                bool healthy = string.IsNullOrWhiteSpace(errors) && (actions is null
                    ? model.StateLabel is "设备就绪" or "未发现视频设备"
                    : actions.NativePreviewRendered && actions.PreviewReleasedBeforeTest && actions.MeasurementCompleted && actions.ChangedParametersClearPreviousResult);
                var path = Path.GetFullPath(arguments[smokeIndex + 1]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(new { Healthy = healthy, model.StateLabel, model.StateDetail, model.EngineText,
                    DeviceCount = model.Devices.Count, ModeCount = model.Modes.Count, TestEnabled = model.TestCommand.CanExecute(null), PreviewEnabled = model.PreviewCommand.CanExecute(null),
                    model.Width, model.Height, model.Fps, Actions = actions, BindingErrors = errors }, new JsonSerializerOptions { WriteIndented = true }));
                if (bindingListener is not null) PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingListener);
                bindingListener?.Dispose(); bindingErrors?.Dispose();
                mayClose = true; Close();
                if (!healthy) Application.Current.Shutdown(3);
                return;
            }
            if (captureIndex >= 0)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                object? layoutCheck = arguments.Contains("--layout-check") ? await CheckWorkspaceLayoutAsync() : null;
                UpdateLayout();
                var content = (FrameworkElement)Content;
                var target = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                target.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
                var path = Path.GetFullPath(arguments[captureIndex + 1]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var output = File.Create(path)) encoder.Save(output);
                if (layoutCheck is not null)
                {
                    var layoutJson = JsonSerializer.Serialize(layoutCheck, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(path + ".layout.json", layoutJson);
                    using var layoutResult = JsonDocument.Parse(layoutJson);
                    if (!layoutResult.RootElement.GetProperty("Healthy").GetBoolean()) { Application.Current.Shutdown(4); return; }
                }
                bindingListener?.Flush();
                var bindingText = bindingErrors?.ToString() ?? "";
                File.WriteAllText(path + ".bindings.txt", bindingText);
                if (bindingListener is not null) PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingListener);
                bindingListener?.Dispose(); bindingErrors?.Dispose();
                if (!string.IsNullOrWhiteSpace(bindingText)) { Application.Current.Shutdown(2); return; }
                mayClose = true; Close();
            }
        };
        Closing += async (_, e) =>
        {
            if (mayClose) return;
            e.Cancel = true;
            if (!IsEnabled) return;
            IsEnabled = false;
            await model.CloseAsync();
            mayClose = true; Close();
        };
    }
}
