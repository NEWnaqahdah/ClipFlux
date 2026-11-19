# ClipFlux

[English](README.md) | [简体中文](README.zh-CN.md)

基于 C#（.NET 7 / WPF）与 SQLite 编写的轻量级 Windows 剪贴板管理工具。

## 主要功能

- **多格式记录**：捕获纯文本、富文本（RTF）、网页片段（HTML）、图像（PNG/DIB）和文件列表，使用 SQLite WAL 模式存储。
- **富内容预览与格式控制**：HTML 与富文本默认以排版视图预览，可切换纯文本或源码视图，支持点击链接；正常粘贴富文本时同步写入 RTF 与 `HTML Format`，`Shift + Enter` 纯文本粘贴会彻底清除富文本格式。
- **网页网址提取**：自动读取 `CF_HTML`、`text/x-moz-url-priv` 或浏览器当前标签页地址栏，存档完整页面 URL，方便直接打开或复制。
- **检索与筛选**：支持按文本、来源程序、网址及中文拼音首字母（如 `wx` 匹配“微信”）快速搜索，并按类型或星标过滤。
- **多方式备份与导入**：支持手动备份（立即备份 / 另存为）、定时后台自动备份与旧备份清理，可选网盘同步目录（自动识别 OneDrive、Google Drive、Dropbox、iCloud Drive、坚果云、百度网盘同步盘或自定义目录）和 WebDAV 上传（密码使用 Windows DPAPI 加密保存），支持从兼容的 SQLite 历史库只读导入。
- **中英文与浅/深色主题**：支持简体中文和英文界面、浅色与深色主题，默认均跟随 Windows 系统设置自动切换。
- **后台低占用与隐私过滤**：自动忽略密码管理器标记的敏感剪贴板格式，最小化到托盘后释放缓存与工作集内存；清空未收藏历史置于设置内并提供两步确认。

## 本地数据路径

个人剪贴板数据与配置保存在系统用户目录中，不会混入源码仓库：

- `%APPDATA%\ClipFlux\history.db`（剪贴板历史数据库）
- `%APPDATA%\ClipFlux\settings.json`（偏好设置与加密后的 WebDAV 凭据）

## 构建方法

需要 Windows 10 (1809+) / Windows 11 与 [.NET 7 SDK](https://dotnet.microsoft.com/download/dotnet/7.0)（或更高版本）。

```bash
git clone https://github.com/NEWnaqahdah/ClipFlux.git
cd ClipFlux

dotnet build -c Release
dotnet publish -c Release -r win-x64 --no-self-contained -o ./publish
```

构建完成后，可执行文件位于 `./publish/ClipFlux.exe`。

## 快捷键说明

| 快捷键 | 功能 |
| :--- | :--- |
| 全局快捷键（默认关闭，可在设置中开启） | 呼出 / 隐藏主窗口 |
| `双击条目` | 粘贴选中条目到当前活动窗口 |
| `Shift + Enter` | 以纯文本格式粘贴到当前活动窗口 |
| `Ctrl + C` | 复制选中条目到剪贴板 |
| `Ctrl + S` | 切换收藏星标 |
| `Ctrl + A` | 全选当前列表 |
| `Delete` | 删除选中条目 |
| `Esc` | 关闭浮层或最小化到托盘 |

## 开源协议

[MIT License](LICENSE)
