using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using UvcInspector.Core;
using CaptureMode = UvcInspector.Core.CaptureMode;

namespace UvcInspector;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private FfmpegEngine? engine;
    private CancellationTokenSource? operationCancel, previewCancel;
    private Task? previewTask;
    private byte[]? latestFrame;
    private readonly DispatcherTimer previewTimer;
    private readonly SemaphoreSlim previewLock = new(1, 1);
    private readonly bool demo;
    private bool isBusy, isPreviewing, closing;
    private VideoDevice? selectedDevice;
    private CaptureMode? selectedMode;
    private MeasurementResult? result;
    private CaptureRequest? resultRequest;
    private DateTimeOffset? measurementCompletedAt;
    private string width = "1920", height = "1080", fps = "30", seconds = "5";
    private string stateLabel = "正在初始化", stateDetail = "检查检测引擎与设备…", logText = "", engineText = "检查 FFmpeg…";
    private Brush stateBrush = new SolidColorBrush(Color.FromRgb(37, 99, 235));
    private ImageSource? previewImage;

    public MainViewModel(bool demo)
    {
        this.demo = demo;
        RefreshCommand = new UiCommand(RefreshAsync, () => !IsBusy && !closing && !demo);
        TestCommand = new UiCommand(TestAsync, () => !IsBusy && SelectedMode is not null && engine is not null && !closing && !demo);
        PreviewCommand = new UiCommand(TogglePreviewAsync, () => !IsBusy && SelectedMode is not null && engine is not null && !closing && !demo);
        StopCommand = new UiCommand(StopAsync, () => (IsBusy || IsPreviewing) && !closing);
        LocateEngineCommand = new UiCommand(LocateEngineAsync, () => !IsBusy && !closing && !demo);
        ExportCommand = new UiCommand(ExportAsync, () => !IsBusy && result is not null && result.Status != CaptureStatus.Running && !demo);
        HelpCommand = new UiCommand(() =>
        {
            MessageBox.Show("1. 刷新设备，选择驱动声明的模式。\n2. 设置测试秒数，点击开始测试。\n3. 查看实际编码、像素格式、码率与帧率，异常见诊断记录。\n\n预览会解码并缩放，只用于观察画面；测试前自动停止预览。测试复制原视频包，不重编码。\n\n视频数据码率不等于 USB 总线占用。驱动声明范围不保证所有尺寸/帧率组合均可采集。\n\n蓝色：可操作/采集中；绿色：完成；琥珀色：提醒/取消；红色：失败。状态也有文字说明。", "UVC 检测 · 使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
            return Task.CompletedTask;
        });
        previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(85) };
        previewTimer.Tick += (_, _) => DisplayLatestPreview();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<VideoDevice> Devices { get; } = [];
    public ObservableCollection<CaptureMode> Modes { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand LocateEngineCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand HelpCommand { get; }
    public bool IsBusy { get => isBusy; private set { isBusy = value; Changed(); Changed(nameof(CanEdit)); Changed(nameof(TestButtonText)); CommandManager.InvalidateRequerySuggested(); } }
    public bool CanEdit => !IsBusy && !IsPreviewing && !demo && !closing;
    public bool IsPreviewing { get => isPreviewing; private set { isPreviewing = value; Changed(); Changed(nameof(CanEdit)); Changed(nameof(PreviewButtonText)); Changed(nameof(PreviewCaption)); CommandManager.InvalidateRequerySuggested(); } }
    public string TestButtonText => IsBusy ? "正在处理…" : "开始测试";
    public string PreviewButtonText => IsPreviewing ? "关闭预览" : "实时预览";
    public string PreviewCaption => demo ? "演示画面 · 未连接摄像头" : IsPreviewing ? "解码预览 · 与码流测试分开运行" : "选择模式，点击实时预览";
    public string Width { get => width; set { if (width == value) return; width = value; Changed(); InvalidatePreviousResult(); } }
    public string Height { get => height; set { if (height == value) return; height = value; Changed(); InvalidatePreviousResult(); } }
    public string Fps { get => fps; set { if (fps == value) return; fps = value; Changed(); InvalidatePreviousResult(); } }
    public string Seconds { get => seconds; set { if (seconds == value) return; seconds = value; Changed(); InvalidatePreviousResult(); } }
    public string EngineText { get => engineText; private set { engineText = value; Changed(); } }
    public string StateLabel { get => stateLabel; private set { stateLabel = value; Changed(); } }
    public string StateDetail { get => stateDetail; private set { stateDetail = value; Changed(); } }
    public Brush StateBrush { get => stateBrush; private set { stateBrush = value; Changed(); } }
    public string LogText { get => logText; private set { logText = value; Changed(); } }
    public ImageSource? PreviewImage { get => previewImage; private set { previewImage = value; Changed(); Changed(nameof(PreviewPlaceholderVisibility)); } }
    public Visibility PreviewPlaceholderVisibility => PreviewImage is null ? Visibility.Visible : Visibility.Collapsed;
    public VideoDevice? SelectedDevice
    {
        get => selectedDevice;
        set
        {
            if (selectedDevice == value) return;
            selectedDevice = value; Changed();
            if (!IsBusy && !demo && !closing) _ = LoadSelectedDeviceAsync();
        }
    }
    public CaptureMode? SelectedMode
    {
        get => selectedMode;
        set
        {
            if (selectedMode != value) InvalidatePreviousResult();
            selectedMode = value; Changed(); Changed(nameof(SelectedFormatText)); Changed(nameof(ModeNote));
            if (value is not null)
            {
                Width = value.MinWidth.ToString(CultureInfo.InvariantCulture);
                Height = value.MinHeight.ToString(CultureInfo.InvariantCulture);
                Fps = (value.MinWidth == value.MaxWidth && value.MinHeight == value.MaxHeight ? value.MaxFps : value.MinFps).ToString("0.###", CultureInfo.InvariantCulture);
            }
            CommandManager.InvalidateRequerySuggested();
            if (IsPreviewing && !IsBusy) _ = StopPreviewForSelectionAsync();
        }
    }
    public string SelectedFormatText => SelectedMode is null ? "选择右侧支持模式" : $"{SelectedMode.Encoding} · {SelectedMode.PixelLabel}";
    public string ModeNote => SelectedMode?.RangeNote ?? "设备模式由驱动声明，测试验证实际输出。";
    public string ModeCountText => $"{Modes.Count} 个声明模式";
    public string CodecText => result?.Stream.CodecLabel ?? "—";
    public string PixelText => result is null ? "待实际采集识别" : result.Stream.PixelFormat.ToUpperInvariant();
    public string ChromaText => result?.Stream.Chroma ?? "—";
    public string DepthText => result?.Stream.BitDepth ?? "—";
    public string RateText => Metric(result?.Mbps, "0.00");
    public string FpsText => Metric(result?.ActualFps, "0.00");
    public string ReferenceText => Metric(result?.ReferenceMbps, "0.0");
    public string ReferenceHint => result?.Stream.Codec is "h264" or "hevc" or "mjpeg" ? "压缩流无固定参考值" : "按实际像素存储布局与标称帧率";
    public string SizeText => result?.Stream.Width > 0 ? $"{result.Stream.Width} × {result.Stream.Height}" : "待采集";
    public string FrameText => result is null ? "尚无数据" : $"{result.Frames:N0} 帧计数 · {result.Packets:N0} 视频包";
    public string DurationText => result?.MediaSeconds is { } s ? $"{s:0.000} s 媒体区间" : "等待时间戳";
    public string BytesText => result is null ? "尚无数据" : $"{result.VideoBytes / 1_048_576.0:0.00} MiB 视频数据";
    public string TimingText => result is null ? "测试后展示统计依据" : $"{result.TimingSource} · 墙钟 {result.WallSeconds:0.00} s";
    public string WarningText => result is null ? "结果来自采集端视频数据，不包含 USB 协议开销。" : result.Warnings.Count > 0 ? string.Join("\n", result.Warnings) : "视频数据统计有效；USB 总线带宽与硬件掉帧未在此测量。";
    public string ResultLabel => result is null ? "等待测量" : result.Status switch { CaptureStatus.Running => "实时统计", CaptureStatus.Completed => "完整结果", CaptureStatus.Cancelled => "已取消 · 部分数据", _ => "诊断数据 · 测试未通过" };
    public double ProgressPercent => result?.MediaSeconds is { } s && double.TryParse(Seconds, out var target) && target > 0 ? Math.Clamp(s / target * 100, 0, 100) : 0;
    private static string Metric(double? value, string format) => value.HasValue ? value.Value.ToString(format, CultureInfo.CurrentCulture) : "—";

    public async Task InitializeAsync()
    {
        if (demo) { LoadDemo(); return; }
        await RefreshAsync();
    }

    private async Task RefreshAsync() => await RunOperationAsync(async token =>
    {
        SetState("检查引擎", "正在查找 FFmpeg 并枚举 DirectShow 视频源。", "#2563EB");
        var preferred = Settings.Load().FfmpegPath;
        string? path = FfmpegEngine.FindExecutable(preferred);
        if (path is null) throw new InvalidOperationException("未找到 FFmpeg。请点击“选择引擎”指定 ffmpeg.exe，或使用包含 tools 文件夹的完整便携包。");
        engine = new FfmpegEngine(path);
        var info = await engine.InspectAsync(token);
        if (!info.DirectShowAvailable) { engine = null; throw new InvalidOperationException("该 FFmpeg 没有 DirectShow 支持，请选择完整 Windows 构建。"); }
        EngineText = info.Version.Split(" Copyright", StringSplitOptions.None)[0];
        AppendLog($"引擎路径：{path}");
        var devices = await engine.DevicesAsync(token);
        var previous = SelectedDevice?.Identifier;
        Devices.Clear(); Modes.Clear(); SelectedMode = null;
        foreach (var device in devices) Devices.Add(device);
        SelectedDevice = Devices.FirstOrDefault(x => x.Identifier == previous) ?? Devices.FirstOrDefault();
        Changed(nameof(ModeCountText));
        if (SelectedDevice is null) SetState("未发现视频设备", "请连接摄像头或采集卡，再点击刷新设备。", "#B45309");
        else await ReadModesAsync(token);
    });

    private async Task LoadSelectedDeviceAsync() => await RunOperationAsync(ReadModesAsync);

    private async Task ReadModesAsync(CancellationToken cancellation)
    {
        Modes.Clear(); SelectedMode = null;
        SetResult(null);
        Changed(nameof(ModeCountText));
        if (SelectedDevice is null || engine is null) return;
        SetState("读取支持模式", SelectedDevice.Label, "#2563EB");
        foreach (var mode in await engine.ModesAsync(SelectedDevice, cancellation)) Modes.Add(mode);
        SelectedMode = Modes.FirstOrDefault(x => x.MinWidth == 1920 && x.MinHeight == 1080 && x.MaxFps >= 30) ?? Modes.FirstOrDefault();
        Changed(nameof(ModeCountText));
        SetState("设备就绪", $"{SelectedDevice.Label} · {Modes.Count} 个驱动声明模式", "#047857");
        AppendLog($"读取设备：{SelectedDevice.Label}；{Modes.Count} 个模式。USB/UVC 身份未单独验证。");
    }

    private CaptureRequest Request()
    {
        if (SelectedDevice is null || SelectedMode is null) throw new ArgumentException("请先选择设备与支持模式。");
        if (!int.TryParse(Width, out int w) || !int.TryParse(Height, out int h) || !int.TryParse(Seconds, out int sec)
            || !double.TryParse(Fps, NumberStyles.Float, CultureInfo.InvariantCulture, out double rate))
            throw new ArgumentException("尺寸与测试秒数需要整数，帧率可输入小数，例如 29.97。");
        var request = new CaptureRequest(SelectedDevice, SelectedMode.Kind, SelectedMode.Format, w, h, rate, sec);
        request.Validate();
        if (w < SelectedMode.MinWidth || w > SelectedMode.MaxWidth || h < SelectedMode.MinHeight || h > SelectedMode.MaxHeight
            || rate < SelectedMode.MinFps - .001 || rate > SelectedMode.MaxFps + .001)
            throw new ArgumentException("当前参数超出所选模式的驱动声明范围，请重新选择模式或调整参数。");
        return request;
    }

    private async Task TestAsync() => await RunOperationAsync(async token =>
    {
        var request = Request();
        resultRequest = request;
        SetResult(null);
        SetState("正在采集", $"{request.Format.ToUpperInvariant()} · {request.Width}×{request.Height} @ {request.Fps:0.###} fps · {request.Seconds} s", "#2563EB");
        AppendLog($"开始测试：{SelectedDevice!.Label}，{StateDetail}");
        var progress = new Progress<MeasurementResult>(value =>
        {
            if (!closing && IsBusy && result?.Status is not (CaptureStatus.Completed or CaptureStatus.Failed or CaptureStatus.Cancelled or CaptureStatus.TimedOut)) SetResult(value);
        });
        var measured = await engine!.MeasureAsync(request, progress, token);
        measurementCompletedAt = DateTimeOffset.Now;
        SetResult(measured);
        var label = measured.Status switch { CaptureStatus.Completed => "测试完成", CaptureStatus.Cancelled => "已取消", CaptureStatus.TimedOut => "采集超时", _ => "测试未通过" };
        SetState(label, measured.Message, measured.Success ? "#047857" : measured.Status == CaptureStatus.Cancelled ? "#B45309" : "#DC2626");
        AppendLog(measured.Message);
        AppendLog($"{measured.Frames} 帧计数，{measured.Packets} 个视频包，{measured.VideoBytes} 字节，媒体区间 {measured.MediaSeconds?.ToString("0.000") ?? "未知"} s。");
        foreach (var warning in measured.Warnings) AppendLog("提醒：" + warning);
        AppendLog("—— FFmpeg 诊断 ——\n" + string.Join('\n', measured.Log));
    });

    private async Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || closing) return;
        IsBusy = true;
        operationCancel = new CancellationTokenSource();
        try { await StopPreviewAsync(); await action(operationCancel.Token); }
        catch (OperationCanceledException) { SetState("操作已取消", "后台任务已停止。", "#B45309"); }
        catch (Exception e) { SetState("操作未完成", FriendlyError(e.Message), "#DC2626"); AppendLog(e.Message); }
        finally { operationCancel.Dispose(); operationCancel = null; IsBusy = false; }
    }

    private static string FriendlyError(string message)
    {
        if (message.Contains("Could not run graph", StringComparison.OrdinalIgnoreCase) || message.Contains("already in use", StringComparison.OrdinalIgnoreCase))
            return "相机可能被其他应用占用。请关闭 OBS、会议软件或其他预览后重试。";
        return message.Split('\n')[0];
    }

    private async Task TogglePreviewAsync()
    {
        if (IsPreviewing) { await StopPreviewAsync(); SetState("预览已关闭", "设备已释放，可以开始测试。", "#2563EB"); return; }
        try
        {
            await StopPreviewAsync();
            if (IsBusy || closing) return;
            var request = Request();
            previewCancel = new CancellationTokenSource();
            IsPreviewing = true; PreviewImage = null;
            previewTimer.Start();
            SetState("预览启动中", "等待相机画面；测试会自动关闭预览。", "#2563EB");
            previewTask = PreviewLoopAsync(request, previewCancel.Token);
            await Task.CompletedTask;
        }
        catch (Exception e) { SetState("预览未打开", FriendlyError(e.Message), "#DC2626"); AppendLog(e.Message); }
    }

    private async Task PreviewLoopAsync(CaptureRequest request, CancellationToken token)
    {
        try
        {
            await NativePreview.RunAsync(engine!.Executable, request, frame => Interlocked.Exchange(ref latestFrame, frame), token);
            if (!token.IsCancellationRequested && !closing) SetState("预览已结束", "相机已停止输出画面。", "#B45309");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!token.IsCancellationRequested && !closing) { SetState("预览失败", FriendlyError(e.Message), "#DC2626"); AppendLog(e.Message); } }
        finally
        {
            previewTimer.Stop(); IsPreviewing = false;
            Interlocked.Exchange(ref latestFrame, null); PreviewImage = null;
        }
    }

    private void DisplayLatestPreview()
    {
        var bytes = Interlocked.Exchange(ref latestFrame, null);
        if (bytes is null) return;
        try
        {
            using var memory = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = memory; image.EndInit(); image.Freeze();
            PreviewImage = image;
            if (StateLabel == "预览启动中") SetState("实时预览", "画面已解码并缩放，仅用于观察。", "#2563EB");
        }
        catch (Exception e) when (e is NotSupportedException or IOException or ArgumentException)
        { operationCancel?.Cancel(); previewCancel?.Cancel(); AppendLog("预览图像无法显示：" + e.Message); }
    }

    private async Task StopPreviewAsync()
    {
        await previewLock.WaitAsync();
        try
        {
            if (previewCancel is null) return;
            await previewCancel.CancelAsync();
            if (previewTask is not null) await previewTask;
            previewCancel.Dispose(); previewCancel = null; previewTask = null;
            IsPreviewing = false; PreviewImage = null;
        }
        finally { previewLock.Release(); }
    }
    private async Task StopPreviewForSelectionAsync() { await StopPreviewAsync(); SetState("预览已关闭", "模式已改变，请重新开启预览或测试。", "#2563EB"); }
    private async Task StopAsync() { if (IsBusy) operationCancel?.Cancel(); else { await StopPreviewAsync(); SetState("预览已关闭", "设备已释放。", "#2563EB"); } }

    private async Task LocateEngineAsync()
    {
        var dialog = new OpenFileDialog { Title = "选择 FFmpeg 检测引擎", Filter = "FFmpeg (ffmpeg.exe)|ffmpeg.exe|可执行程序 (*.exe)|*.exe" };
        if (dialog.ShowDialog() != true) return;
        try { (Settings.Load() with { FfmpegPath = dialog.FileName }).Save(); await RefreshAsync(); }
        catch (Exception e) { SetState("设置未保存", e.Message, "#DC2626"); }
    }

    private Task ExportAsync()
    {
        if (result is null) return Task.CompletedTask;
        var dialog = new SaveFileDialog { Title = "导出检测报告", Filter = "JSON 报告 (*.json)|*.json|CSV 汇总 (*.csv)|*.csv", FileName = $"UVC检测-{DateTime.Now:yyyyMMdd-HHmmss}", AddExtension = true };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        try
        {
            var report = new { AppVersion = "2.0.1", MeasurementCompletedAt = measurementCompletedAt, ExportedAt = DateTimeOffset.Now, Engine = engine?.Executable, Device = resultRequest?.Device, Request = resultRequest, Result = result,
                MeasurementScope = "采集端视频包有效字节与媒体时间戳；不包含 USB 协议开销；压缩包不解码验证。" };
            if (Path.GetExtension(dialog.FileName).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var data = new[] { new[] { "状态", "设备", "编码", "像素格式", "宽", "高", "帧计数", "视频包数", "视频字节", "媒体秒数", "Mbps", "采集FPS", "提醒" },
                    new[] { result.Status.ToString(), resultRequest?.Device.Label ?? "", result.Stream.Codec, result.Stream.PixelFormat, result.Stream.Width.ToString(), result.Stream.Height.ToString(),
                        result.Frames.ToString(), result.Packets.ToString(), result.VideoBytes.ToString(), result.MediaSeconds?.ToString(CultureInfo.InvariantCulture) ?? "", result.Mbps?.ToString(CultureInfo.InvariantCulture) ?? "",
                        result.ActualFps?.ToString(CultureInfo.InvariantCulture) ?? "", string.Join("; ", result.Warnings) } };
                File.WriteAllLines(dialog.FileName, data.Select(row => string.Join(',', row.Select(CsvCell))), new UTF8Encoding(true));
            }
            else File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }), new UTF8Encoding(false));
            AppendLog("报告已导出：" + dialog.FileName);
        }
        catch (Exception e) { SetState("导出未完成", e.Message, "#DC2626"); }
        return Task.CompletedTask;
    }

    private static string CsvCell(string value)
    {
        // CSV may be opened in spreadsheets. Treat device/log text as text, not formulas.
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public async Task CloseAsync()
    {
        closing = true;
        Changed(nameof(CanEdit)); CommandManager.InvalidateRequerySuggested();
        operationCancel?.Cancel();
        await StopPreviewAsync();
        while (IsBusy) await Task.Delay(40);
        previewTimer.Stop();
    }

    private void SetState(string label, string detail, string color)
    { StateLabel = label; StateDetail = detail; StateBrush = (Brush)new BrushConverter().ConvertFromString(color)!; }
    private void SetResult(MeasurementResult? value)
    {
        result = value;
        foreach (var name in new[] { nameof(CodecText), nameof(PixelText), nameof(ChromaText), nameof(DepthText), nameof(RateText), nameof(FpsText), nameof(ReferenceText), nameof(ReferenceHint),
            nameof(SizeText), nameof(FrameText), nameof(DurationText), nameof(BytesText), nameof(TimingText), nameof(WarningText), nameof(ResultLabel), nameof(ProgressPercent) }) Changed(name);
        CommandManager.InvalidateRequerySuggested();
    }
    private void InvalidatePreviousResult()
    {
        if (demo || IsBusy || result is null) return;
        SetResult(null); resultRequest = null; measurementCompletedAt = null;
        SetState("参数已变更", "请按当前参数重新测试；之前的统计仍保留在诊断记录中。", "#B45309");
    }
    private void AppendLog(string text)
    {
        var updated = LogText + $"[{DateTime.Now:HH:mm:ss}] {text}\n";
        LogText = updated.Length > 30_000 ? updated[^30_000..] : updated;
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void LoadDemo()
    {
        Devices.Add(new("演示 · USB Camera", "demo-only", 0)); SelectedDevice = Devices[0];
        foreach (var mode in new[] { new CaptureMode("vcodec", "h264", 1920, 1080, 1920, 1080, 30, 30), new CaptureMode("vcodec", "mjpeg", 1920, 1080, 1920, 1080, 60, 60),
            new CaptureMode("pixel_format", "nv12", 1920, 1080, 1920, 1080, 30, 30), new CaptureMode("pixel_format", "yuyv422", 1280, 720, 1280, 720, 30, 30) }) Modes.Add(mode);
        SelectedMode = Modes[0]; Changed(nameof(ModeCountText));
        EngineText = "界面演示 · 未访问摄像头";
        var stream = new StreamInfo("h264", "yuv420p", 1920, 1080, 30, "4:2:0", "8-bit", "演示数据");
        SetResult(new(CaptureStatus.Completed, stream, 150, 150, 5_312_500, 5, "视频包时间戳＋包时长", 5.22, 8.5, 30, null,
            "界面演示数据，不代表真实采集。", ["当前为界面演示；所有结果均为示例。"], [], 0));
        SetState("界面演示", "示例结果与画面用于检查布局，未访问真实摄像头。", "#2563EB");
        PreviewImage = DemoPreview.Create();
        AppendLog("界面演示 · 原生 WPF 窗口；摄像头采集未启动。");
        AppendLog("编码与像素格式独立展示；颜色配合明确文字状态。");
    }

    public void LoadEmptyDemo(bool error)
    {
        Devices.Clear(); Modes.Clear(); SelectedDevice = null; SelectedMode = null; SetResult(null); PreviewImage = null;
        Changed(nameof(ModeCountText));
        SetState(error ? "引擎不可用" : "未发现视频设备", error ? "请点击“选择引擎”指定带 DirectShow 支持的 ffmpeg.exe。" : "请连接摄像头或采集卡，再点击刷新设备。", error ? "#DC2626" : "#B45309");
        EngineText = "界面演示 · 未执行硬件检测";
    }

    /// <summary>Opt-in local release check. Observe native preview without writing camera images.</summary>
    public async Task<UiSmokeResult> RunSmokeActionsAsync()
    {
        if (demo || engine is null || SelectedDevice?.Name != "FHD Camera")
            throw new InvalidOperationException("此硬件 UI 检查需要本机 FHD Camera。");
        SelectedMode = Modes.FirstOrDefault(x => x.Kind == "pixel_format" && x.Format == "nv12" && x.MinWidth == 640 && x.MinHeight == 360)
            ?? throw new InvalidOperationException("所需 UI 测试模式不可用。");
        Seconds = "2";
        await TogglePreviewAsync();
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (PreviewImage is null && IsPreviewing && wait.Elapsed.TotalSeconds < 15) await Task.Delay(100);
        bool rendered = PreviewImage is not null;
        await TestAsync();
        var actual = result;
        bool released = !IsPreviewing;
        bool valid = actual?.Success == true;
        Seconds = "3";
        bool invalidated = result is null && RateText == "—" && StateLabel == "参数已变更";
        return new(rendered, released, valid, invalidated, actual?.Stream.Codec, actual?.Stream.PixelFormat, actual?.Mbps, actual?.ActualFps);
    }
}

public sealed record UiSmokeResult(bool NativePreviewRendered, bool PreviewReleasedBeforeTest, bool MeasurementCompleted,
    bool ChangedParametersClearPreviousResult, string? Codec, string? PixelFormat, double? Mbps, double? Fps);
