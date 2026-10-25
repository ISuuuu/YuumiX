<p align="center">
  <img src="src/YuumiX.App/Assets/Logo.png" width="128" alt="YuumiX logo">
</p>

<h1 align="center">YuumiX</h1>

<p align="center">
  面向 Windows 11 的英雄联盟对局助手与生涯查询工具。<br>
  纯原生、单进程、高响应，基于 WinUI 3 与 Microsoft.UI.Reactor 构建。
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
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10_%7C_11-0078D4?logo=windows11">
  <img alt="Tests" src="https://img.shields.io/badge/tests-xUnit-blue">
</p>

## 下载与安装

从 [Releases](https://github.com/ISuuuu/YuumiX/releases) 选择一种形态，均无需预装运行时：

- **安装版** `YuumiX_v<版本>_x64-setup.exe`：双击安装，按当前用户装到
  `%LOCALAPPDATA%\Programs\YuumiX`，不弹 UAC，自动创建开始菜单入口与卸载项。
- **便携版** `YuumiX_v<版本>_portable.zip`：解压后运行 `YuumiX-portable\YuumiX.exe`，
  删除整个目录即完全卸载。

> 标记为 **Pre-release** 的版本是预览版，用于提前验证与收集反馈；不追求尝鲜时优先选正式版。

| 事项 | 说明 |
| --- | --- |
| 运行要求 | Windows 10 1809（17763）及以上，x64；推荐 Windows 11 |
| 数据位置 | 安装版写入 `%APPDATA%\Yuumi`（与旧版 Yuumi 共用，可直接沿用既有配置）；便携版写入 `YuumiX-portable\data\`，删除该目录即完全卸载 |
| 更新 | 应用内「设置 → 软件更新」自动比对 GitHub Releases：便携版解压覆盖，安装版静默重装，两者都在退出后由脚本接手 |
| 安全提示 | 发布包未做代码签名，首次运行需在 SmartScreen 提示中点「更多信息 → 仍要运行」 |

> 发布资产命名受更新检查约束且不得混用：便携版必须含 `portable` 且为 `.zip`（包内需带 `portable.flag`），
> 安装版必须含 `setup` 且为 `.exe`（不得含 `portable`）。发布流程见
> [`.github/workflows/release.yml`](.github/workflows/release.yml)，安装脚本见 [`installer/YuumiX.iss`](installer/YuumiX.iss)。

## 功能

### 生涯

- 段位与胜点进度、赛季最高段位、按队列分段的战绩汇总
- 战利品与英雄熟练度渲染、生涯勋章设置
- 召唤师图标与等级进度、战绩卡片联动筛选

### 战绩查询

- 按 Riot ID（`名称#TAG`）搜索任意召唤师，支持查看他人生涯
- 一页 10 条分页与 SGP 增量加载，搜索历史可回溯
- 对局详情面板：双方装备、符文、技能与伤害明细，历史对局回放下载

### 对局信息

- 接入 LiveClient 实时数据，展示双方 10 人段位、近期战绩与阵营胜率
- 按队伍 / 全部战绩视图切换，最多回溯 50 局
- 分路位置图标、常选与次选位置、KDA 分割线；与路人集联动，可跳转至对应对局与生涯
- 无实况数据时以演示数据说明当前状态，避免空白页

### 路人集

- 自动记录同局玩家与偶遇次数，断线重连与多阶段事件不会重复写入
- 自定义标签、快捷置顶，选人阶段自动提醒已标记玩家
- 离线时回退展示本地存量数据

### 工具箱

- 对局自动化：自动接受对局、断线重连、自动选人/禁用、自动接受邀请、自动荣誉点赞、自动再来一局、ARAM 自动报边
- 辅助能力：多杀截图守护、标记提醒、好友清理、观战启动、客户端路径探测与启动
- LCU 未连接时整页锁定为只读，避免在无效状态下改写配置

### 其他

- **OP.GG**：英雄梯度榜与出装/符文推荐，独立窗口承载，可从对局信息直接跳转
- **公告**：内容随仓库同步，打开公告页自动拉取最新
- **设置**：`SettingsCard` / `SettingsExpander` 承载全部配置项，含带透明度通道的颜色选择器、代理设置、路径管理与配置备份回滚
- **对局上传**：对局结束后入队上传，具备去重、串行执行与失败落盘重试

## 技术栈

| 层级 | 技术选型 | 说明 |
| --- | --- | --- |
| UI 框架 | Microsoft.UI.Reactor `0.1.0-preview.16` | 基于 WinUI 3 的声明式组件化 C# 框架 |
| 平台层 | Windows App SDK `2.5.1`、WinUI 3 | Windows 现代桌面应用基础设施 |
| 运行时 | .NET 10（`net10.0-windows10.0.22621.0`） | 由 `global.json` 锁定 SDK 版本 |
| 领域层 | `YuumiX.Core`（`net10.0`） | 纯 C# 类库，零 UI 依赖，便于单测 |
| 打包部署 | 非打包（`WindowsPackageType=None`） | Windows App SDK 自包含便携分发 |
| 目标平台 | x64 | 英雄联盟客户端仅有 x64 Windows 版本 |

### 架构要点

- **声明式 UI**：全部界面由 C# 声明式编写，具备组件化与 Hooks 响应式范式，不写 XAML、不用 MVVM 模板代码。
- **单进程**：UI 渲染、业务编排与 LCU 链路驻留同一 CLR 进程，消除多进程 IPC 序列化开销。
- **分层解耦**：业务逻辑与领域模型沉淀于 `YuumiX.Core`，不依赖任何 WinUI/XAML 类型（ADR-02）。
- **原生视觉**：字号、颜色、圆角、间距一律走 WinUI 语义层，支持 Mica 材质与系统深浅色/高对比度三态。

## 环境要求

| 组件 | 要求 | 说明 |
| --- | --- | --- |
| .NET SDK | 10.0.4xx | 由 `global.json` 锁定 |
| 开发工具 | Visual Studio 2022/2026 或 VS Code | 需包含「.NET 桌面开发」工作负载（命令行构建不强制依赖 VS） |
| Windows SDK | 10.0.26100 或更高 | WinUI 3 编译器由 NuGet 托管，本机无需预装特定版本 |

## 从源代码构建

> ⚠️ **构建时必须显式指定平台**。直接构建单个 `csproj`（而非 `YuumiX.slnx`）且启用了
> `WindowsAppSDKSelfContained` 时，省略 `-p:Platform` 会报平台不匹配错误。

```powershell
# 还原依赖
dotnet restore YuumiX.slnx -p:Platform=x64

# 构建（x64）
dotnet build YuumiX.slnx -p:Platform=x64

# 运行主应用
dotnet run --project src/YuumiX.App -p:Platform=x64

# 执行测试
dotnet test YuumiX.slnx -p:Platform=x64
```

首次构建成功后，可执行文件输出于：
`src/YuumiX.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/YuumiX.exe`

发布产物（自包含便携包内容）：

```powershell
dotnet publish src/YuumiX.App/YuumiX.App.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained true -p:Version=0.1.0 -o artifacts/publish
```

`--self-contained true` 不能省：.NET 6 起 `publish -r <rid>` 不再隐式自包含，漏掉它产出的是框架依赖包（顶层没有 `coreclr.dll`），在没装 .NET 10 运行时的机器上双击无反应。`WindowsAppSDKSelfContained` 只管 WinAppSDK 运行时，不替代这一项。产物顶层应有约 300 个 dll，远少于这个量级即说明退化了。

## 质量门禁

代码规范与验收标准见 [AGENTS.md](AGENTS.md)，提交前必须全部通过：

| 门禁 | 内容 |
| --- | --- |
| 构建 | `dotnet build --no-incremental` 必须 0 警告 0 错误（增量模式会吞分析器告警） |
| 禁用 API | `BannedApiAnalyzers` + `BannedSymbols.txt`：`ScrollView`、`FontWeights`、`ContentDialog.ShowAsync` 等直接编译失败 |
| 源码扫描 | `tests/YuumiX.SourceScan.Tests` 拦截硬编码颜色、字号字面量、非 4 倍数间距等模式级违规 |
| 单元测试 | `YuumiX.Core.Tests`（领域层）与 `YuumiX.App.Tests`（控件与调和逻辑），LCU 交互由 `YuumiX.Testing.Lcu` 提供本地仿真 |
| 发布校验 | 发布流水线校验 `Assets` 完整性（34 个文件）与程序集版本与 tag 一致 |

## 工程目录结构

```
YuumiX/
├── src/YuumiX.App/                  # 应用宿主（WinUI 3 + Reactor）
│   ├── App.cs                       # 应用入口
│   ├── Assets/                      # 静态资源（图标、Logo、段位与分路图标）
│   ├── Composition/                 # 服务宿主容器（DI + 日志 + 生命周期）
│   ├── Controls/                    # 通用组件（SettingsCardKit、LcuImage、Toast 等）
│   ├── Diagnostics/                 # 架构能力验证与诊断实验室
│   ├── Navigation/                  # 路由枚举与导航元数据
│   ├── Pages/                       # 功能页面（Home/Career/Search/GameInfo/SavedPlayers/Tools/Opgg/Notice/Settings）
│   ├── Services/                    # 平台服务（文件选择器等）
│   ├── Shell/                       # 主外壳（自定义标题栏 + 侧边栏 + Mica）
│   ├── Theming/                     # 主题令牌与资源映射
│   └── Windowing/                   # 独立窗口宿主（OP.GG）
├── src/YuumiX.Core/                 # 核心领域层（禁止 UI 依赖）
│   ├── Assets/                      # 游戏资源映射与双级缓存
│   ├── Automation/                  # 对局自动化代理（接收/重连/选人/点赞等）
│   ├── Common/                      # 颜色转换、事件流等基础类型
│   ├── Diagnostics/                 # 轮转日志与诊断设施
│   ├── Features/                    # 业务服务（Match/Summoner/Career/GameInfo/Opgg 等）
│   ├── Interop/                     # Win32 互操作
│   ├── Lcu/                         # LCU 进程发现、凭据解析、HTTP 与 WebSocket
│   ├── Parsers/                     # JSON 数据解析器
│   ├── Persistence/                 # 配置原子读写、备份与迁移
│   ├── Realtime/                    # 云端实时通道（SignalR）
│   ├── Runtime/                     # 运行环境与存储路径计算
│   ├── Updates/                     # GitHub Releases 更新检查与就地替换
│   └── Upload/                      # 对局数据上传队列
├── tests/                           # 4 个测试工程（Core / App / SourceScan / Testing.Lcu）
├── tools/ScreenCapture/             # 开发辅助（窗口前置 + 截图比对）
├── docs/                            # 架构规范、验收记录与需求文档
├── Directory.Build.props            # 统一 MSBuild 属性与产品元数据
├── Directory.Packages.props         # 集中包版本管理（CPM）
├── global.json                      # .NET SDK 锁定
└── YuumiX.slnx                      # 解决方案配置
```

## 数据与日志位置

| 类别 | 存储位置 | 机制说明 |
| --- | --- | --- |
| 配置（安装版） | `%APPDATA%/Yuumi/config.json` | 数据目录名固定为 `Yuumi`，跨版本升级时配置自动沿用 |
| 配置（便携版） | `<exe>/data/config.json` | exe 同目录放置 `portable.flag` 即开启便携模式 |
| 日志 | `<exe>/log/yuumi_yyyy-MM-dd.log` | 单文件 2 MB 分片轮转，默认保留 30 天 |
| 战绩缓存 | `<数据目录>/cache/` | 按客户端版本失效，避免跨版本脏读 |

> 配置写入采用「写临时文件 → 原子重命名替换」，意外断电不会留下半截文件；
> 解析失败时自动备份为 `config.json.backup.<unix秒>` 并降级为默认安全配置。

## 必读文档

| 文档 | 作用 |
| --- | --- |
| [开发强制约定](AGENTS.md) | 排版、颜色、控件、Reactor 约束与提交门禁 |
| [架构与实施规范](docs/architecture.md) | 分层契约、目录规划、选型决策（ADR 系列） |
| [架构验证报告](docs/validation-report.md) | 技术路线与关键能力验证记录 |
| [联机验证清单](docs/live-verification.md) | 真实客户端环境联机实测验收指南 |
| [更新日志](CHANGELOG.md) | 各版本面向用户的变更 |

## 参与贡献

欢迎提交 Issue、拉取请求（PR）与优化建议。

1. 先读 [AGENTS.md](AGENTS.md) 与 [架构与实施规范](docs/architecture.md)，确保改动与工程约定对齐。
2. 保持单进程架构，领域逻辑归入 `YuumiX.Core` 并补充单元测试。
3. 提交前跑通「质量门禁」全部条目。

## 开发辅助

```powershell
# 截图当前屏幕，并把指定进程窗口前置（用于 UI 改动前后的视觉比对）
dotnet run --project tools/ScreenCapture -- shot.png YuumiX
```

## 许可证

仅供个人学习、技术研究与交流使用。
