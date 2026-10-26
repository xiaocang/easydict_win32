<p align="center">
  <img src="screenshot/icon_512x512@2x.png" height="256">
  <h1 align="center">Easydict <sup><sub>for Windows</sub></sup></h1>
  <h4 align="center">轻松查词翻译</h4>
  <p align="center">A Windows port of <a href="https://github.com/tisfeng/Easydict">Easydict</a></p>
</p>

<div align="center">
<a href="./README.md">English</a> &nbsp;&nbsp;|&nbsp;&nbsp; <a href="./README_ZH.md">中文</a>
</div>

<div align="center">

[![CI](https://github.com/xiaocang/easydict_win32/actions/workflows/ci.yml/badge.svg)](https://github.com/xiaocang/easydict_win32/actions/workflows/ci.yml) [![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0) ![Source:Test LOC](https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/xiaocang/easydict_win32/badges/source-test-ratio.json) [![WinGet](https://img.shields.io/winget/v/xiaocang.EasydictforWindows)](https://github.com/microsoft/winget-pkgs/tree/master/manifests/x/xiaocang/EasydictforWindows)

<a href="https://apps.microsoft.com/detail/9p7nqvxf9dzj">
  <img src="https://get.microsoft.com/images/zh-cn%20dark.svg" alt="从 Microsoft 获取" width="200" />
</a>
<br>
<a href="#通过-winget-安装">
  <img src="screenshot/winget-install-dark.svg" alt="winget install" width="280" />
</a>

</div>

## 目录

- [简介](#简介)
- [截图](#截图)
- [功能](#功能)
- [安装](#安装)
- [技术栈](#技术栈)
- [翻译服务集成测试](#翻译服务集成测试)
- [与 macOS 版本对比](#与-macos-版本对比)
- [支持项目](#支持项目)
- [许可证](#许可证)
- [致谢](#致谢)

## 简介

Easydict for Windows 将翻译、词典查询和文本工具整合在一起，并提供快捷键与灵活的窗口模式。

<p align="right"><a href="#目录">回到顶部</a></p>

## 截图

![总览](screenshot/overview.png)

*主窗口与迷你窗口（快速翻译）*

| 主窗口 | 所有窗口 | 设置 |
|--------|---------|------|
| ![主窗口](screenshot/main-window.png) | ![所有窗口](screenshot/all-windows.png) | ![设置](screenshot/settings.png) |
| 完整翻译界面 | 主窗口 + 迷你窗口 + 固定窗口及快捷键设置 | 服务配置 |

![浅色与深色模式](screenshot/light-dark-mode.png)

*浅色与深色模式 — 迷你窗口（快速翻译）*

| 语法纠错 | 词典模式 | 长文档翻译 |
|---|---|---|
| ![语法纠错](screenshot/grammar-correction.png) | ![词典模式](screenshot/dictionary.png) | ![长文档翻译](screenshot/long-document.png) |
| AI 智能语法纠错 | 单词释义、音标和例句 | PDF/文本/Markdown 翻译及进度显示 |

| OCR 截图翻译 | 划词翻译 |
|---|---|
| ![OCR 截图翻译](screenshot/ocr-screenshot.png) | ![划词翻译](screenshot/mouse-selection.png) |
| 截取屏幕区域 → OCR 识别 → 翻译 | 在任意应用中选中文本 → 点击翻译 |

<p align="right"><a href="#目录">回到顶部</a></p>

## 功能

- <img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Microsoft.scale-100.png" alt="Microsoft Copilot+" title="Microsoft Copilot+" width="16" height="16"> **Copilot+ PC 与本地 AI** — 在 Copilot+ PC 上使用 NPU 加速翻译，其他 Windows 电脑可使用 Foundry Local。

- **OCR 截图翻译**
  - `Ctrl+Alt+S` — 截取屏幕区域，识别文字并翻译。
  - `Ctrl+Alt+Shift+S` — 识别文字并复制到剪贴板，不进行翻译。

- **划词翻译** — 在任意应用中选中文本，再点击浮动按钮即可翻译。

- **文本动作** — 在任意窗口中将选中文本发送到翻译或搜索服务。

- **Bump plugin**

- **实验室** — 新功能请从 Settings -> Labs 开始体验。

- **悬浮取词** — 按住 `Ctrl` 至少 50 毫秒，再将鼠标悬停在单词上即可查询释义。

- **语法纠错模式**

- **AI 服务配置与** <img src="screenshot/mocktail-mark.svg" alt="Mocktail" title="Mocktail" width="16" height="16"> **Mocktail**

- **长文档翻译** — 支持 PDF、纯文本和 Markdown 文档翻译，具备版面检测、公式保护、并行处理、双语输出和翻译缓存。

- **词典模式** — 查询单词释义、音标和例句。

- **MDX 词典** — 导入 MDict (`.mdx`) 词典文件，离线查词。

- **Direction Mode**

- **MDX Direction**

- **全局快捷键**
  - `Ctrl+Alt+T` — 显示/隐藏主窗口
  - `Ctrl+Alt+D` — 翻译剪贴板内容
  - `Ctrl+Alt+M` — 显示/隐藏迷你窗口
  - `Ctrl+Alt+F` — 显示/隐藏固定窗口
  - `Ctrl+Alt+S` — OCR 截图翻译
  - `Ctrl+Alt+Shift+S` — 静默 OCR（复制识别文字到剪贴板）
  - `Ctrl+Alt+Shift+M` — 切换迷你窗口可见性
  - `Ctrl+Alt+Shift+F` — 切换固定窗口可见性

- **多窗口模式**
  - 主窗口、迷你窗口和固定窗口

- **流式翻译** — 实时查看翻译结果。

- **深色主题**

- **TTS** — 朗读原文和译文。

- **多语言界面** — 支持 15 种界面语言。

- **40+ 语种** — 选择源语言和目标语言列表中显示的语种。

- **HTTP 代理支持**

- **高 DPI 支持**

- **多组翻译服务**

<table>
  <tr>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/google_web.scale-100.png" alt="Google 翻译和 Google 词典" title="Google 翻译和 Google 词典" width="24" height="24"><br>Google 翻译<br>Google 词典</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Bing.scale-100.png" alt="必应翻译" title="必应翻译" width="24" height="24"><br>必应</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/DeepL.scale-100.png" alt="DeepL" title="DeepL" width="24" height="24"><br>DeepL</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Youdao.scale-100.png" alt="有道" title="有道" width="24" height="24"><br>有道</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/OpenAI.scale-100.png" alt="OpenAI" title="OpenAI" width="24" height="24"><br>OpenAI</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Gemini.scale-100.png" alt="Gemini" title="Gemini" width="24" height="24"><br>Gemini</td>
  </tr>
  <tr>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/DeepSeek.scale-100.png" alt="DeepSeek" title="DeepSeek" width="24" height="24"><br>DeepSeek</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Groq.scale-100.png" alt="Groq" title="Groq" width="24" height="24"><br>Groq</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Zhipu.scale-100.png" alt="智谱 AI" title="智谱 AI" width="24" height="24"><br>智谱 AI</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Kimi.scale-100.png" alt="Kimi" title="Kimi" width="24" height="24"><br>Kimi</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/GitHub.scale-100.png" alt="GitHub Models" title="GitHub Models" width="24" height="24"><br>GitHub Models</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Doubao.scale-100.png" alt="豆包" title="豆包" width="24" height="24"><br>豆包</td>
  </tr>
  <tr>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Volcano.scale-100.png" alt="火山翻译" title="火山翻译" width="24" height="24"><br>火山翻译</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Caiyun.scale-100.png" alt="彩云小译" title="彩云小译" width="24" height="24"><br>彩云小译</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/NiuTrans.scale-100.png" alt="小牛翻译" title="小牛翻译" width="24" height="24"><br>小牛翻译</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Linguee.scale-100.png" alt="Linguee（暂不可用）" title="Linguee（暂不可用）" width="24" height="24"><br>Linguee*</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Ollama.scale-100.png" alt="Ollama" title="Ollama" width="24" height="24"><br>Ollama</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/windows-local-ai.scale-100.png" alt="Windows Local AI" title="Windows Local AI" width="24" height="24"><br>Windows Local AI</td>
  </tr>
  <tr>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/OpenRouter.scale-100.png" alt="OpenRouter" title="OpenRouter" width="24" height="24"><br>OpenRouter</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/OrcaRouter.scale-100.png" alt="OrcaRouter" title="OrcaRouter" width="24" height="24"><br>OrcaRouter</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/Magpie.scale-100.png" alt="Magpie" title="Magpie" width="24" height="24"><br>Magpie</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/CustomOpenAI.scale-100.png" alt="自定义 OpenAI 兼容服务" title="自定义 OpenAI 兼容服务" width="24" height="24"><br>自定义 OpenAI</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/claude-code.scale-100.png" alt="Claude Code" title="Claude Code" width="24" height="24"><br>Claude Code</td>
    <td align="center"><img src="dotnet/src/Easydict.WinUI/Assets/ServiceIcons/codex.scale-100.png" alt="Codex" title="Codex" width="24" height="24"><br>Codex</td>
  </tr>
</table>

*Linguee 当前暂不可用。*

<p align="center"><a href="https://www.orcarouter.ai/ref/ref_a42265f998f62828c4d6"><img src="https://img.shields.io/badge/Powered_by-OrcaRouter-2563eb" alt="Powered by OrcaRouter" title="Powered by OrcaRouter"></a></p>

<p align="right"><a href="#目录">回到顶部</a></p>

## 安装

### 系统要求

- Windows 10 版本 2004（内部版本 19041）或更高版本
- x64 或 ARM64 处理器

### 通过 winget 安装

```powershell
winget install xiaocang.EasydictforWindows
```

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

<p align="right"><a href="#目录">回到顶部</a></p>

## 技术栈

- **.NET** - 运行时框架
- **WinUI 3 (Windows App SDK)** - 现代 Windows UI 框架
- **C#** - 编程语言
- **xUnit + FluentAssertions** - 单元测试

<p align="right"><a href="#目录">回到顶部</a></p>

## 翻译服务集成测试

| 服务 | 协议 | 状态 | 备注 |
|------|------|:----:|------|
| Google 翻译 | REST | ✅ | 免费，无需 API Key |
| 必应翻译 | REST | ✅ | 免费，无需 API Key |
| 有道 | REST | ✅ | Web + OpenAPI |
| OpenAI | OpenAI API | ✅ | |
| DeepSeek | OpenAI API | ✅ | |
| Gemini | Gemini API | ✅ | 自定义 SSE 流式协议 |
| 智谱 AI | OpenAI API | — | 暂时跳过：未配置 API Key |
| Kimi | OpenAI API | — | OpenAI 兼容（月之暗面），缺少 API Key |
| 火山翻译 | REST | ✅ | HMAC-SHA256 签名 |
| Groq | OpenAI API | ✅ | OpenAI 兼容 |
| GitHub Models | OpenAI API | — | OpenAI 兼容，缺少 API Key |
| 豆包 | Custom SSE | — | 缺少 API Key |
| DeepL | REST | ✅ | 支持 Free/Pro API |
| 彩云小译 | REST | — | 缺少 API Key |
| 小牛翻译 | REST | ✅ | |
| Linguee | REST | — | 不可用：上游示例 API 域名已无法解析；集成测试默认跳过 |
| Google 词典 | REST | ✅ | |
| Ollama | OpenAI API | — | 需要本地 Ollama 环境 |
| Windows Local AI | 本地运行时 | ✅ | 已测试 TranslationManager 路由；真实推理需要 Phi Silica、Foundry Local 或 OpenVINO 模型 |
| OpenRouter | OpenAI API | — | OpenAI 兼容，缺少 API Key |
| OrcaRouter | OpenAI API | ✅ | 测试覆盖：流式翻译、非流式翻译和实时模型目录（免费模型优先） |
| 自定义 OpenAI | OpenAI API | — | OpenAI 兼容，用户自定义端点 |
| Magpie | OpenAI 兼容网关 | ✅ | 32 项定向离线检查及隔离合成网关界面验收；未调用真实网关或模型 |
| Claude Code | 本地 CLI（stream-json） | — | 需要本地已登录的 `claude` CLI |
| Codex | 本地 CLI（JSONL） | — | 需要本地已登录的 `codex` CLI |

> 标记为 **OpenAI API** 的服务均继承自 `BaseOpenAIService`，共享相同的 OpenAI 兼容实现，因此未测试的服务预期行为一致。

<p align="right"><a href="#目录">回到顶部</a></p>

## 支持项目

如果 Easydict for Windows 对你有帮助，请[在 GitHub 上为本仓库点亮 Star](https://github.com/xiaocang/easydict_win32)。你的支持能帮助更多人发现这个项目，也会激励项目持续开发。

<p align="right"><a href="#目录">回到顶部</a></p>

## 与 macOS 版本对比

| 功能 | macOS | Windows |
|------|-------|---------|
| 翻译服务 | 25+ | 20 |
| OCR 截图翻译 | 支持 | 支持 |
| TTS | 支持 | 支持 |
| 划词翻译 | 支持 | 支持 |
| 悬浮取词 | 不支持 | 支持（默认关闭） |
| 语法纠错 | 不支持 | 支持 |
| 长文档翻译 | 不支持 | 支持 |
| 词典模式 | 支持 | 支持 |
| 窗口类型 | 3 | 3 |
| 全局快捷键 | 10+ | 8 |
| LLM 流式翻译 | 支持 | 支持 |
| 繁体中文 | 支持 | 支持 |

<p align="right"><a href="#目录">回到顶部</a></p>

## 与 pdf2zh 对比

[pdf2zh (PDFMathTranslate)](https://github.com/Byaidu/PDFMathTranslate) 是一款专注于科学论文 PDF 翻译的热门开源工具。下表对比两者在长文档翻译方面的能力。

| 功能 | pdf2zh | Easydict Win32 |
|------|--------|----------------|
| 版面检测 | DocLayout-YOLO | ONNX (DocLayout-YOLO) / Vision LLM / 启发式 / 自动 |
| 公式保护 | LaTeX 保留 | 三级检测、多字体字形渲染、上下标处理 |
| LLM 服务 | OpenAI、Ollama、Azure OpenAI 等 | OpenAI、Gemini、DeepSeek、Groq、智谱、Kimi、GitHub Models、豆包、Ollama、自定义 OpenAI 兼容接口 |
| 文档上下文预处理 | 不支持 | 支持 — 块级翻译前提取术语表、摘要与保留提示，保证术语一致性 |
| 翻译缓存 | 不支持 | 支持 — 基于源文本哈希 + 服务 + 语言对的 SQLite 缓存 |
| 并行翻译 | 有限 | 可配置并发 + 重试 + 质量反馈 |
| OCR 回退（扫描版 PDF） | 不支持 | 支持 — 使用 Windows OCR 处理纯图片页面 |
| CJK 字体处理 | 基础 | 内置 CJK 字体解析器 + TrueType CMAP 解析 + 自动字体下载 |
| 页面范围选择 | 支持 | 支持（`1-3,5,7-10` 或 `all`） |
| 用户界面 | CLI + 简易 GUI | 原生 WinUI 3，含进度、预览、取消 |
| 集成度 | 独立工具 | 完整翻译词典应用的一部分（OCR、划词、快捷键、流式翻译等） |
| 平台 | 跨平台（Python） | Windows 10/11（原生 .NET 8） |

<p align="right"><a href="#目录">回到顶部</a></p>

## 许可证

GPL-3.0 - 仅供学习和交流使用。使用源代码时必须包含许可证和版权声明。

<p align="right"><a href="#目录">回到顶部</a></p>

## 致谢

- [Easydict](https://github.com/tisfeng/Easydict) - macOS 原版
- [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) - WinUI 3 框架

---

*本项目使用 Vibe Coding 开发，由 Claude (Anthropic) 提供 AI 辅助编程。*
