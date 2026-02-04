+++
title = "Easydict Windows 版"
description = "轻松查词翻译"
template = "index.html"
path = "zh"

[extra]
screenshot = "img/overview.png"
+++

这是 [Easydict](https://github.com/tisfeng/Easydict) 的 Windows 移植版本，原版是一款 macOS 翻译词典应用。本项目使用 **Vibe Coding** — AI 辅助编程，将 Swift/SwiftUI 代码库迁移到 .NET 8 + WinUI 3。

虽然功能尚未完全对齐 macOS 版本，但此移植版填补了 Windows 用户对便捷翻译工具的需求，支持全局快捷键和多种翻译服务。

## 功能特性



## 安装

### 系统要求

- Windows 10 版本 2004（内部版本 19041）或更高版本
- x64 或 ARM64 处理器

### 下载

从 [Releases](https://github.com/xiaocang/easydict_win32/releases) 页面下载。

#### 便携版（推荐）

**文件：** `easydict_win32-vX.Y.Z-x64.zip`

- 无需安装 - 解压即用
- 无需管理员权限
- 自包含（内含 .NET 运行时）
- 首次运行可能触发 Windows SmartScreen 警告 - 点击「更多信息」→「仍要运行」

```powershell
# 解压并运行
Expand-Archive easydict_win32-v1.0.0-x64.zip -DestinationPath Easydict
.\Easydict\Easydict.WinUI.exe
```

#### 验证下载（可选）

每个发布版本都包含 SHA256 校验和文件。

```bash
# Linux/macOS/WSL
sha256sum -c checksums-x64.sha256 --ignore-missing

# PowerShell
$expected = (Get-Content checksums-x64.sha256 | Select-String "easydict_win32").ToString().Split()[0]
$actual = (Get-FileHash easydict_win32-v1.0.0-x64.zip -Algorithm SHA256).Hash.ToLower()
if ($expected -eq $actual) { "OK" } else { "FAILED" }
```

### 从源码构建

```powershell
# 克隆仓库
git clone https://github.com/xiaocang/easydict_win32.git
cd easydict_win32/dotnet

# 构建
dotnet build src/Easydict.WinUI/Easydict.WinUI.csproj -c Release

# 运行
dotnet run --project src/Easydict.WinUI/Easydict.WinUI.csproj
```
