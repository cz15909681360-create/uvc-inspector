# 架构与统计约定

## 结构

`UvcInspector.Core` 不依赖 UI：

- `Models.cs`：不可变设备、驱动模式、请求、实际流及检测结果；输入校验。
- `DeviceParser.cs`：解析设备 alternative name 与完整模式范围，忽略音频源。
- `PixelFormats.cs`：将样本位深、色度与存储字节区分；对未知布局不作猜测。
- `CaptureAnalyzer.cs`：原视频包大小、PTS、包时长、帧计数与输入流信息；有锁的实时快照。
- `ProcessRunner.cs`：无 shell 的参数列表、并发读取管道、取消/超时杀进程并等待退出。
- `FfmpegEngine.cs`：发现引擎、检查版本/DirectShow、枚举模式、执行测量与实时进度。
- `JpegFrameReader.cs` / `NativePreview.cs`：有大小限制的图像流分帧、预览超时及释放。

WPF 工程：

- `App.xaml`：共享配色和控件样式。
- `MainWindow.xaml`：可滚动的原生界面，设备设置、结果卡片、模式表、预览和诊断。
- `MainViewModel.cs`：任务状态、选择/测试协调、结果展示及报告导出；采集核心不操作控件。
- `Settings.cs`：当前用户设置，临时文件写入后替换。
- `MainWindow.xaml.cs`：生命周期及不访问设备的演示渲染检查。
- `MainWindow.Layout.cs`：可用空间分配、拖动/键盘调整、保存高度及实际布局检查。
- `BundledEngine.cs`：读取 EXE 中的引擎资源，按 SHA-256 管理每用户缓存；进程间文件锁与临时文件替换防止并发半成品，启动前校验并修复缓存。
- `EngineChecks.cs`：隔离的测试缓存、并发提取、损坏修复、四种格式的实测统计/JPEG 解码与取消；不访问摄像头。

## 测量

命令先固定 DirectShow 设备、格式、尺寸和帧率，然后使用 `-map 0:v:0 -an -c:v copy -f framecrc pipe:1`。framecrc 的 `packet_size` 是原视频包大小，输出文字量不是视频大小。帧计数从 `-progress pipe:2` 的 `frame=` 读取，与包数独立保存。两条管道并发读取，避免子进程阻塞。

媒体区间优先为 `max(PTS + duration) - min(PTS)`，再乘视频时间基。缺失包时长时，用观测到的相邻时间戳间隔估算末端并标记；缺少足够时间戳时，可回退至 FFmpeg 媒体进度。没有实测时间就保持未知，不填请求秒数。

framecrc 会对视频包计算校验和，因此有额外 CPU/内存读取开销。高尺寸/高帧率需结合机器性能评估；本工具不承诺零扰动捕获，也不依据采集帧率直接认定 USB 硬件掉帧。压缩视频不解码检验画面质量或逐帧完整性。

参考 [FFmpeg framecrc 文档](https://ffmpeg.org/ffmpeg-formats.html#framecrc)、[FFmpeg Streamcopy 文档](https://ffmpeg.org/ffmpeg.html#Streamcopy) 与 [DirectShow 文档](https://ffmpeg.org/ffmpeg-devices.html#dshow)。

## 任务与释放

UI 采集任务互斥，预览时禁止修改输入设置；开始刷新/测试前等待本程序预览退出。预览仅保留最新一帧供窗口定时绘制，避免 UI 任务队列积压。取消、超时和窗口关闭都会停止本程序启动的 FFmpeg 进程并等待退出。不会结束其他软件的 FFmpeg 进程。

## 当前边界

DirectShow 是 Windows 接口；本工程不支持 macOS/Linux GUI。不读取 USB 拓扑/协议流，也不自动确认 UVC 设备身份。未完整覆盖所有像素布局与驱动日志变体。未知信息保持未知，失败保留部分诊断。原作者的注释、构建历史与许可信息没有被恢复。
