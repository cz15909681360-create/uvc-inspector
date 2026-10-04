# 第三方组件

## .NET 10 / WPF

Windows x64 便携包以自包含方式发布，包含 Microsoft .NET 与 Windows Desktop / WPF 运行时。主要许可为 MIT，部分组件有额外通知。发行包 `licenses` 保存构建环境提供的许可与通知文件。

- [Runtime 源码与许可证](https://github.com/dotnet/runtime)
- [WPF 源码与许可证](https://github.com/dotnet/wpf)

## FFmpeg

FFmpeg 为独立子进程，不作为库链接进入应用。普通源码工程不包含 FFmpeg；带 `tools/ffmpeg.exe` 的本地便携包包含未修改的已安装 Gyan.dev 8.1.1 full build，其配置启用 GPL 和 version3。发行包保存原构建 LICENSE、README、版本/配置、SHA-256，并在可用时附上对应 FFmpeg 8.1.1 源码归档。

- [FFmpeg 许可说明](https://ffmpeg.org/legal.html)
- [FFmpeg 8.1.1 源码](https://ffmpeg.org/releases/ffmpeg-8.1.1.tar.xz)
- [所用发行构建及外部库说明](https://www.gyan.dev/ffmpeg/builds/)
- [发行方使用的外部库构建工程](https://github.com/m-ab-s/media-autobuild_suite)

FFmpeg 的附加编解码库各自有许可与源码要求。以上材料记录当前本地包依赖，不代表已完成公开分发全部依赖的许可审核；将引擎一并公开发布前需按实际构建整理完整的第三方材料，或者让用户使用已有的合规安装。

这些许可不替代本项目源代码的许可决定。
