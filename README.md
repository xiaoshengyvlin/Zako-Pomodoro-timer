# Tomato Note Timer

轻量番茄钟 + 便签工具（WPF / .NET 8，Windows）。
L站佬友链接：https://linux.do/

## 主要功能

- 番茄倒计时：工作/休息阶段、循环次数、开始/暂停/重置
- 便签系统：多段文本、定时轮播、字体/字号/颜色配置
- 音频提醒：过程音频、工作结束音频、休息结束音频
- 托盘常驻：快捷操作、简洁模式、置顶/固定、网页任务
- 全局快捷键：6 项动作可自定义（默认均为空）

## 运行方式

1. 直接运行根目录 `TomatoNoteTimer.exe`（已打包好放在[![Release](https://img.shields.io/badge/Release-下载-blue?style=for-the-badge)](https://github.com/xiaoshengyvlin/Zako-Pomodoro-timer/releases/tag/v1.0.0)）。
2. 首次运行会自动创建运行时目录：`config`、`data`、`logs`、`audio`。
3. 内置预设音频会释放到 `audio\countdown_end\音乐预设.mp3`，不会写到根目录。

如需恢复初始化状态，删除以上运行时目录后重新启动即可。

## 体积说明

- **自包含单文件**（无需安装运行时）：体积较大（约 70MB+）
- **框架依赖单文件**（需安装 .NET 8 Desktop Runtime）：体积较小（约 3~6MB）

框架依赖单文件示例：

```powershell
dotnet publish .\TomatoNoteTimer\TomatoNoteTimer.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## 开发

环境要求：

- Windows 10/11
- .NET SDK 8.0+

构建命令：

```powershell
dotnet build .\TomatoNoteTimer\TomatoNoteTimer.csproj -c Release
```

## 仓库内容说明

```text
TomatoNoteTimer/        项目源码目录
icon.ico                图标源文件（用于编译时写入 EXE 并内嵌）
音乐预设.mp3            默认音频源文件（内嵌资源）
TomatoNoteTimer.exe     发布产物（非源码）
README.md               说明文档
LICENSE                 开源许可证
```
## ps.你说的游戏我也玩了，感觉一般，没有那么好玩，也可能是因为没有你......
