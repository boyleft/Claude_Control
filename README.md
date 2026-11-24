# Claude 与 ChatGPT 控制台

### 下载与运行

| Windows 系统 | 压缩包 |
| --- | --- |
| Windows 10，32 位 x86 | `ClaudeControl-win-x86.zip` |
| Windows 10 / 11，64 位 Intel 或 AMD | `ClaudeControl-win-x64.zip` |

解压所选 ZIP，双击包内的 `ClaudeControl.exe`。两个包都附有对应版本的完整源码和构建脚本。运行时使用 Windows 的 .NET Framework 4.8 或 4.8.1 桌面组件，程序图标和界面已嵌入 EXE。需要补装系统运行库时，可从微软下载 [.NET Framework 4.8 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48)。Windows 11 ARM64 可尝试通过 x64 仿真运行，本项目尚未在 ARM64 实机验证。Anthropic 官方列出的 Windows 桌面安装包为 x64 和 ARM64，纯 32 位 Windows 可运行控制台的适用场景取决于本机安装的 Claude 组件。

主窗口分别显示 Claude 和 ChatGPT 的进程数。展开「查看进程明细」可查看应用、文件名和 PID。「关闭 Claude」和「关闭 GPT」分别结束所选应用及可识别的后台进程。Windows 随进程终止其线程，应用中正在进行的任务和未保存内容可能丢失。

开启「桌面悬浮标」后，双击 99 × 99 像素的悬浮标可打开主窗口，包括从托盘或最小化状态恢复。悬停时，上方是「关闭 Claude」，下方是「关闭 GPT」。按住左键拖动可移动悬浮标，程序会记住开关和位置。关闭主窗口时可选择「隐藏到托盘」或「退出程序」。单击托盘图标也可恢复主窗口，右键菜单提供同样的两个关闭操作。

### ChatGPT 的关闭范围

程序根据 Windows 安装包路径、主程序产品信息及父子关系识别 ChatGPT。本机开始菜单中的「ChatGPT」由 `OpenAI.Codex_2p2nqsd0c76g0!App` 启动，实际入口是 `app\ChatGPT.exe`。关闭这个版本会中断其桌面会话和由它运行的本地 Codex 工作。独立运行的通用 Node、浏览器或其他 OpenAI 应用，仅具有相同发布者或进程名不足以确认归属。

程序也识别 `OpenAI.ChatGPT-Desktop` 安装包及具有该包身份的 WebView2 进程。关闭前后会按 PID 和创建时间多次扫描；进程数、残留项和权限问题会显示在控制台中。Windows 桌面外壳等共享进程构成进程树的边界。浏览器里打开的 ChatGPT 网页和云端任务不属于本机桌面应用的进程。

## 本机核查

2026-11-23 在本机核对了 Claude 的 MSIX 安装清单、安装目录、服务注册信息和运行进程。安装包为 `Claude_6.28899.6.0_x64__pzs8sxrjxfjjc`。核查时有 14 个 `Claude.exe` 实例，分别承担主进程、8 个渲染器、GPU、崩溃处理、音频、视频采集和网络服务职责。其中网络服务运行参数标明 `network.mojom.NetworkService`。此外，Windows 服务 `CoworkVMService` 当时正在运行。网络服务的存在只能说明应用有网络组件。

| 本机安装包内的可执行文件 | 关闭方式 |
| --- | --- |
| `Claude.exe` | 匹配安装路径，处理各个实例和可识别的后代进程 |
| `chrome-native-host.exe` | 匹配 Claude 安装路径，包括由浏览器启动的本地桥接程序 |
| `claude-ssh-askpass.exe`、`claude-ssh-broker.exe`、`claude-ssh-proxy.exe` | 匹配 Claude 安装路径 |
| `claude-make-data-folder.exe`、`github-mcp-server.exe` | 匹配 Claude 安装路径 |
| `cowork-svc.exe` | 核对已注册服务的映像路径，通过 Windows 服务控制管理器停止服务，并处理检测到的服务子进程 |

程序按版本无关的安装目录识别包内程序，也会识别 Claude 启动的 Node、Python、命令行及 MCP 子进程。每轮扫描按 PID 和创建时间追踪已经观察到的进程。关闭后会重复扫描，并显示残留项。停止 Cowork 服务需要相应权限，出现 Windows 管理员授权弹窗时可选择是否继续。中途取消时，主窗口会显示服务的实际状态。

## 官方资料与识别范围

- Anthropic 的 Windows 部署文档说明，Claude Desktop 为 Cowork 注册独立后台服务。Windows 的 HNS、虚拟机计算服务等属于共享基础设施。本工具对 Cowork 操作其专属服务。[Windows 部署文档](https://support.claude.com/en/articles/12622703-deploy-claude-desktop-for-windows)
- 桌面扩展支持 Node.js、Python 和独立可执行文件，因此扩展的进程名会随安装内容变化。进程树可以识别从 Claude 启动、具有其他文件名的程序。[本地 MCP 服务](https://support.claude.com/en/articles/10949351-getting-started-with-local-mcp-servers-on-claude-desktop)
- 独立的 Windows Claude Code 也纳入检查，包括原生安装、WinGet、官方 npm 包、旧版 Node 启动脚本、官方 VS Code 扩展和 Agent SDK 中可识别的 CLI。自定义位置的 `claude.exe` 还会检查产品和公司信息。[Claude Code 安装文档](https://code.claude.com/docs/en/setup) · [VS Code 集成](https://code.claude.com/docs/en/vs-code) · [Anthropic Agent SDK](https://github.com/anthropics/claude-agent-sdk-python)
- Chrome 扩展可在浏览器侧栏独立运行。工具覆盖 Claude 的专属本地桥接程序，浏览器内的扩展、普通网页、WSL 内部进程、外部容器及云端任务超出 Windows 进程检查范围。[Claude in Chrome](https://support.claude.com/en/articles/12012173-get-started-with-claude-in-chrome) · [Cowork 运行方式](https://support.claude.com/en/articles/13345190-get-started-with-claude-cowork)

关闭按钮执行一次关闭操作。控制台持续刷新运行状态，Claude 被用户或其他程序重新启动后会再次显示。应用自启动设置和防火墙规则保持原有配置。对于控制台启动前已经脱离父进程、使用通用解释器且缺乏归属证据的程序，关闭检查无法判定它是否由 Claude 启动。

## 构建与验证

在已安装 .NET Framework 4.8 或 4.8.1 的 Windows 上，使用 PowerShell 在 `build.ps1` 所在目录运行以下命令。输出分别位于 `bin\x86\ClaudeControl.exe` 和 `bin\x64\ClaudeControl.exe`。运行 `.\build.ps1` 可构建原有的 AnyCPU 版本。用户设置保存在 `HKCU\Software\ClaudeControlDesk`。

```powershell
.\build.ps1 -Platform x86
.\build.ps1 -Platform x64
```

已在本机验证主窗口读取真实进程及服务状态、悬停弹出按钮、拖动、退出选项、隐藏到托盘和恢复窗口。关闭操作用隔离测试进程验证了三级进程树、父进程提前退出、同名无关进程保留及重复关闭。测试也覆盖了旧版 Node CLI 的路径和 PID 归属识别。真实 Claude 会话和 Cowork 服务在验证期间继续运行。
