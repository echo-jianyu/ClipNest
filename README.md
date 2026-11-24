# ClipNest

一个轻巧、常驻桌面最上层的 Windows 粘贴板历史工具。ClipNest 会用原生 WPF
监听系统剪贴板，保存最近 50 条文字或图片，并保持简洁、低干扰的悬浮体验。

![ClipNest 主界面](docs/clipnest-screenshot.png)

## 功能

- 自动捕获来自其他程序的文字、富文本和图片，默认视口优先展示最近内容；纯文字记录可同时显示 5 条。
- 最多保留 50 条历史，其中最多 40 条可置顶；重复内容会合并并移动到最新位置。
- 左键单击整条记录即可复制；文字和图片卡片均可复制、右键操作、置顶或删除。
- 通过 hotkey 唤出后点击记录不会抢走原窗口焦点，可直接切回编辑器粘贴。
- 文字和图片使用同一种动态卡片，按照最短列优先的规则混合排布为 2～3 列。
- 置顶记录固定从左上开始，最新记录紧随其后；文字长度和图片宽高比会改变卡片尺寸。
- 卡片默认显示 40 个字符，超出部分以省略号收尾；可在设置页调整字符数与字号。
- 图片优先读取原始 PNG，自动等待延迟渲染完成，避免空白缩略图；双击图片或按 `Space` 可查看大图。
- 复制纯文本、编辑、置顶和删除集中在记录右键菜单中；图片编辑支持左右旋转；删除后可在提示条中撤销。
- 卡片和右键菜单复制成功时不显示提示，连续点击会合并为最近一次复制，避免打断操作节奏。
- 支持 `Ctrl+F` 搜索、全部 / 文本 / 图片 / 置顶筛选，以及方向键、回车和数字键快速操作。
- 原生 RTF、HTML 与图片 PNG 数据会随历史持久化；图片独立保存到 `images/*.png`，最长边限制为 2560px。
- 程序内复制会通过剪贴板序号识别并抑制回环，不会重复生成记录。
- 点击标题栏 `···` 可打开设置页，重新设置全局快捷键、卡片显示和“复制后自动隐藏”。
- 设置页显示当前版本，并提供关于页面、运行环境与本地数据位置说明。
- 默认 `450 × 300` 逻辑尺寸，使用更大的正文、菜单字号和图片预览；纯文字记录仍保持五行视口。

## 交互

| 操作 | 方式 |
| --- | --- |
| 显示 / 隐藏 | 按默认快捷键 `F4`，单击托盘图标，或使用托盘菜单 |
| 隐藏 | 双击窗口任意空白区域、点击标题栏右侧箭头、按 `Esc` |
| 移动 | 按住窗口空白区域拖动 |
| 浏览历史 | 在列表中滚动鼠标滚轮 |
| 调整大小 | 拖拽右下角缩放手柄，或在标题栏滚动滚轮 / 按住 `Shift` / `Ctrl` 滚动；缩放时左上角位置保持不动 |
| 搜索与筛选 | `Ctrl+F`，或窗口激活后直接键入；使用顶部标签切换类型 |
| 复制记录 | 鼠标左键单击文字或图片卡片；文字记录默认保留格式 |
| 键盘操作 | 方向键选择，`Enter` 复制，`Shift+Enter` 复制纯文本，`Delete` 删除，`Ctrl+P` 置顶 |
| 快速复制 | 数字键 `1`～`5` 复制当前筛选结果前 5 条 |
| 查看大图 | 双击图片卡片或按 `Space` |
| 纯文本 / 编辑 / 置顶 / 删除 | 在记录卡片上单击鼠标右键，从菜单选择 |
| 保存编辑 | `Ctrl+Enter` |
| 设置 | 点击标题栏 `···`，或使用托盘菜单中的“设置” |
| 快捷键与自动隐藏 | 在设置页修改快捷键、卡片显示参数和复制后自动隐藏 |
| 退出 | 右键托盘图标，选择“退出” |

窗口默认逻辑尺寸为 `450 × 300`，缩放时始终保持 `3:2` 比例。发布版本不会在
任务栏或 Alt+Tab 中显示窗口，只保留一个系统托盘图标。

## 直接使用

ClipNest 基于 Windows 10 1903+ 和 Windows 11 已内置的 **.NET Framework 4.8**，
不需要额外安装 .NET 8 或其他现代 .NET 运行时。发布程序使用 AnyCPU，可运行在
32 位和 64 位 Windows 上；项目本身不依赖第三方 DLL。

## 本地开发

开发环境要求：Windows 10 或更高版本、.NET SDK，以及 .NET Framework 4.8
Developer Pack（Visual Studio 的“.NET 桌面开发”工作负载通常已包含）。

```powershell
dotnet restore
dotnet run --project ClipNest/ClipNest.csproj
```

生成并测试：

```powershell
dotnet build ClipNest.slnx -c Release
dotnet test ClipNest.slnx -c Release --no-build
```

发布给最终用户的 .NET Framework 版本：

```powershell
dotnet publish ClipNest/ClipNest.csproj `
  -c Release `
  -p:PublishProfile=FrameworkDependent-Windows
```

发布结果不包含 .NET Framework 运行时本身，因为 Windows 10/11 已内置该运行时。
发布目录为 `artifacts/windows-<版本>`，其中只包含一个
`ClipNest-<版本>.exe`，复制该文件即可直接运行。

## 数据与隐私

历史仅保存在本机：

```text
%LOCALAPPDATA%\ClipNest\history.json
%LOCALAPPDATA%\ClipNest\settings.json
%LOCALAPPDATA%\ClipNest\images\
```

窗口位置、尺度、快捷键和自动隐藏设置会随使用自动保存。图片以独立 PNG
保存在 `images` 目录，历史裁剪或清空时会同步清理不再引用的图片。历史文件是
明文数据，请根据自己的隐私需求决定是否在共享电脑上使用。

## 项目结构

```text
ClipNest/
  Infrastructure/   原生 Win32 调用、命令与属性通知基类
  Models/           剪贴板内容、历史项与应用设置
  Services/         剪贴板监听、热键、历史与设置持久化
  ViewModels/       历史排序、去重、编辑、置顶与撤销逻辑
  Themes/           全局配色、控件样式与动效
ClipNest.Tests/     ViewModel、内容签名与持久化测试
tools/              图标生成与无桌面截图辅助工具
docs/               README 截图
```

## 设计说明

- 原生 WPF，无第三方运行时 UI 依赖。
- 单实例运行；再次启动会唤醒已有窗口。
- 使用 `AddClipboardFormatListener` 和 `WM_CLIPBOARDUPDATE` 监听剪贴板变化。
- 剪贴板重试与图片解码 / 编码在后台完成，并提供可配置的全局热键。
- 文字与图片混合剪贴板优先保留 Excel / Word 等应用的真实文本内容。
- 通过 `WM_MOUSEACTIVATE` 抑制记录卡片的激活行为，支持即点即贴。
- 使用 `WS_EX_TOOLWINDOW`、`ShowInTaskbar=false` 和 `Topmost=true` 实现悬浮工具窗口。
- 历史保存采用临时文件原子替换，损坏的历史文件会保留备份并从空历史恢复。
- 应用通过 Per-Monitor V2 DPI 清单支持不同缩放比例的显示器。

## License

[MIT](LICENSE)

版本记录见 [CHANGELOG.md](CHANGELOG.md)。
