# 开发与发布

## 获取源码

在 Windows 上安装 Git 和 .NET 10 SDK，然后执行：

```powershell
git clone https://github.com/cz15909681360-create/uvc-inspector.git
cd uvc-inspector
./build.ps1 -Test
dotnet run --project src/UvcInspector -c Release
```

脚本使用项目或上级工作区的 `.tools/dotnet/dotnet.exe`，否则使用 PATH 的 `dotnet`。可用 `-DotnetPath 'C:\path\dotnet.exe'` 显式指定 SDK。项目没有额外的 NuGet 应用库依赖；首次构建仍可能需要获取 .NET SDK/Windows 目标组件。

## 工程结构

```text
assets/                     应用图标源文件
src/UvcInspector.Core/      设备解析、参数校验、测量与进程管理
src/UvcInspector/           WPF 窗口、状态协调、预览与报告
tests/UvcInspector.Tests/   可执行检查程序
docs/                      架构、开发、排错与演示截图
.github/                   CI 与问题模板
build.ps1                  构建、验证和便携包脚本
```

`MainWindow.Layout.cs` 管理拖动高度、自动空间分配、键盘操作、保存设置及真实 WPF 布局检查。`.gitignore` 排除 bin/obj、引擎、打包结果与本地工具。

## 验证层级

```powershell
# 核心检查，不访问相机，不需要 FFmpeg
./build.ps1 -Test

# FFmpeg 生成样本检查，不访问相机；先把完整 FFmpeg 加入 PATH
./build.ps1 -Integration

# 自包含发布程序的界面检查，不访问相机
./build.ps1 -Package
./artifacts/UvcInspector-v2.0.1-win-x64/UvcInspector.exe --demo --capture ui.png --width 2048 --height 1232 --layout-check
```

测试项目是自带断言的控制台程序，使用 `dotnet run --project tests/UvcInspector.Tests -c Release` 执行；不要用 `dotnet test` 代替。失败会返回非零退出码。

硬件检查需要手动指定 `--integration --hardware`，当前夹具使用本机 `FHD Camera`，会短时打开相机，详见 [验证记录](../VERIFICATION.md)。CI 不执行硬件检查，也不将演示数据标记为真实采集。

## 打包

```powershell
./build.ps1 -Package
```

生成自包含的 Windows x64 单文件程序、完整说明、MIT 与 .NET 通知，以及源码 ZIP。公开下载包不包含 FFmpeg，用户独立安装引擎。不要把 ZIP、EXE 或 SDK 提交到 Git 源码仓库。

本地确需复制既有 FFmpeg 时可使用 `-BundleEngine -EnginePath 'C:\path\ffmpeg.exe'`。该选项仅用于有对应第三方材料的本地包；不要将本机引擎包当作公开下载包。公开打包应在干净克隆中运行，避免旧的 `tools` 文件夹混入。

版本号当前为 2.0.1。更改版本时同步应用 csproj、界面版本、报告版本、build.ps1 包名与 README 下载文件名。

## GitHub Actions

推送 main、Pull Request 或手动触发时，在 Windows runner 上安装 .NET 10，执行核心检查和自包含打包，并检查默认、空设备、错误及大窗口布局。官方 Actions 固定到提交 SHA；任务只授予读取源码权限。

成功后，Actions 页面可下载不含 FFmpeg 的构建结果和界面检查记录。工作流不会自行发布 Release，也不会打开真实相机。发布时使用验证过的构建包，并同时附源码和依赖说明。

截图模式显式分配请求尺寸的 WPF 画布，布局检查改变该画布的视口高度。这样即使 runner 的虚拟屏幕较小，也能检查大尺寸布局与真实控件事件；普通运行仍由实际窗口尺寸决定空间分配。
