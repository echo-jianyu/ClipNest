# ClipNest

轻巧、常驻桌面最上层的 Windows 剪贴板历史工具。

**Windows 10/11** · **.NET Framework 4.8** · **MIT License**

![ClipNest 主界面](docs/clipnest-screenshot.png)

ClipNest 使用原生 WPF 监听系统剪贴板，保存最近 50 条文字、富文本和图片。按下
`F4` 即可在任意应用上方唤出，单击复制，按住卡片左键拖动还能直接拖入 Word、
Typora 等编辑区域。

## 下载

从 [GitHub Releases](https://github.com/echo-jianyu/ClipNest/releases) 或
[Gitee Releases](https://gitee.com/echo-jian/clipnest/releases) 下载最新版本。

- 单文件运行，无需安装
- 不依赖额外 .NET 运行时
- 支持 Windows 10 1903+ 和 Windows 11
- 历史记录仅保存在本机

## 功能

### 快速取用

- 按 `F4`、单击托盘图标，或通过托盘菜单显示和隐藏窗口
- 单击卡片复制，按住左键拖动可把文字或图片拖入其他应用
- 支持 `Ctrl+F` 搜索，以及全部、文本、图片、置顶筛选
- 方向键选择，`Enter` 复制，数字键 `1`～`5` 快速复制
- 唤出后点击卡片不会抢走原窗口焦点，可直接切回编辑器粘贴

### 内容与卡片

- 自动捕获文字、富文本和图片，最多保留 50 条记录
- 使用紧凑单列清单，所有卡片保持等宽并按时间顺序纵向排列
- 小图片保持原始尺寸封顶，不会被强制放大
- 支持置顶、编辑、删除与撤销；图片可向左或向右旋转
- 双击图片或按 `Space` 查看大图
- 原生 RTF、HTML 与 PNG 数据随历史持久化

### 窗口与设置

- 默认 `400 × 400`，可拖动右下角自由调整宽高，滚轮按当前比例缩放
- 窗口始终置顶，不显示任务栏按钮，只保留系统托盘图标
- 可配置全局快捷键、卡片字符数、字号和复制后自动隐藏
- 支持随 Windows 登录自动启动，默认关闭，可在设置页主动开启
- “关于”区域提供 GitHub 和 Gitee 项目链接

## 常用操作

| 操作 | 方式 |
| --- | --- |
| 显示 / 隐藏 | `F4`、托盘图标或托盘菜单 |
| 复制 | 左键单击文字或图片卡片 |
| 拖出 | 按住卡片左键，拖动到目标编辑区域 |
| 搜索 | `Ctrl+F` |
| 纯文本 / 编辑 / 置顶 / 删除 | 右键单击卡片 |
| 保存编辑 | `Ctrl+Enter` |
| 查看大图 | 双击图片或按 `Space` |
| 调整尺寸 | 拖动右下角，或在标题栏滚动滚轮 |
| 退出 | 右键托盘图标，选择“退出” |

## 数据与隐私

历史与设置仅保存在本机：

```text
%LOCALAPPDATA%\ClipNest\history.json
%LOCALAPPDATA%\ClipNest\settings.json
%LOCALAPPDATA%\ClipNest\images\
```

历史文件为明文数据。图片以独立 PNG 保存，裁剪或清空历史时会同步清理不再引用的图片。

## 本地开发

需要 Windows 10+、.NET SDK 和 .NET Framework 4.8 Developer Pack。

```powershell
dotnet restore
dotnet run --project ClipNest/ClipNest.csproj
```

构建、测试与发布：

```powershell
dotnet build ClipNest.slnx -c Release
dotnet test ClipNest.slnx -c Release --no-build

dotnet publish ClipNest/ClipNest.csproj `
  -c Release `
  -p:PublishProfile=FrameworkDependent-Windows
```

发布结果位于 `artifacts/windows-<版本>/ClipNest-<版本>.exe`，目录中只包含一个
可执行文件。GitHub Release 由 `release` 工作流创建；Gitee Release 使用
Gitee OpenAPI 单独创建并上传同一 EXE。

## 技术要点

- 原生 WPF，单实例运行，不依赖第三方运行时 UI
- `AddClipboardFormatListener` 与 `WM_CLIPBOARDUPDATE` 监听剪贴板
- 通过 `WM_MOUSEACTIVATE` 保持即点即贴体验
- 遵循 Windows 剪贴板监控排除协议，跳过密码管理器等敏感内容
- `WS_EX_TOOLWINDOW`、`ShowInTaskbar=false`、`Topmost=true` 实现悬浮工具窗口
- 历史保存采用临时文件原子替换，并支持损坏文件备份恢复
- 支持 Per-Monitor V2 DPI

## License

[MIT](LICENSE) · 版本记录见 [CHANGELOG.md](CHANGELOG.md)
