<p align="center">
  <img src="src/YuumiX.App/Assets/Logo.png" width="128" alt="YuumiX logo">
</p>

<h1 align="center">YuumiX</h1>

<p align="center">
  面向 Windows 的英雄联盟对局助手与生涯查询工具。<br>
  原生界面、单进程运行，启动快、占用低。
</p>

<p align="center">
  <a href="docs/internal/docbak/architecture.md">架构规范</a> ·
  <a href="docs/internal/docbak/validation-report.md">验证报告</a> ·
  <a href="docs/internal/docbak/live-verification.md">联机验证</a> ·
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

从 [Releases](https://github.com/ISuuuu/YuumiX/releases) 下载，两种形态均无需预装运行时：

- **安装版** `YuumiX_v<版本>_x64-setup.exe`：默认安装到当前用户的 `%LOCALAPPDATA%\Programs\YuumiX`（不弹 UAC，安装时可自由选择其他盘符或路径），自动创建开始菜单入口与卸载项。
- **便携版** `YuumiX_v<版本>_portable.zip`：解压后运行 `YuumiX-portable\YuumiX.exe`，删除目录即完全卸载。

运行要求 Windows 10 1809（17763）及以上、x64，推荐 Windows 11。发布包未做代码签名，首次运行需在 SmartScreen 提示中点「更多信息 → 仍要运行」。

## 功能

### 生涯

- 单双排、灵活排分开显示段位、胜点、场次与胜率，附赛季最高与上赛季结算
- 战利品、英雄熟练度与生涯勋章
- 召唤师图标与等级进度，点战绩卡片可筛选

### 战绩查询

- 输入 Riot ID（`名称#TAG`）搜索任意召唤师，也能查看他人生涯
- 每页 10 条，可继续加载更多，搜索历史随时回溯
- 对局详情：双方装备、符文、技能与伤害明细，可下载回放

### 对局信息

- 读取客户端实时对局，显示双方 10 人段位、近期战绩与阵营胜率
- 显示分路、常选与次选位置、KDA，可跳转到对应对局与生涯
- 没有实况数据时展示演示数据，不留空白页

### 路人集

- 自动记录同局玩家与偶遇次数 
- 可自定义标签、置顶，选人阶段自动提醒遇到过的玩家

### 工具箱

- 对局自动化：自动接受对局、断线重连、自动选人/禁用、接受邀请、荣誉点赞、再来一局、ARAM 报边
- 辅助功能：多杀截图、标记提醒、好友清理、观战启动、客户端路径探测与启动

### 其他

- **OP.GG**：英雄梯度榜与出装、符文推荐，独立窗口打开
- **公告**：内容随仓库同步，打开即是最新
- **设置**：所有选项集中在一处，支持代理、路径与配置备份还原
- **对局上传**：对局结束后自动上传，重复数据会跳过，失败自动重试

## 技术栈

| 层级     | 技术选型                                 | 说明                                |
| -------- | ---------------------------------------- | ----------------------------------- |
| UI 框架  | Microsoft.UI.Reactor `0.1.0-preview.16`  | 基于 WinUI 3 的声明式组件化 C# 框架 |
| 平台层   | Windows App SDK `2.5.1`、WinUI 3         | Windows 现代桌面应用基础设施        |
| 运行时   | .NET 10（`net10.0-windows10.0.22621.0`） | 由 `global.json` 锁定 SDK 版本      |
| 领域层   | `YuumiX.Core`（`net10.0`）               | 纯 C# 类库，零 UI 依赖，便于单测    |
| 打包部署 | 自包含发布 + Inno Setup 6                 | 不发 MSIX 包（`WindowsPackageType=None`），同时出安装版与便携版，两者均免装运行时 |
| 目标平台 | x64                                      |                                     |

**架构要点**

- 全界面以 C# 声明式编写（组件化 + Hooks），不写 XAML、不用 MVVM 模板。
- UI、业务编排与 LCU 链路同驻一个 CLR 进程，省去跨进程 IPC 序列化。
- 领域模型沉淀于 `YuumiX.Core`，不依赖任何 WinUI/XAML 类型。
- 字号、颜色、圆角、间距走 WinUI 语义层，支持 Mica 与深浅色、高对比度。

## 环境要求

| 组件        | 要求                               | 说明                                                       |
| ----------- | ---------------------------------- | ---------------------------------------------------------- |
| .NET SDK    | 10.0.4xx                           | 由 `global.json` 锁定                                      |
| 开发工具    | Visual Studio 2022/2026 或 VS Code | 需包含「.NET 桌面开发」工作负载（命令行构建不强制依赖 VS） |
| Windows SDK | 10.0.26100 或更高                  | WinUI 3 编译器由 NuGet 托管，本机无需预装特定版本          |

## 从源代码构建

```powershell
dotnet restore YuumiX.slnx            # 还原依赖
dotnet build YuumiX.slnx              # 构建
dotnet run --project src/YuumiX.App   # 运行主应用
dotnet test YuumiX.slnx               # 执行测试
```

无需手动指定平台：解决方案只定义 x64，应用项目也把 `RuntimeIdentifier` 固定为 `win-x64`。解决方案构建的产物位于 `src/YuumiX.App/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/YuumiX.exe`；直接构建单个项目时输出在 `bin/Debug/...`，内容一致。

发布自包含便携包：

```powershell
dotnet publish src/YuumiX.App/YuumiX.App.csproj -c Release -r win-x64 --self-contained true -p:Version=0.1.0 -o artifacts/publish
```

`--self-contained true` 不能省略：.NET 6 起 `publish -r <rid>` 不再隐式自包含，漏掉它产出的是框架依赖包，在未安装 .NET 10 运行时的机器上无法启动。

## 质量门禁

代码规范与验收标准见 [AGENTS.md](AGENTS.md)，提交前须全部通过：

| 门禁     | 内容                                                                                                              |
| -------- | ----------------------------------------------------------------------------------------------------------------- |
| 构建     | `dotnet build --no-incremental` 0 警告 0 错误（增量模式会吞分析器告警）                                           |
| 禁用 API | `BannedApiAnalyzers`：`ScrollView`、`FontWeights`、`ContentDialog.ShowAsync` 等直接编译失败                        |
| 源码扫描 | `tests/YuumiX.SourceScan.Tests` 拦截硬编码颜色、字号字面量、非 4 倍数间距                                          |
| 单元测试 | `YuumiX.Core.Tests` 与 `YuumiX.App.Tests`，LCU 交互由 `YuumiX.Testing.Lcu` 提供本地仿真                            |
| 发布校验 | 流水线校验 `Assets` 完整性与程序集版本、tag 一致                                                                  |

## 必读文档

| 文档                                      | 作用                                     |
| ----------------------------------------- | ---------------------------------------- |
| [开发强制约定](AGENTS.md)                 | 工程目录结构、排版/颜色/控件规范、Reactor 约束与提交门禁 |
| [架构与实施规范](docs/internal/docbak/architecture.md)    | 分层契约、目录规划、选型决策（ADR 系列） |
| [架构验证报告](docs/internal/docbak/validation-report.md) | 技术路线与关键能力验证记录               |
| [联机验证清单](docs/internal/docbak/live-verification.md) | 真实客户端环境联机实测验收指南           |
| [更新日志](CHANGELOG.md)                  | 各版本面向用户的变更                     |

## 参与贡献

欢迎提交 Issue 与 PR。开始前请先读 [AGENTS.md](AGENTS.md) 与 [架构规范](docs/internal/docbak/architecture.md)：保持单进程架构，领域逻辑归入 `YuumiX.Core` 并补充测试，提交前跑通「质量门禁」。

UI 改动可用 `tools/ScreenCapture` 做前后截图比对：

```powershell
dotnet run --project tools/ScreenCapture -- shot.png YuumiX
```

## 许可证

仅供个人学习、技术研究与交流使用。
