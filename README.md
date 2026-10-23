# YuumiX

Yuumi 的 **WinUI 3 + Microsoft.UI.Reactor** 重写工程。

原项目 [Yuumi](../Yuumi) 是 Tauri v2（Rust，22,389 行）+ Vue 3（43,752 行）的成熟桌面应用。
本工程采用**渐进并行迁移**策略：新建 Reactor 工程，逐模块搬迁，旧版本保持可发布、随时可回退。

> 当前阶段：**骨架与架构验证已完成**，待进入业务迁移。

## 必读文档

| 文档 | 用途 |
| --- | --- |
| [架构与实施规范](docs/architecture.md) | **后续开发的唯一依据**：分层规则、目录规划、架构决策、迁移路线 |
| [架构验证报告](docs/validation-report.md) | 第一阶段环境与能力验证记录 |

## 技术栈

| 层级 | 技术 |
| --- | --- |
| UI 框架 | Microsoft.UI.Reactor `0.1.0-preview.16`（WinUI 3 之上的声明式 C# 框架） |
| 平台 | Windows App SDK `2.5.1`、WinUI 3 |
| 运行时 | .NET 10（`net10.0-windows10.0.22621.0`） |
| 打包 | 非打包（`WindowsPackageType=None`）+ Windows App SDK 自包含 |

## 环境要求

| 组件 | 要求 | 说明 |
| --- | --- | --- |
| .NET SDK | 10.0.4xx | 已由 `global.json` 锁定 |
| Visual Studio | 2022/2026，含「.NET 桌面开发」工作负载 | 仅 IDE 开发需要；命令行构建不依赖 |
| Windows SDK | 10.0.26100 或更高 | WinUI 3 的 XAML 编译器由 NuGet 包自带，不强制要求本机 SDK 版本 |

## 构建与运行

> ⚠️ **必须显式指定平台**。Reactor 依赖 `WindowsAppSDKSelfContained`，
> 省略 `-p:Platform` 会报 `WindowsAppSDKSelfContained requires a supported Windows architecture`。

```bash
# 构建（x64）
dotnet build YuumiX.slnx -p:Platform=x64

# 运行
dotnet run --project src/YuumiX.App -p:Platform=x64

# ARM64
dotnet build YuumiX.slnx -p:Platform=ARM64
```

首次构建产物位于 `src/YuumiX.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/`，
可执行文件为 `YuumiX.exe`
（因设置了 `RuntimeIdentifier`，输出目录会比常规项目多一层 RID 子目录）。

## 目录结构

```
YuumiX/
├── src/YuumiX.App/                 # 应用主工程（命名空间 YuumiX.App.*）
│   ├── App.cs                      # 入口（Reactor 无 Program.cs / App.xaml）
│   ├── Shell/AppShell.cs           # 外壳：自定义标题栏 + 侧边导航 + 内容宿主
│   ├── Navigation/AppRoute.cs      # 路由枚举与侧边栏元数据
│   ├── Theming/YuumiTheme.cs       # 主题令牌桥接（品牌色/胜负卡片色/Mica）
│   ├── Pages/                      # 页面（当前为占位，逐步替换为真实实现）
│   │   ├── PageCatalog.cs          # 每个路由的待迁移功能清单（进度唯一事实来源）
│   │   └── PlaceholderPage.cs      # 骨架期统一占位组件
│   └── Diagnostics/CapabilityLab.cs # 架构能力验证页（迁移完成后可移除）
├── tools/ScreenCapture/            # 开发辅助：前置目标窗口并截图，用于视觉比对
├── docs/                           # 架构决策与验证报告
├── Directory.Build.props           # 统一编译属性
├── Directory.Packages.props        # 集中包版本管理（CPM）
├── global.json                     # SDK 版本锁定
└── YuumiX.slnx                     # 解决方案（含 x64/ARM64 平台映射）
```

## 迁移进度

路由共 8 项，当前全部为占位页。每个页面的待迁移功能清单位于
`src/YuumiX.App/Pages/PageCatalog.cs`，清单清空即代表该模块完成。

| 路由 | 原实现 | 状态 |
| --- | --- | --- |
| 主页 | `src/views/Home.vue` | 占位 |
| 生涯 | `src/views/Career.vue` | 占位 |
| 战绩查询 | `src/views/Search.vue` | 占位 |
| 对局信息 | `src/views/GameInfo.vue` | 占位 |
| 路人集 | `src/views/SavedPlayers.vue` | 占位 |
| 云顶之弈 | `src/views/TFT.vue` | 占位 |
| 工具箱 | `src/views/Tools.vue` | 占位 |
| 设置 | `src/views/Settings.vue` | 占位 |

尚未纳入主导航、需独立窗口承载的模块：
- **大乱斗悬浮窗**（原 `src/views/BenchOverlay.vue`）
- **OP.GG 独立窗口**（原 `src/components/OpggWindow.vue`）

## 开发辅助

```bash
# 截图当前屏幕，并把指定进程窗口前置（用于 UI 迁移前后的视觉比对）
dotnet run --project tools/ScreenCapture -- shot.png YuumiX
```

## 许可

仅供学习交流使用。
