# FlankNote

<div align="center">
  <img src="./icon.png" width="100" />
</div>

_FlankNote_ 是一个**Windows**屏幕边缘的轻量便签应用。

_FlankNote_ is a lightweight sticky-note app that sits on the edge of your Windows screen.

## 致谢与许可

FlankNote 的部分功能、交互设计和实现思路移植自 macOS 应用 [Noty](https://github.com/aimen08/noty)。本项目并非 Noty 的完整复刻；其 Windows 代码包含针对本项目的重新实现和新增功能。

## Attribution and License

Parts of FlankNote's functionality, interaction design, and implementation are adapted from the macOS app [Noty](https://github.com/aimen08/noty). FlankNote is not a complete reproduction of Noty; its Windows codebase includes project-specific reimplementation and additional features. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for license details.

## 实现

- 使用 **.NET 10、C# 和 WPF** 构建 Windows 桌面界面，窗口布局、主题和动画主要通过代码实现；Windows Forms 仅用于系统托盘、显示器信息和文件保存对话框。
- 通过 **Win32 API 互操作**处理窗口置顶、命中测试和屏幕边缘交互，并使用 WPF 计时器驱动纸签栏展开、收起及过渡动画。

## Implementation
- Use .NET 10, C#, and WPF to build the Windows desktop interface, with window layout, themes, and animations implemented primarily through code; Windows Forms is used only for the system tray, monitor information, and file save dialogs.

- Handle window topmost, hit testing, and screen‑edge interaction via Win32 API interop, and use WPF timers to drive the expansion, collapse, and transition animations of the sticky‑note tray.

## 安装

如果不能安装，请在设置里关闭“Smart App Control”选项。

## Install

If can't install, please turn off “Smart App Control” in Settings.