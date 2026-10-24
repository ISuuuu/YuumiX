# AGENTS.md — YuumiX 开发强制约定

> 本文件对本仓库的**所有**代码改动生效，优先级高于个人偏好与"看起来能跑"。
> 技术栈固定：**WinUI 3（Windows App SDK 2.5.x）+ Microsoft.UI.Reactor 0.1.0-preview.16 + .NET 10**。
> 目标是**原生 Fluent Design**：字号、颜色、圆角、间距、控件一律走框架语义层，不手搓视觉。
> 详细方案见 `docs/ui-native-fluent-plan.md`；架构决策见 `docs/architecture.md` 的 ADR 系列。

---

## 0. 提交前必过的三道门

```bash
dotnet build src/YuumiX.App/YuumiX.App.csproj -p:Platform=x64   # 必须 0 警告 0 错误
```

并且以下五条 grep 必须为 0（新代码不得新增违规；历史欠账按批次清零）：

```bash
grep -rn "FontSize([0-9]" --include=*.cs src/YuumiX.App | wc -l     # 目标 0
grep -rn "FontWeights.Bold" --include=*.cs src/YuumiX.App | wc -l   # 目标 0（标题用 Heading 工厂）
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
| 页面标题、对话框标题 | `Title(x)`（28 / SemiBold）或 `Heading(x)`（28 / 700） |
| 首屏主标题（每页最多一个） | `TitleLarge(x)`（40） |

> 需要 WinUI 命名样式（含行高等完整 setter）时用 `.ApplyStyle("BodyTextBlockStyle")`，
> **不要**用 `FontSize` 模拟。`Caption()` 是纯字号预设，要完整 Caption 样式请 `.ApplyStyle("CaptionTextBlockStyle")`。

**最小字号 12px**。任何小于 12 的设计意图一律改为 12 + 降低字重/提高对比度解决，不得靠缩字号。
**禁止删除/绕过** `docs/ui-native-fluent-plan.md` §5.1 的映射表自行发明新档位。

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
- 深色/浅色/高对比度三态必须同时成立：凡是"浅色下发白""深色下发黑"的写法，一律视为 bug。

**取舍**：业务色与结构色语义重叠时（如胜率着色），优先语义色（`Theme.SystemSuccess/Caution`），而非业务色板。

---

## 3. 圆角与间距：只有两档圆角，4px 网格

- 控件级（按钮、输入框、徽标）：`Theme.ControlCornerRadius`（4）
- 容器级（卡片、对话框、浮层）：`OverlayCornerRadius`（8）
- 头像/等级环等胶囊元素如需第三档，**必须**在 `YuumiTheme` 定义为单一常量 `PillRadius`，不得就地写字面量。
- 间距只用 4 的倍数：4/8/12/16/20/24/32。禁止 9、10、11、13、14、15 这类数值。
- 卡片内边距统一 `16`（`SettingsCard` 已内置，无需手写）。

---

## 4. 控件：设置/工具类界面必须用 SettingsCard / SettingsExpander

- 设置项、工具箱开关一律用 `Controls/ToolkitControls.cs` 暴露的 `SettingsCard` / `SettingsExpander`。
- **禁止**再手写 `Border(...).CornerRadius(8).Background(Theme.CardBackground).WithBorder(Theme.CardStroke, 1)` 组合出来的"伪卡片壳"，也禁止用 `Button` 包裹整行来做可展开卡片。
- 需要纯视觉容器时优先用 Reactor 自带 `Card(Element)` 工厂（已内置背景 + 1px 描边 + 8px 圆角 + 16px 内边距）。
- 卡片状态用强类型枚举（`SettingStatus`），**禁止**从显示文案反推（`statusText != "未启用"` 这类写法一律不通过评审）。
- 分组标题必须 `Subtitle(title).HeadingLevel(AutomationHeadingLevel.Level2)`，禁止用 `TextBlock` + 字号模拟。

---

## 5. Reactor 使用硬约束（违反会编译通过、运行崩溃或观感退化）

1. **元素必须通过工厂创建**：`TextBlock(...)` / `SettingsCard(...)`，禁止 `new XxxElement(...)` 直接构造 record。Reactor 的 handler 注册是惰性的，直接 new 会在挂载时抛 `No handler is registered for element type`。
2. **`with { }` 后不能直接链式修饰**：写成 `(Xxx(...) with { ... }).Modifier(...)`，或把回调写进 `with` 初始化器。
3. **对话框一律声明式**：`ContentDialog(title, content, primary) with { IsOpen = state, ... }`，禁止 `ShowAsync()`（分析器 `REACTOR_DIALOG_001`）。
4. **走 `IconData`/`ImageIcon` 的图标必须是位图 PNG**：`IconResolver` 解析成 `ImageIcon + BitmapImage`，不支持 SVG（静默失败）。需要 SVG 时先光栅化（如 `Assets/opgg-nav.png`）。
5. **滚动容器一律用 `ScrollViewer`，禁止 `ScrollView`**：`ScrollView` 把内容放在 `ScrollPresenter` 的独立 Composition Visual 上，靠浮点 `Visual.Offset` 驱动，**绕过 XAML 布局取整** → 内容落在亚像素位置 → DirectWrite 失去像素网格拟合 → **该滚动区内所有文字整体发虚**（笔画变胖、边缘过渡带约翻倍；实测归一化梯度 0.26 vs 0.40）。编译通过、零告警，只在观感上退化（上游同类现象：microsoft-ui-xaml#1579）。
   - 诊断判据：同一窗口内，滚动区**外**的元素（侧边栏图标、裸 `Border` 里的按钮）明显更锐利；与字号、字重、颜色、字体、运行时版本全部无关，改这些只会空转。
   - 焦点约定：承载表单的 `ScrollViewer` 加 **`.IsTabStop(true)` 修饰符**，**不要** `.Set(sv => sv.IsTabStop = true)`（控件走池回收，Set 写入会在重渲染时丢失，触发 `REACTOR_POOL_001`）。否则点空白会把焦点下发给第一个输入框（microsoft-ui-xaml#597）。
6. **`NavigationView.IsPaneOpen` 初值不能在渲染期赋**，必须 `Loaded` → `DispatcherQueue.TryEnqueue(Low, ...)` 延后校正，并用 `paneInitialized` 守卫 `PaneOpenChanged`（详见 `AppShell.cs` 现有实现）。
7. **事件钩子委托必须跨渲染保持同一实例**（`UseRef` 缓存），否则触发 `REACTOR_EVENT_001`。
8. **`DispatcherQueue.TryEnqueue` 优先级参数在前**：`(priority, callback)`。

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

- 单文件**上限 300 行**。`SettingsPage`/`ToolsPage` 这类聚合页只保留：状态编排、写回、滚动容器。
- 业务分节拆到 `Pages/<模块>/Sections/*.cs`，一个 `GroupHeader` 对应一个文件。
- 可复用的视觉构件（卡片壳、状态条、空态、加载态）放 `Controls/` 或 `Pages/<模块>/<模块>Visuals.cs`，禁止复制粘贴到第二个页面。
- 跨页面共享的提示/弹窗走全局宿主（`Composition/ToastHost`），禁止每个页面各写一套 toast 队列。

---

## 8. 主题与个性化边界

- 主题模式（浅色/深色/跟随系统）、Mica、战绩卡片配色、死亡数字配色 = **保留**的用户配置。
- **强调色不再由应用配置**：全局跟随系统 Accent。新增任何"品牌色"需求先反问是否为产品决策，不要默认加配置项。
- 新增配置项必须同时改三个文件并保持节结构一致：`appsettings.json`、`appsettings.Development.json`、`appsettings.Development.local.json`（敏感值只放 `.local.json`）。

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
- 不确定时写注释解释**为什么**，不解释**做了什么**（代码本身说明做了什么）。
