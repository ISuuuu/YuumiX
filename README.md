<p align="center">
  <img src="src/YuumiX.App/Assets/Logo.png" width="128" alt="YuumiX logo">
</p>

<h1 align="center">YuumiX</h1>

<p align="center">
  面向 Windows 11 的现代化英雄联盟对局助手与生涯查询工具。<br>
  纯原生、高响应、单进程，基于 WinUI 3 与 Microsoft.UI.Reactor 深度打造。
</p>

<p align="center">
  <a href="docs/architecture.md">架构规范</a> ·
  <a href="docs/validation-report.md">验证报告</a> ·
  <a href="docs/live-verification.md">联机验证</a> ·
  <a href="https://github.com/ISuuuu/YuumiX/issues">问题反馈</a>
</p>

<p align="center">
  <a href="https://github.com/ISuuuu/YuumiX/releases/latest"><img alt="Latest release" src="https://img.shields.io/github/v/release/ISuuuu/YuumiX?display_name=tag&sort=semver"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet">
  <img alt="Windows App SDK" src="https://img.shields.io/badge/Windows_App_SDK-2.5-0078D4?logo=windows11">
  <img alt="Reactor" src="https://img.shields.io/badge/Microsoft.UI.Reactor-0.1.0--preview-FF8C00">
  <img alt="Windows 11" src="https://img.shields.io/badge/Windows-11-0078D4?logo=windows11">
  <img alt="Tests" src="https://img.shields.io/badge/tests-xUnit-blue">
</p>

> [!IMPORTANT]
> YuumiX 是英雄联盟桌面助手 [Yuumi](https://github.com/ISuuuu/Yuumi)（基于 Tauri v2 + Vue 3）的 **WinUI 3 + Microsoft.UI.Reactor** 现代化 C# 重写工程。
> 本工程采用**渐进并行迁移**策略：新建 Reactor 工程逐模块搬迁，消除原架构的双进程通信瓶颈与复杂自定义协议，旧版本保持独立发布、随时可回退。

## 功能亮点

- **现代化原生体验**：遵循 Windows 11 Fluent Design，全面支持 Mica 毛玻璃材质、系统深浅色主题自适应与 Yuumi 专属品牌色。
- **声明式 UI 架构**：采用 Microsoft.UI.Reactor 纯 C# 声明式编写，具备类 React/Flutter 的组件化与 Hooks 响应式开发范式，彻底摆脱冗长 XAML 与复杂 MVVM。
- **单进程零 IPC 开销**：UI 渲染、业务编排与 LCU 链路完全驻留于同一 CLR 进程中，消除多进程 IPC 序列化与跨进程传输延迟。
- **高可靠 LCU 连接层**：自动监听 League Client 运行进程，毫秒级提取 `lockfile` 凭据，集成自适应自签名证书信任与原生 WebSocket 事件流。
- **全面对局辅助与分析**：对局 10 人段位与近期战绩透视、阵营胜率分析、召唤师生涯、资产统计、历史对局回放下载与云顶之弈阵容百科。
- **同局路人集与标记**：自动记录并统计同局玩家偶遇频次，支持自定义标签、快捷置顶与对局聊天室自动提醒。
- **独立多窗口与轻量悬浮**：主导航窗口与大乱斗独立悬浮窗、OP.GG 窗口解耦，各自承载独立状态树，互不干扰。
- **无缝配置兼容**：安装版（`%APPDATA%/Yuumi`）与便携版（`data/config.json`）自适应识别，采用安全原子写入与损坏自动备份降级。
- **x64 原生自包含编译**：面向 `x64` 的高性能自包含编译（Self-Contained），无须预装额外运行时，即开即用。英雄联盟客户端目前只有 x64 Windows 版本，因此本项目不提供 ARM64 构建。

## YuumiX.Core

YuumiX 采用分层解耦架构，所有业务逻辑与领域模型均沉淀于 [src/YuumiX.Core](src/YuumiX.Core/)。
它是纯粹的领域库（基于 `net10.0`），完全与 UI 层解耦（不依赖任何 WinUI/XAML 组件），具备高可测性与高内聚性（xUnit 单元测试覆盖）：

- **Lcu/**：LCU 进程发现、凭据提取、自适应 HttpClient 通信与原生 WebSocket 事件分发。
- **Assets/**：LCU 本地静态资源与 CDragon CDN 资产加载映射及双级缓存。
- **Parsers/**：对局历史、战绩详情、召唤师 Profile、SGP 聚合与队友关系的极速解析。
- **Features/**：聚合业务服务层（`MatchService`、`SummonerService` 等）。
- **Persistence/**：强类型配置定义、原子替换保存机制、坏配置降级与模式迁移。

## 技术栈

| 层级 | 技术选型 | 说明 |
| --- | --- | --- |
| UI 框架 | Microsoft.UI.Reactor `0.1.0-preview.16` | 基于 WinUI 3 的声明式组件化 C# 框架 |
| 平台层 | Windows App SDK `2.5.1`、WinUI 3 | Windows 11 现代桌面应用基础设施 |
| 运行时 | .NET 10（`net10.0-windows10.0.22621.0`） | 统一由 `global.json` 锁定 SDK 版本 |
| 领域层 | `YuumiX.Core`（`net10.0`） | 纯 C# 类库，零 UI 依赖，跨平台核心 |
| 打包部署 | 非打包（`WindowsPackageType=None`） | Windows App SDK 自包含便携分发 |
| 目标平台 | x64 | 英雄联盟客户端仅有 x64 Windows 版本，无需 ARM64 构建 |

## 环境要求

| 组件 | 要求 | 说明 |
| --- | --- | --- |
| .NET SDK | 10.0.4xx | 由 `global.json` 锁定 |
| 开发工具 | Visual Studio 2022/2026 或 VS Code | 需包含「.NET 桌面开发」工作负载（命令行构建不强制依赖 VS） |
| Windows SDK | 10.0.26100 或更高 | WinUI 3 编译器由 NuGet 托管，本机无需预装特定版本 |

## 从源代码构建

正式 Windows 客户端使用 .NET 10、Windows App SDK 和 Microsoft.UI.Reactor 构建。

> ⚠️ **构建时必须显式指定平台**。直接构建单个 `csproj`（而非 `YuumiX.slnx`）且启用了 `WindowsAppSDKSelfContained` 时，省略 `-p:Platform` 会报平台不匹配错误。
> 通过 `YuumiX.slnx` 构建可不带 `-p:Platform`（解决方案现在只声明 x64 一个平台），仍建议统一带上以免踩历史坑。

```powershell
# 还原依赖
dotnet restore YuumiX.slnx -p:Platform=x64

# 构建项目（x64）
dotnet build YuumiX.slnx -p:Platform=x64

# 运行主应用
dotnet run --project src/YuumiX.App -p:Platform=x64

# 执行单元测试套件（Core 层）
dotnet test YuumiX.slnx -p:Platform=x64
```

首次构建成功后，可执行文件输出于：
`src/YuumiX.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/YuumiX.exe`

## 工程目录结构

```
YuumiX/
├── src/YuumiX.App/                 # 应用主宿主工程（WinUI 3 + Reactor）
│   ├── App.cs                      # 应用入口
│   ├── Assets/                     # 静态资产（应用图标、Logo）
│   ├── Composition/AppHost.cs      # 服务宿主容器（DI + 日志 + 状态）
│   ├── Controls/                   # 自定义通用组件（如 LcuImage）
│   ├── Diagnostics/                # 架构能力验证与诊断实验室
│   ├── Navigation/AppRoute.cs      # 路由枚举与导航元数据
│   ├── Pages/                      # 各功能模块页面
│   │   ├── PageCatalog.cs          # 迁移任务清单（进度唯一事实源）
│   │   ├── Settings/               # 设置页（已完成）
│   │   └── PlaceholderPage.cs      # 迁移期统一占位页面
│   ├── Shell/AppShell.cs           # 应用主外壳（自定义标题栏 + 侧边栏 + Mica）
│   └── Theming/                    # 视觉主题令牌桥接与资产映射
├── src/YuumiX.Core/                # 核心领域层（.NET 10，禁止 UI 依赖）
│   ├── Assets/                     # 资源映射与缓存服务
│   ├── Common/                     # 颜色转换、基础事件流
│   ├── Diagnostics/                # 轮转日志与诊断设施
│   ├── Features/                   # 业务服务（Match/Summoner/SavedPlayers）
│   ├── Lcu/                        # LCU 连接管理与事件总线
│   ├── Parsers/                    # 高性能 JSON 数据解析器
│   ├── Persistence/                # 配置文件原子读写与迁移
│   └── Runtime/                    # 运行环境与存储路径计算
├── tests/YuumiX.Core.Tests/        # 核心领域层单测套件（xUnit）
├── tests/YuumiX.Testing.Lcu/       # LCU 本地仿真实测测试夹具
├── tools/ScreenCapture/            # 开发辅助工具（前置窗口比对截图）
├── docs/                           # 架构设计、演进规范与验收记录
├── Directory.Build.props           # 统一 MSBuild 编译属性
├── Directory.Packages.props        # 集中包版本管理（CPM）
├── global.json                     # .NET SDK 锁定
└── YuumiX.slnx                     # 解决方案配置
```

## 数据与日志位置

| 类别 | 存储位置 | 机制说明 |
| --- | --- | --- |
| 配置文件（安装版） | `%APPDATA%/Yuumi/config.json` | **与原版 Yuumi 完全互通**，平滑过渡 |
| 配置文件（便携版） | `<exe>/data/config.json` | 在可执行文件同目录下创建 `portable.flag` 即可开启 |
| 日志文件 | `<exe>/log/yuumi_yyyy-MM-dd.log` | 单文件 2 MB 分片轮转，默认保留 30 天 |

> 写入机制：配置文件采用「写临时文件 -> 原子重命名替换」机制，保障意外断电不损坏数据；解析失败时会自动备份为 `config.json.backup.<unix秒>` 并降级为默认安全配置。

## 迁移与开发进度

路由共 8 个主要视图与 2 个独立窗口。各模块待迁移功能清单详见 `src/YuumiX.App/Pages/PageCatalog.cs`。

| 模块 | 原版视图 | 迁移状态 | 备注 |
| --- | --- | --- | --- |
| 主页 | `src/views/Home.vue` | 占位中 | 依赖 LCU 客户端状态链路（已就绪） |
| 生涯 | `src/views/Career.vue` | 占位中 | 依赖召唤师与资产解析（已就绪） |
| 战绩查询 | `src/views/Search.vue` | 进行中 | `MatchService` 核心契约与解析已落地 |
| 对局信息 | `src/views/GameInfo.vue` | 占位中 | 10 人数据透视与组队分析 |
| 路人集 | `src/views/SavedPlayers.vue` | 进行中 | 最近队友解析与模型契约已完成 |
| 云顶之弈 | `src/views/TFT.vue` | 占位中 | 阵容数据与海克斯百科 |
| 工具箱 | `src/views/Tools.vue` | 占位中 | 好友管理、资产分解等小工具 |
| 设置 | `src/views/Settings.vue` | **已完成** | 支持配置项实时编辑与原子写回 |
| 大乱斗悬浮窗 | `src/views/BenchOverlay.vue` | 待迁移 | 独立窗口承载 |
| OP.GG 窗口 | `src/components/OpggWindow.vue` | 待迁移 | 独立窗口承载 |

## 必读文档

| 文档 | 作用 |
| --- | --- |
| [架构与实施规范](docs/architecture.md) | **开发实施的唯一权威依据**：包含分层契约、目录规划、选型决策与迁移路线 |
| [架构验证报告](docs/validation-report.md) | 第一阶段技术路线与关键能力验证记录 |
| [联机验证清单](docs/live-verification.md) | 真实 LoL 客户端环境联机实测验收指南 |

## 参与贡献

欢迎提交 Issue、拉取请求（PR）与优化建议！

1. 查阅 [架构与实施规范](docs/architecture.md)，确保代码设计与工程规范对齐。
2. 保持单进程架构，领域逻辑归入 `YuumiX.Core` 并补充单元测试。
3. 遵循现有的代码格式化与规范。

## 开发辅助

```powershell
# 截图当前屏幕，并把指定进程窗口前置（用于 UI 迁移前后的视觉比对）
dotnet run --project tools/ScreenCapture -- shot.png YuumiX
```

## 与原版 Yuumi 的关系

Yuumi 积累了成熟优秀的功能体验与用户基础。YuumiX 重构旨在借助现代 .NET 平台与 WinUI 3 带来更强劲的性能与更纯正的 Windows 11 视觉体验，感谢原项目 [Yuumi](https://github.com/ISuuuu/Yuumi) 及其所有贡献者。

## 许可证

仅供个人学习、技术研究与交流使用。
