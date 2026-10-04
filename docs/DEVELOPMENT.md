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
./artifacts/UvcInspector-v2.1.0-win-x64/UvcInspector.exe --demo --capture ui.png --width 2048 --height 1232 --layout-check
```

测试项目是自带断言的控制台程序，使用 `dotnet run --project tests/UvcInspector.Tests -c Release` 执行；不要用 `dotnet test` 代替。失败会返回非零退出码。

硬件检查需要手动指定 `--integration --hardware`，当前夹具使用本机 `FHD Camera`，会短时打开相机，详见 [验证记录](../VERIFICATION.md)。CI 不执行硬件检查，也不将演示数据标记为真实采集。

## 打包

```powershell
./build.ps1 -Package
```

默认打包会把 `.tools/engine/ffmpeg.exe` 作为资源嵌入主 EXE，包含 .NET 运行时、完整说明和第三方材料。该引擎由 GitHub Actions 的 Ubuntu 交叉编译任务按 `scripts/build-engine.sh` 从校验过的 FFmpeg 8.1.3 原始源码生成；可从成功的 Actions 运行下载 `ffmpeg-windows` artifact 到 `.tools/engine`。

显式指定已完成的项目构建：

```powershell
./build.ps1 -Package -BundleEngine -EnginePath 'C:\engine\ffmpeg.exe' -EngineMaterialsPath 'C:\engine\licenses'
```

脚本要求对应源码/许可证、版本和 LGPL 配置完整，并在发布目录附上所有材料。不要把任意 Gyan full build 替代这个精简构建。单纯开发界面时可用 `-Package -WithoutEngine`，这类本地开发包需要外部引擎，不作为正式下载版。

内置引擎检查不使用 PATH 或已安装 FFmpeg，也不访问相机：

```powershell
./artifacts/UvcInspector-v2.1.0-win-x64/UvcInspector.exe --engine-check artifacts/engine-check.json --fixtures .tools/engine/fixtures
```

检查并发提取、隔离缓存损坏修复、DirectShow 能力、四种合成视频的复制测量/JPEG 解码和进程取消。测试缓存位于报告旁的 `engine-check-cache`，不修改用户实际引擎缓存；报告 Healthy 为 true、退出码为 0 表示通过。

版本号当前为 2.1.0。更改版本时同步应用 csproj、界面版本、报告版本、build.ps1 包名与 README 下载文件名。

## GitHub Actions

推送 main、Pull Request 或手动触发时，先在 Ubuntu 上交叉编译内置引擎，再在 Windows runner 上安装 .NET 10，执行核心检查、内置引擎检查和自包含打包，并检查默认、空设备、错误及大窗口布局。官方 Actions 固定到提交 SHA；任务只授予读取源码权限。

成功后，Actions 页面可下载包含内置 FFmpeg 和对应源码材料的构建结果和界面检查记录。工作流不会自行发布 Release，也不会打开真实相机。发布时使用验证过的构建包，并同时附源码和依赖说明。

截图模式显式分配请求尺寸的 WPF 画布，布局检查改变该画布的视口高度。这样即使 runner 的虚拟屏幕较小，也能检查大尺寸布局与真实控件事件；普通运行仍由实际窗口尺寸决定空间分配。
