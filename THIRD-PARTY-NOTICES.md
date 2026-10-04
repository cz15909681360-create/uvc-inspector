# 第三方组件

## .NET 10 / WPF

Windows x64 便携包以自包含方式发布，包含 Microsoft .NET 与 Windows Desktop / WPF 运行时。主要许可为 MIT，部分组件有额外通知。发行包 `licenses` 保存构建环境提供的许可与通知文件。

- [Runtime 源码与许可证](https://github.com/dotnet/runtime)
- [WPF 源码与许可证](https://github.com/dotnet/wpf)

## 内置 FFmpeg 8.1.3

2.1.0 的 Windows EXE 内嵌项目从 FFmpeg 8.1.3 原始源码构建的 Windows x64 独立命令行程序。运行时解出引擎并通过管道调用；不修改 FFmpeg 源码、不静态链接 FFmpeg 库到 C# 工程。

构建未启用 GPL、nonfree 或额外编解码库，适用 LGPL-2.1-or-later。包含 DirectShow、RAW/MJPEG/H.264/HEVC 解码、原视频包复制、缩放和 MJPEG 预览；不含 libx264/libx265 编码器或网络协议。应用自身源码仍按 MIT 发布。

发行包 `licenses/ffmpeg` 提供：

- 官方 `ffmpeg-8.1.3.tar.xz` 原始源码归档及 LGPL/FFmpeg 许可文件。
- `build-engine.sh`、实际 configure 命令、交叉编译器与 Debian 包版本记录。
- MinGW 和 GCC 原始版权信息，以及 PE 导入依赖记录。
- 内置可执行文件的 SHA-256、版本输出和实际许可输出。

源码归档固定 SHA-256：`7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3`。构建脚本校验下载内容；发布时与引擎一同提供源码，而不只提供上游链接。

官方资料：[FFmpeg 源码](https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz)、[FFmpeg 许可说明](https://ffmpeg.org/legal.html)、[MinGW-w64](https://www.mingw-w64.org/)、[GCC Runtime Library Exception](https://www.gnu.org/licenses/gcc-exception-3.1.html)。

用户明确指定的外部 FFmpeg 可能有不同配置与许可证，该文件不对外部安装授予许可。2.0.1 的公开发行包未包含 FFmpeg。
