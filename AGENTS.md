# AGENTS.md — YuumiX 开发强制约定

> 本文件对本仓库的**所有**代码改动生效，优先级高于个人偏好与"看起来能跑"。
> 技术栈固定：**WinUI 3（Windows App SDK 2.5.x）+ Microsoft.UI.Reactor 0.1.0-preview.16 + .NET 10**。
> 目标是**原生 Fluent Design**：字号、颜色、圆角、间距、控件一律走框架语义层，不手搓视觉。
> 详细方案见 `docs/ui-native-fluent-plan.md`；架构决策见 `docs/architecture.md` 的 ADR 系列。
> **注意**：`docs/` 目前在 `.gitignore` 中，不随仓库分发，上述及下文引用的 `docs/*` 文档仅作者本地可见；
> 克隆仓库的人以本文件为准。

---

## 0. 提交前必过的门禁

```bash
dotnet build src/YuumiX.App/YuumiX.App.csproj -p:Platform=x64                            # 必须 0 警告 0 错误
dotnet test tests/YuumiX.SourceScan.Tests/YuumiX.SourceScan.Tests.csproj -p:Platform=x64 # 模式级规则必须全绿
```

> 以上两条是**主门禁**，已落地为构建期 / 测试期自动拦截：
> - 符号级：`Microsoft.CodeAnalysis.BannedApiAnalyzers` + `src/YuumiX.App/BannedSymbols.txt`
>   （禁 `ScrollView`、`FontWeights`、`ContentDialog.ShowAsync`、`SyncWinUiAccentResources`），
>   配合 App 项目 `TreatWarningsAsErrors`，引用即构建失败
>   （`REACTOR_THEME_001` 是既有 `NoWarn` 例外，新增例外须评审）。
> - 模式级：`tests/YuumiX.SourceScan.Tests` 扫源码拦截硬编码 8 位 ARGB、字号字面量、非 4 倍数布局间距（栈内 <8px 微间距除外）、
>   裸字形字体名、从文案反推状态；存量白名单见 `GateWhitelist.json`，目前为空。
> - 暂无自动门禁、靠评审把关：空 catch（§10）、`.CornerRadius(数字)` 字面量（§3）。

以下六条 grep 是**兜底自检**（主门禁失效或换环境时跑），应保持为 0：

```bash
grep -rn "FontSize([0-9]" --include=*.cs src/YuumiX.App | wc -l     # 目标 0
grep -rn "FontWeights.Bold" --include=*.cs src/YuumiX.App | wc -l   # 目标 0（标题用 Title 工厂）
grep -rn "FontWeights\." --include=*.cs src/YuumiX.App | wc -l      # 目标 0（字重见 §1.1，禁止手写 FontWeights 常量）
grep -rn 'statusText != "' --include=*.cs src/YuumiX.App | wc -l    # 目标 0（禁止文案反推状态）
grep -rn "SyncWinUiAccentResources" --include=*.cs src | wc -l      # 目标 0（禁止覆盖系统强调色）
grep -rn "ScrollView(" --include=*.cs src/YuumiX.App | wc -l        # 目标 0（滚动容器只允许 ScrollViewer，见 §5.5）
```

---

## 1. 排版：只用语义工厂，禁止裸字号

**禁止**：`TextBlock(x).FontSize(13.5).FontWeight(FontWeights.SemiBold)`、任何 `.FontSize(...)` 字面量、`FontWeights.Bold`。

**必须**从 Reactor 排版工厂里选（括号内为实际字号/字重）：

| 场景 | 工厂 |
|---|---|
| 角标、时间戳、次要说明 | `Caption(x)`（12 / Regular） |
| 正文、列表主文本、表头 | `Body(x)`（14 / Regular） |
| 卡片标题、强调标签 | `BodyStrong(x)`（14 / SemiBold） |
| 强调正文 | `BodyLarge(x)`（18 / Regular） |
| 分组标题、二级标题 | `Subtitle(x)`（20 / SemiBold） |
| 页面标题、对话框标题 | `Title(x)`（28 / SemiBold） |
| 首屏主标题（每页最多一个） | `TitleLarge(x)`（40） |

> 需要 WinUI 命名样式（含行高等完整 setter）时用 `.ApplyStyle("BodyTextBlockStyle")`，
> **不要**用 `FontSize` 模拟。`Caption()` 是纯字号预设，要完整 Caption 样式请 `.ApplyStyle("CaptionTextBlockStyle")`。
> `Heading`（28 / Bold 700）、`SubHeading`（20 / SemiBold 600）是 Reactor 自带的便捷封装，并非弃用 API，且会自动带上 `HeadingLevel` 1/2。
> 但 `Heading` 与 `Title` 同级却字重不同（700 vs 600），会造成同一级标题两种观感——页面标题固定用 `Title`，禁止新增 `Heading`。
> 因 `Title`/`Subtitle` **不会**自动设置标题层级，必须手写 `.HeadingLevel(...)`（见 §4、§6）。

**最小字号 12px**。任何小于 12 的设计意图一律改为 12 + 降低字重/提高对比度解决，不得靠缩字号。
**禁止删除/绕过** `docs/ui-native-fluent-plan.md` §5.1 的映射表自行发明新档位。

### 1.1 字重：不许手写 `FontWeights.*`

工厂里**没有 12px 加粗档位**（`BodyStrong` 是唯一带 Strong 的工厂，14px）。所以 12px 想强调只有两条合规路径：

| 内容类型 | 做法 |
|---|---|
| 12px 中文 / 运行时多为中文的动态文本（昵称、段位、道具名、按钮文字） | **去字重**，层级改由 `Theme.PrimaryText` vs `Theme.SecondaryText` 对比度 + 既有边框/底色承担 |
| 12px 纯数值、拉丁文、符号（KDA、等级、胜率、徽标字形） | 用官方修饰符 `.SemiBold()`（`TextBlockElement.SemiBold()`），保留强调观感 |
| 14px 需要强调 | 换 `BodyStrong(x)` 工厂，不要再叠 `.FontWeight(...)` |
| ≥18px 需要强调 | 保留 `BodyLarge`/`Subtitle` 尺寸，用 `.SemiBold()` |

**为什么 12px 中文不能加粗**：雅黑没有 SemiBold 字重，字重 600 会被吸附到 Bold，小字号笔画粘连发糊——
这正是今天工具页/设置页观感变好的原因（`refactor(ui): 移除自定义字体栈，工具页排版对齐原生 Fluent`）。

**禁止**手写 `.FontWeight(Microsoft.UI.Text.FontWeights.SemiBold)`：字重是框架语义层的概念，
应走工厂或 `.SemiBold()`；手写常量既绕过类型阶，也让「哪些小字该加粗」失去可审的依据。

### 1.2 文字渲染：浏览器兜不了的底，必须自己守

Vue/网页里字体回退、字重合成、亚像素渲染都由浏览器兜底；WinUI 直连 DirectWrite 且做像素对齐，任何一处没对齐文字就发虚。历史三次踩坑（全局字体栈注入崩溃 `83fd2ef`、12px 中文加粗发糊、ScrollView 亚像素虚化）全是这一类。因此：

- **禁止注入全局字体/字体栈**：系统默认 Segoe UI Variable + 中文回退已是最优；"统一品牌字体"先按 §8 反问是否产品决策。
- **尺寸、偏移、圆角一律整数像素**，禁止小数——亚像素位置会让 DirectWrite 失去像素网格拟合。
- **禁止对文字做 Scale / Translation 动画**（动画期间文字脱离像素网格，结束时也可能停在半像素上）。
- 观感类改动必须用 Reactor devtools（`mur`）截图对比验证，不要肉眼反复试、更不要靠改字号/字重"救"清晰度。

---

## 2. 颜色：结构色走 `Theme.*`，业务色走 `palette`

| 类别 | 取值 | 例 |
|---|---|---|
| 结构色（文本、描边、卡片底、hover） | **`Theme.*`** | `Theme.PrimaryText`、`Theme.SecondaryText`、`Theme.DisabledText`、`Theme.CardBackground`、`Theme.CardStroke`、`Theme.SubtleFill`、`Theme.Accent`、`Theme.AccentText`、`Theme.SystemCritical`、`Theme.SystemCaution`、`Theme.SystemSuccess` |
| 业务色（用户可配置：胜负、死亡数字、重开） | `palette.WinText/WinBg/LoseText/LoseBg/Remake*/LightDeath/DarkDeath` | 只允许出现在战绩相关视图 |

规则：
- **禁止**在 `Pages/**` 新增 `#FF______` 硬编码颜色（图表/品牌位除外，且必须注释来源）。
- **禁止**覆盖 `SystemAccentColor*` / `AccentFillColor*` 等系统资源来"统一品牌色"——强调色由系统决定。
- `YuumiPalette` 只保留业务色派生；结构色字段只减不增。
- **强调色一律 `Theme.Accent` / `Theme.AccentText`**：旧的 `palette.Accent` 与 `ThemeColor` 配置项已清零，禁止复活；半透明强调底用 `Theme.SubtleFill` 等现成令牌。
- **禁止带透明度的手写 ARGB 充当结构色**（如 `"#20808080"` 当描边）：高对比度模式下全部失效，还会盖住 Mica；描边一律 `Theme.ControlStroke` / `Theme.CardStroke` / `Theme.DividerStroke`。
- 深色/浅色/高对比度三态必须同时成立：凡是"浅色下发白""深色下发黑"的写法，一律视为 bug。

**取舍**：业务色与结构色语义重叠时（如胜率着色），优先语义色（`Theme.SystemSuccess/Caution`），而非业务色板。

---

## 3. 圆角与间距：只有两档圆角，4px 网格

- 控件级（按钮、输入框、徽标）：`YuumiTheme.ControlRadius`（4），对应系统资源 `ControlCornerRadius`
- 容器级（卡片、对话框、浮层）：`YuumiTheme.OverlayRadius`（8），对应系统资源 `OverlayCornerRadius`
- 头像/等级环等胶囊元素的第三档是 `YuumiTheme.PillRadius`。
- **禁止**在 `.CornerRadius(...)` 里写数字字面量（当前存量为 0，由此保持）；需要主题资源形态时用 `ThemeResource.CornerRadius("ControlCornerRadius")`。
  注意 `Theme.ControlCornerRadius` 在 Reactor API 中**不存在**，不要猜。
- 布局间距只用 4 的倍数：4/8/12/16/20/24/32，禁止 9、10、11、13、14、15 这类数值。
  `HStack`/`VStack` 内 <8px 的图标/文字微间距可自由取值（1–8）；≥8px 仍走 4 的倍数。存量已清零，由 SourceScan 测试 `SpacingOff4Grid` 拦截。
- 卡片内边距统一 `16`（`SettingsCard` 已内置，无需手写）。

---

## 4. 控件：设置/工具类界面必须用 SettingsCard / SettingsExpander

- 设置项、工具箱开关一律用 `Controls/ToolkitControls.cs` 暴露的 `SettingsCard` / `SettingsExpander`。
- **禁止**再手写 `Border(...).CornerRadius(8).Background(Theme.CardBackground).WithBorder(Theme.CardStroke, 1)` 组合出来的"伪卡片壳"，也禁止用 `Button` 包裹整行来做可展开卡片。
- 需要纯视觉容器时优先用 Reactor 自带 `Card(Element)` 工厂（已内置背景 + 1px 描边 + 8px 圆角 + 16px 内边距）。
- 卡片状态用强类型枚举（`SettingStatus`），**禁止**从显示文案反推（`statusText != "未启用"` 这类写法一律不通过评审）。
- 分组标题必须 `Subtitle(title).HeadingLevel(AutomationHeadingLevel.Level2)`，禁止用 `TextBlock` + 字号模拟。
- **`SettingsExpander.Items` 内部子项必须为 `SettingsCard`**：CommunityToolkit 的 `SettingsExpanderItemStyleSelector` 返回固定 `TargetType = typeof(SettingsCard)` 的样式，传入非 `SettingsCard`（如裸 `VStack`）在展开实例化时会在原生 XAML 触发 `COMException 0x800F1000` 导致进程直接闪退（`0xc000027b`）。整块表单请用 `SettingsCardKit.ContentCard` 承载，多张子卡片必须拆为 `SettingsCard[]` 传入，严禁外层多包一层 `VStack`。
- **危险/不可逆操作不要手搓红色按钮**：WinUI 没有内置 danger 按钮样式，深色主题 `SystemFillColorCriticalBrush` 是浅粉色、压白字几乎不可读。用普通按钮 + `ContentDialog` 二次确认；确需红色时前景色必须按主题分别取值，禁止写死 hex。

---

## 5. Reactor 使用硬约束（违反会编译通过、运行崩溃或观感退化）

1. **元素必须通过工厂创建**：`TextBlock(...)` / `SettingsCard(...)`，禁止 `new XxxElement(...)` 直接构造 record。Reactor 的 handler 注册是惰性的，直接 new 会在挂载时抛 `No handler is registered for element type`。
2. **`with { }` 后不能直接链式修饰**：写成 `(Xxx(...) with { ... }).Modifier(...)`，或把回调写进 `with` 初始化器。
3. **对话框一律声明式**：`ContentDialog(title, content, primary) with { IsOpen = state, ... }`，禁止 `ShowAsync()`（分析器 `REACTOR_DIALOG_001`）。
4. **走 `IconData`/`ImageIcon` 的图标用位图 PNG**：`IconResolver` 解析成 `ImageIcon + BitmapImage`；`Image()` 会按 `.svg` 扩展名改用 `SvgImageSource`（静态支持良好，动画/动态支持有限），两条链路不要混用。需要 SVG 图标时先光栅化（如 `Assets/opgg-nav.png`）。
5. **滚动容器一律用 `ScrollViewer`，禁止 `ScrollView`**：`ScrollView` 把内容放在 `ScrollPresenter` 的独立 Composition Visual 上，靠浮点 `Visual.Offset` 驱动，**绕过 XAML 布局取整** → 内容落在亚像素位置 → DirectWrite 失去像素网格拟合 → **该滚动区内所有文字整体发虚**（笔画变胖、边缘过渡带约翻倍；实测归一化梯度 0.26 vs 0.40）。编译通过、零告警，只在观感上退化（上游同类现象：microsoft-ui-xaml#1579）。
   - 诊断判据：同一窗口内，滚动区**外**的元素（侧边栏图标、裸 `Border` 里的按钮）明显更锐利；与字号、字重、颜色、字体、运行时版本全部无关，改这些只会空转。
   - 焦点约定：承载表单的 `ScrollViewer` 加 **`.IsTabStop(true)` 修饰符**，**不要** `.Set(sv => sv.IsTabStop = true)`（控件走池回收，Set 写入会在重渲染时丢失，触发 `REACTOR_POOL_001`）。否则点空白会把焦点下发给第一个输入框（microsoft-ui-xaml#597）。
6. **`NavigationView.IsPaneOpen` 初值不能在渲染期赋**，必须 `Loaded` → `DispatcherQueue.TryEnqueue(Low, ...)` 延后校正，并用 `paneInitialized` 守卫 `PaneOpenChanged`（详见 `AppShell.cs` 现有实现）。
7. **事件订阅走 `OnMountAdd` / `OnUnmountAdd`，委托用 `UseRef` 缓存成同一实例**（`-=` 才能摘得掉）。
   在 `.Set(c => c.Event += h)` 里订阅会随每次重渲染叠加，触发 `REACTOR_EVENT_001`（见 §5.12）。
8. **`DispatcherQueue.TryEnqueue` 优先级参数在前**：`(priority, callback)`。
9. **`SettingsExpander.Items` 子项必须为 `SettingsCard`**：详见 §4（违反会 `COMException 0x800F1000` 闪退）。`SettingsCardKit.Expander` 已内置自动归一化兜底。
10. **渲染状态不得用类字段 + 强制重渲染**（`_renderTick` + `setTick(++_renderTick)`、`_proxyDraft` 这类模式）：草稿值用 `UseState`；配置对象用不可变 record + `UseState`/`UseReducer`；非渲染数据用 `UseRef`。命令式"踢一下"刷新会让状态来源不可追溯。
11. **异步数据走 `UseResource` / `UseMutation`**（详见 `agentkit/skills/async.md`）：读数据自带取消、加载/出错状态、缓存与重试；写操作用 `UseMutation`。禁止每页手写 `Task.Run` + 自维护 loading/error（存量约 20 处，主要散在 `ReplayButton`、`SavedPlayersLoaders`、`NoticePopupView`、`SearchEffects` 等；`AppShell` 只有 1 处，`AppHost` 的启动期后台任务不在此列），组件卸载后请求必须自动取消，禁止新增 `async void`。
12. **禁止在 `.Set()` 里挂事件或补写受控值**：控件走池回收，`Set` 每次重渲染都会重放，`Tag` 守卫不可靠（SearchPage AutoSuggestBox 的教训）。事件挂载用 `OnMountAdd`/`OnUnmountAdd` + `UseRef` 缓存委托（照 `AppShell` 现有做法）；受控值（如 ComboBox 选中项）走工厂参数，若确属 Reactor 缺陷则收成统一辅助方法，不要散在各页 `.Set()` 里补写。
13. **导航参数走路由对象，禁止全局单例 payload**（曾有 `CareerNavigationPayload` / `SearchNavigationPayload` 这种一边 Set 一边 Consume 的可变单例，有时序问题，现已清除，禁止复活）：`AppRoute` 已是 record 层级（如 `CareerRoute(string? Puuid, ...)`），新增页面参数一律加在对应路由 record 上。
14. **字形图标与字体族**：图标字体统一 `"Segoe Fluent Icons, Segoe MDL2 Assets"`（Win10 没装 Fluent 字体，只写前者会渲染成方框）；字形码集中在 `Icons` 静态类，禁止散落 `\uE...` 字面量；等宽字体只写 `"Cascadia Mono, Consolas"`，禁止 CSS 写法（`monospace` 不是 WinUI 字体名，会被静默丢弃）。

> 包内自带 API 索引与分析器文档：`~/.nuget/packages/microsoft.ui.reactor/0.1.0-preview.16/agentkit/`。
> 不确定某个 API 是否存在时**先查 `agentkit/plugins/reactor/skills/reactor-dsl/references/reactor.api.txt`**，不要猜。

---

## 6. 无障碍

- 每个交互控件必须 `.AutomationName(...)`；纯装饰元素 `.AccessibilityHidden()`。
- 标题层级：页面标题 `Level1`，分组标题 `Level2`，卡片标题按需 `Level3`。
- 图标按钮必须有 `ToolTip`。
- 禁止用颜色作为唯一信息载体（涨跌/胜负必须同时有符号或文字）。

---

## 7. 文件规模与结构

- `SettingsPage`/`ToolsPage` 这类聚合页只保留：状态编排、写回、滚动容器。
- 业务分节拆到 `Pages/<模块>/Sections/*.cs`，一个 `GroupHeader` 对应一个文件。
- 可复用的视觉构件（卡片壳、状态条、空态、加载态）放 `Controls/` 或 `Pages/<模块>/<模块>Visuals.cs`，禁止复制粘贴到第二个页面。
- 跨页面共享的提示/弹窗走全局宿主（`Composition/ToastHost`），禁止每个页面各写一套 toast 队列。

---

## 8. 主题与个性化边界

- 主题模式（浅色/深色/跟随系统）、Mica、战绩卡片配色、死亡数字配色 = **保留**的用户配置。
- **强调色不再由应用配置**：全局跟随系统 Accent。新增任何"品牌色"需求先反问是否为产品决策，不要默认加配置项。
- **业务色必须响应运行时主题切换**：`Theme.*` 会自动跟随，`palette.*` 依赖的 `IsDark` 不允许只读启动时的 `Application.Current.RequestedTheme`——"跟随系统"模式下运行中切换系统深浅，必须由根元素监听 `ActualThemeChanged` 写回实际主题并触发调色板重算，否则界面深浅混搭。
- 配置统一存放在数据目录的 `config.json`（路径见 `YuumiX.Core/Runtime/RuntimePaths.cs`，迁移期与旧版 Yuumi 共用）。
  新增配置项必须改 `AppConfig`（`YuumiX.Core/Persistence/AppConfig.cs`），且**不得丢弃未知键**（`JsonExtensionData`），否则会破坏与旧版的配置互通；敏感值不得写入仓库内任何文件。

---

## 9. 提交规范

- 中文 + conventional commit，带 scope：`feat(settings): ...`、`refactor(ui): ...`、`fix(tools): ...`；跨模块改动不带 scope。
- 一个批次一个提交，**不混合**"结构重构"与"视觉微调"。
- 禁止 `--no-verify`；禁止自动 `push`。
- 提交前 `git status` 确认没有误带构建产物（`bin/`、`obj/`、`.workbuddy/`、`docs/` 均在忽略列表，其中 `docs` 需手动带出到 worktree）。

---

## 10. 代码改动纪律

- **外科手术式修改**：只改任务相关的行，不做顺手重排、不做无关格式化。
- 先读后改：改动前必须读过目标文件与相邻实现，确认没有既有约定被破坏。
- 任何"为了让它编译过"的临时 hack（`#pragma`、空 catch、`!` 压制、`TODO` 留下）都不允许进入主干。
  存量：App 层约 7 处裸 `catch`（其中 `SettingsPage`、`SearchLoaders` 各有一处完全为空），Core 侧另有约 33 处（`GameInfoService` 最集中）；新代码必须至少记日志或写明为何可忽略，存量按批次处理。
- 不确定时写注释解释**为什么**，不解释**做了什么**（代码本身说明做了什么）。

---

## 11. 工程目录结构

```
YuumiX/
├── src/YuumiX.App/                  # 应用宿主（WinUI 3 + Reactor）
│   ├── App.cs                       # 应用入口
│   ├── Assets/                      # 静态资源（图标、Logo、段位与分路图标）
│   ├── Composition/                 # 服务宿主容器（DI + 日志 + 生命周期）
│   ├── Controls/                    # 通用组件（SettingsCardKit、LcuImage、Toast 等）
│   ├── Diagnostics/                 # 架构能力验证与诊断实验室
│   ├── Navigation/                  # 路由 record 层级（AppRoute）与导航元数据
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
├── docs/                            # 架构规范、验收记录与需求文档（本地，不入库）
├── Directory.Build.props            # 统一 MSBuild 属性与产品元数据
├── Directory.Packages.props         # 集中包版本管理（CPM）
├── global.json                      # .NET SDK 锁定
└── YuumiX.slnx                      # 解决方案配置
```

- 新增顶层目录或调整既有分层前，先更新本节的目录树与 `docs/architecture.md` 的对应章节。
- 上面标注的「禁止 UI 依赖」「禁止复制粘贴视觉构件」等约束，以本文件 §5、§7 为准。
