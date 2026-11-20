# CU Font Loader — Custom Font & Fallback Framework for Casualties: Unknown

`cu-font-loader` 是一个面向 **Casualties: Unknown** 的独立 BepInEx 插件，提供自定义字体替换与 CJK 字形回退（glyph fallback）框架，用于改善游戏内文字的可读性与无障碍体验。

- **Custom font replacement**：从外部加载 `.ttf` / `.otf`，运行时替换 TextMeshPro 字体
- **CJK glyph fallback**：缺字回退到游戏原字体，图标与符号不变成方块
- **CJK-only mode**：中文、日文假名与韩文谚文由插件字体补字，数字与英文符号保留游戏原字体（`PreserveNonCjk`）
- **Metric and retry modes**：`ScaleAware` 按源字体度量替换，`Consistent` 在字体被改回后再次应用
- **CJK replacement whitelist**：`ScienceUnifont`、`ConsoleFont` 等自带 CJK 字形的字体可优先用插件字体替换 CJK 文字（`CjkOverrideEnabled`，默认开启）
- **In-game settings panel**：在游戏「语言」设置页内切换开关、模式、字体与字重（CUCoreLib 为可选依赖）
- **Font weight selection**：同字族的 Thin / Light / Regular / Medium / Bold / Black 可直接切换
- **Mod UI compatibility**：保守的兼容策略，尽量不碰其他 Mod 的 UI、字体与材质
- **No game asset modification**：不修改游戏资源文件与 AssetBundle，卸载即恢复
- **Graceful degradation**：CUCoreLib 缺失时面板不注册，功能全部由配置文件驱动

> **v1.6.1** — 停用共享标点名单（`IsCjkPunctuation`）：弯引号、省略号、间隔号、破折号等中英共用标点不再按 CJK 字形处理，「仅替换中日韩字形」下改由游戏原字体绘制。
>
> 修复：缓存释放时没有销毁 `TMP_FontAsset` 的材质；字体加载或 profile 创建失败时会残留已创建的图集、材质和字体资产。两种情况现在都会完整释放。
>
> **v1.6.0** — 新增韩文（谚文）支持：`PreserveNonCjk` 的 CJK 判定加入谚文，韩文与中日文走同一条 fallback；面板项改名为「仅替换中日韩字形」「中日韩字体替换白名单」；「系统字体」新增「（未选择）」。
>
> 修复：解除白名单接管或切到“无字体”状态时，文本可能保留已释放的 profile 图集或材质，导致文字（包括控制台）空白——还原时会一并还原材料，无字体时清掉全部 fallback 链并强制重排。面板改动改为下一帧生效；切换字体或采样字号时释放旧 profile；新增空字形诊断日志，便于定位字体里“有码位、无轮廓”的字符。

## 来源与分支

| 项 | 值 |
|---|---|
| 上游项目 | [FenChen0211/cu-font-loader](https://github.com/FenChen0211/cu-font-loader) |
| 本仓库（fork） | [RedAsteroid/cu-font-loader](https://github.com/RedAsteroid/cu-font-loader) |
| 本分支 | `update`（基于上游 `feat/v1.4.0-pre.1-compatibility`） |
| 本分支版本 | v1.6.1 |
| 许可证 | MIT，上游版权归 FenChen0211；详见 [LICENSE](./LICENSE) |

本分支基于上游 `feat/v1.4.0-pre.1-compatibility` 分支继续开发（并非从 `main` 直接拉出），与上游 `main` 的行为不完全一致。反馈请提交到 [Issues](https://github.com/RedAsteroid/cu-font-loader/issues)，附 BepInEx 日志更佳。

## 安装

1. 确保游戏已安装 BepInEx（5.x）。
2. 把插件解压到游戏根目录：

```text
BepInEx/
  plugins/
    cu-font-loader/
      cu-font-loader.dll
      fonts/            <- 字体放这里（首次启动自动创建）
```

3. 把 `.ttf` / `.otf` 放进 `fonts/`，启动游戏一次即生成配置：`BepInEx/config/fenchen.cu-font-loader.cfg`。

## 游戏内设置面板

安装 [CUCoreLib](https://github.com/jimmyking9999999/CUCoreLib/releases)（1.1+）后，插件在游戏「语言」设置页注册以下选项；未安装时选项不出现，功能不受影响。

| 面板项 | 作用 |
|---|---|
| 启用字体替换 | 总开关；关闭后还原 TMP 文本、旧版 `UI.Text` 与输入框 |
| 替换模式 | `FallbackOnly` / `Safe` / `Normal` / `Aggressive` / `ScaleAware` / `Consistent`，切换后重新应用 |
| 采样字号 | 图集采样尺寸（默认 36 px）；只重建当前生效字体，切回用过的档位直接复用 |
| 字体文件 | `fonts/` 内的字体；第一项「（未选择）」清除该选择，关闭「系统字体优先」时留空则不替换 |
| 系统字体 | Windows 已安装字体；「系统字体优先」开启时作为字体来源，第一项「（未选择）」清除该系统字体 |
| 系统字体优先 | 决定字体来源：开启用系统字体、关闭用字体文件；所选来源未选择时不替换文本，由游戏原字体渲染 |
| 字重选择 | 当前生效字体的同族字重；无可用项时显示「（未选择）」 |
| 仅替换中日韩字形 | 不改字体：中文、日文假名与韩文谚文由插件字体作为首选 fallback 补字，数字/英文保持游戏原字体 |
| 中日韩字体替换白名单 | 仅「仅替换中日韩字形」开启时有效；把白名单字体（默认 `ScienceUnifont`、`ConsoleFont`）的文本换成 CJK-only profile，默认开启 |
| 行距对齐 | 仅「仅替换中日韩字形」开启时有效；把插件字体的垂直度量对齐到当前界面最常用的游戏字体，切换后重新对齐 |

- cfg 是唯一的配置来源：面板改动立即写入 cfg，打开设置页时按 cfg 显示
- 面板改动在下一帧重新应用，设置行先完成回调，避免设置页文字被重排打断
- 「字体文件」下拉每次打开都重新扫描 `fonts/`，运行中新增的字体无需重启
- 面板文案跟随游戏语言：中文显示中文，其它语言显示英文

## 字重选择

字重按**文件名**识别：去掉 `Thin` / `ExtraLight` / `Light` / `Regular` / `Medium` / `SemiBold` / `Bold` / `ExtraBold` / `Black` 等关键字后，剩余前缀即字族。

```text
HarmonyOS_Sans_SC_Light.ttf   -> 字族 HarmonyOS_Sans_SC，字重 Light
HarmonyOS_Sans_SC_Bold.ttf    -> 字族 HarmonyOS_Sans_SC，字重 Bold
HarmonyOS_Sans_SC.ttf         -> 字族 HarmonyOS_Sans_SC，字重 Regular
```

- 只列出**当前生效来源**中的同族文件：系统字体优先关闭时查 `fonts/`，开启时查系统字体目录
- 文件名不含关键字的，按该族的 `Regular` 处理；同族只有一个文件时就只有一项
- 选中字重等同于切换到对应文件，随后重新应用
- **可变字体**（单文件多字重）只支持默认字重：Unity 2022 无可变字体轴接口，面板会标注「（可变字体，仅默认字重）」

## 替换模式（ReplaceMode）

```ini
[General]
Enabled = true
ReplaceMode = Normal
ScanIntervalSeconds = 1
```

- `Enabled`：总开关，等价于面板「启用字体替换」
- `ScanIntervalSeconds`：扫描新文本的间隔（默认 1 秒，最低 0.25）。开启「仅替换中日韩字形」后，正常情况下由 `OnEnable`、`TMP_Text.font` 和 `TMP_Text.text` 钩子即时处理，不再按这个间隔整段扫描；钩子不可用时才退回分帧增量扫描

| 模式 | 行为 | 适合情况 |
|---|---|---|
| `FallbackOnly` | 只把外部字体加入 TMP fallback，不替换文本；切到此模式会撤销已有替换 | 最安全；只补 CJK 缺字 |
| `Safe` | 只替换"普通"文本：跳过含 `<sprite>`、自定义材质、自定义 Shader 的文本 | 想替换字体但装了不少 Mod |
| `Normal` | 替换大多数文本，保留其他 Mod 的 UI 与材质（默认） | 推荐的日常模式 |
| `ScaleAware` | 按原字体的 scale、行高和基线创建匹配 profile 再替换 | 替换后希望布局不跳 |
| `Consistent` | 与 `Normal` 相同，但文本被游戏改回原字体后会再次替换 | 游戏或 Mod 会延迟设置字体 |
| `Aggressive` | 接近旧版 `Persistent`：每轮强制替换所有未排除文本，含自定义材质 | 明确需要强制全局替换 |

优先使用 `Normal`；替换后行高或基线变化明显时改用 `ScaleAware`。

`Safe` / `Normal` / `ScaleAware` 对每个文本和输入框只替换一次；`Consistent` 在检测到字体被改回快照原字体时再次替换；`Aggressive` 每轮强制替换。切换模式后重新应用，被新模式判定为跳过的文本会回退到游戏字体。`ScaleAware` 和 `Consistent` 在「仅替换中日韩字形」开启时不参与，行为与 `Normal` 相同。

### 从旧版本迁移

| 旧配置 | 新行为 | 说明 |
|---|---|---|
| `ReplaceMode = FallbackOnly` | 保持 `FallbackOnly` | 不变 |
| `ReplaceMode = ReplaceOnce` | 迁移为 `Normal` | 首次启动时改写 cfg 并写日志 |
| `ReplaceMode = Persistent` | 迁移为 `Aggressive` | 同上 |
| `ReplaceAllText = true` | 已废弃 | 由 `ReplaceMode` 承接 |
| `PrimaryFont = ...` | 已废弃 | 由 `FontFile` 或面板承接 |

迁移会改写 cfg 中的旧值。回退到 v1.3.2 时旧版不识别 `Normal` / `Aggressive`，会按 `FallbackOnly` 处理（重新设置即可）。

## 字体加载与缓存

```ini
[Font]
AtlasSize = 4096
SamplingPointSize = 36
AtlasPadding = 5
MaxCachedSamplingSizes = 3
MaxFontProfiles = 4
PreserveNonCjk = true
AlignNativeMetrics = true
```

- **按需加载**：启动只读取当前生效的字体，其余字体在被选中时才读取。日志会记录每次加载的耗时：

  ```text
  Loading: hywh65W.ttf
    OK: hywh65W (4096x4096 dynamic atlas, sampling 36, 47 ms)
  ```

- **按字号缓存**：缓存以（文件, 采样字号）为键，切到新字号只新建 1 个，切回用过的字号直接复用：

  ```text
  Sampling size = 48: built 'hywh65W.ttf'; 2 asset(s) cached, ...
  Sampling size = 36: 'hywh65W.ttf' is already cached at this size (2 asset(s) cached).
  ```

- **缓存有上限**：每个字体最多保留 `MaxCachedSamplingSizes` 个字号（默认 `3`，`0` = 不限）。超出时在一次完整重新应用后销毁最久未用的资产（图集纹理 + 字体数据），再次切回该字号会重新解析文件：

  ```text
  Released cached font asset 'hywh65W' (sampling 36).
  [Cache] Released 1 superseded font asset(s); 3 kept (limit 3 per file).
  ```

- 销毁前会确认没有文本、输入框、fallback 表或快照还引用该资产；当前字体与当前字号不会被释放
- **单字号文件不会被释放**：上限是按文件计算的，一个文件只缓存过一个字号时不会淘汰它。切换过多个字体时，每个字体都会留下至少一张图集，显存随选过的字体数量累加
- `MaxFontProfiles`：每个源字体一份度量 profile，默认最多同时保留 `4` 份（`0` = 不限），最近使用的优先；被引用的 profile 不会释放，达到上限时新的源字体退回主字体。每份 profile 同样使用 `AtlasSize²` 图集，`4096²` 时约 16.7 MB
- 白名单字体（`CjkOverrideFonts`）走强制分支，不受 `MaxFontProfiles` 限制；每个白名单字体各占一份 profile，名单越长占用越高
- 切换字体或采样字号后，旧 primary 建出的 profile 会被释放，不会随着反复切换累积占用
- 每个资产图集为 `AtlasSize²`（Alpha8，`4096² ≈ 16.7 MB`），CJK 字形多时还会追加图集页
- `AtlasSize` 建议 `4096`；显存紧张可降为 `2048`，代价是更容易出现缺字
- `SamplingPointSize` 是图集采样分辨率，**不改变界面文字的实际大小**；数值越大边缘越锐利，显存占用越高

字体选择键：

```ini
[General]
FontFile = HarmonyOS_Sans_SC_Light.ttf
SystemFont = HarmonyOS_Sans_SC_Bold
SystemFontPriority = false
```

- `FontFile`：`fonts/` 内的文件名（含扩展名，不区分大小写），「系统字体优先」关闭时由它接管；关闭且留空则不替换，由游戏原字体渲染
- `SystemFont`：系统字体名（不含扩展名），仅在「系统字体优先」开启时作为字体来源；关闭时即使有值也不生效
- 选中的文件**不存在**时自动清空并写日志；文件存在但**加载失败**时保留选择并重试

## 仅替换中日韩字形（PreserveNonCjk）

```ini
[Font]
PreserveNonCjk = true
```

对应面板「仅替换中日韩字形」，默认开启（旧配置保持原值），推荐中日韩用户使用。开启后**不改写 TMP 文本的字体**（旧版 `UI.Text` 见下表）：文本继续使用游戏原来的 FontAsset（数字、英文字母、符号与材质保持原生；行距默认按参考字体对齐，见下文「行距对齐」），插件字体只作为该字体的**首选 fallback**，用于补上它画不出来的 CJK 字形（中文、日文假名、韩文谚文）：

| 文本 | 行为 |
|---|---|
| 含 CJK 字符 | 字体仍是游戏原字体；数字/英文/符号按原字体绘制，CJK 字形由插件字体补上 |
| 完全不含 CJK 字符 | 完全不参与，字体/材质/行距保持原样 |
| 旧版 `UI.Text` | 没有逐字 fallback：只有“纯 CJK（仅 CJK 字符与空白）”的标签才整段换成插件字体，其余保持原生 |
| `TMP_InputField` | 字体保持原样，输入 CJK 文本时由 fallback 补字 |

- **英文用户不要开启**：开启后英文文本仍由游戏原字体渲染，所选英文字体不会生效；需要字体作用于英文时请关闭本项
- 插件字体被插入到**文本原字体的 fallback 链首位**（同时进入 TMP 全局 fallback 首位），因此 CJK 字形优先用插件字体，而不是游戏/翻译 Mod 的字体
- 不改 `text.text`、不注入富文本标签、不加组件、不换材质：数字/英文的字体与材质保持原生，行距由下方「行距对齐」决定
- 因为不再"每个文本换字体"，开启/切换后不会再有 0–1 秒的替换延迟或闪烁，新文本也不需要等待扫描
- 性能：新文本由 `OnEnable` 钩子处理，字体和文本变更由 `TMP_Text.font` / `TMP_Text.text` setter 钩子处理；正常情况下不再按 `ScanIntervalSeconds` 全量扫描。钩子不可用时改用每帧最多 24 个文本、1.5 ms 的分帧增量扫描。混排文本会多出一个由 fallback 材质绘制的 sub-mesh，同一画布按材质合批，通常只增加少量 draw call
- **行距对齐**（`[Font] AlignNativeMetrics`，默认开启，面板可切换）：混排行高取行内字符所属字体的 ascender/descender 最大值，插件会把自己的垂直度量按比例对齐到**当前界面里 CJK 文本最常用的那款游戏字体**（每轮扫描统计），并在参考字体变化时重新对齐；面板切换立即生效
- 退出该模式（关闭开关、切到 `FallbackOnly`、禁用插件、取消字体选择）时，会把插件字体从所有被扩展的 fallback 链中移除，并还原曾被替换的文本
- 开启时仍会剔除插件字体里已有的非 CJK 字形，CJK 字形按需加入图集；弯引号、省略号、间隔号、破折号等中英共用标点按非 CJK 处理，始终由游戏原字体绘制
- **韩文与中日文走同一条路径**：所选字体带谚文字形时由插件字体补字，字体缺该码位时继续沿 fallback 链降级到游戏原字体；如果字体把码位映射成**空轮廓**，TMP 会认为找到了并画出空白、不会降级——开启 `LogTextDetails` 或 `CompatibilityLog` 后会输出 `[Native] '…' maps U+XXXX to an empty glyph`，便于据此排查翻译文本
- 不修改预制体（prefab）里的文本：只处理屏幕上真实存在的对象，避免把插件字体写进预制体后，新生成的界面元素一出生就带着插件字体
- 少数界面元素是从"替换期间被改过的模板"克隆出来的，没有自己的原字体快照；插件会把它换成一款游戏原生字体（优先选当前界面 CJK 文本最常用的那款），而不是让它继续用插件字体
- `Safe` / `Normal` / `Aggressive` / `ScaleAware` / `Consistent` 下行为一致（本模式不改写字体/材质，排除规则与材质保护自然也不参与）；`FallbackOnly` 下不生效
- 日志：`[Native] native font coverage: …` 打印当前可达的原生字体；面板切到「开启」后的下一次扫描会自检并输出 `[Preserve] check after switching on: …`，正常时确认所有非 CJK 字符都由游戏字体绘制，异常时给出文本数量与对象名
- 改 cfg 后需重启；游戏内切换立即生效

## 中日韩字体替换白名单（CjkOverrideEnabled）

```ini
[Font]
CjkOverrideEnabled = true
CjkOverrideFonts = ScienceUnifont, ConsoleFont
```

部分字体（典型是 `ScienceUnifont`、`ConsoleFont`）自己带了一套 CJK 字形，TMP 会优先使用源字体里已有的字形，普通 fallback 补不进去。这个开关只对白名单源字体生效：把文本的 `text.font` 换成该字体的 **CJK-only profile**，CJK 文字由插件字体绘制，非 CJK 字符仍 fallback 到源字体，因此英文、数字和材质保持原生。

- 默认开启；使用前需先开启「仅替换中日韩字形」
- 关闭「仅替换中日韩字形」后本开关不改变替换方式：替换模式下这些字体和其它字体一样被整体替换（数字、英文一起换），`FallbackOnly` 下不被替换
- 关闭白名单或离开「仅替换中日韩字形」时，会还原被接管的字体与材质（含自定义材质的克隆）；关闭状态下切换本开关也会做一次残留还原
- `CjkOverrideFonts` 是逗号分隔的子串匹配，默认包含 `ScienceUnifont`（结局相关界面）和 `ConsoleFont`（控制台），留空等于关闭特例
- `Safe` / `Normal` / `Aggressive` 下都生效；`FallbackOnly` 下不生效
- 自定义材质会先尝试克隆，保留 outline / 辉光；`PreserveCustomMaterials = true` 且克隆失败时放弃接管，避免把 UI 弄坏
- 和 PreserveNonCjk 的普通 fallback 互斥：命中白名单的文本走接管，其余文本继续用 fallback
- 只覆盖 TMP；旧版 `UI.Text` 的混合 CJK 与英文标签仍只支持纯 CJK 整段替换

## 兼容性保护（Compatibility）

其他 Mod 可能创建自己的 TMP UI，使用自定义 `TMP_FontAsset`、Glow / Outline 材质、自定义 Shader 或图标字体。原则是**宁可少替换，也不破坏其他 Mod 的 UI**。

```ini
[Compatibility]
PreserveCustomMaterials = true
ExcludedFontNames =
ExcludedMaterialNames =
ExcludedObjectNames =
```

### PreserveCustomMaterials

- `true`（默认）：`Safe` / `Normal` 下，材质不是当前字体默认材质的文本（描边、辉光、自定义 Shader 等）**整体跳过**——不换字体，也不动材质
- `false`：`Safe` / `Normal` 也替换这些文本的字体与材质
- `Aggressive`：无论此选项为何值，都按 v1.3.2 的强制行为替换

字体与材质替换是分离的：默认只改 `text.font`，仅当原材质就是原字体的默认材质时才同步换材质；关闭保护或使用 `Aggressive` 时强制同步。

### 排除规则

三个选项都是**逗号分隔的关键字列表**，不区分大小写，**包含匹配**，留空表示不排除：

```ini
ExcludedFontNames = FancyModFont, IconFont
ExcludedMaterialNames = FancyGlow
ExcludedObjectNames = SomeModMenuTitle, SomeModIcon
```

- `ExcludedFontNames`：按当前字体（FontAsset）名称匹配
- `ExcludedMaterialNames`：按当前材质名称匹配
- `ExcludedObjectNames`：按 GameObject 名称匹配（TMP 与旧版 `UnityEngine.UI.Text` 均生效）

排除规则在替换模式（含 `Aggressive`）下生效，修改后立即生效，命中的文本回退到游戏字体；「仅替换中日韩字形」下普通 TMP 文本不受影响，白名单接管与旧版 `UI.Text` 标签仍会应用排除。

### 已知限制

- 插件无法可靠识别"其他 Mod 创建的字体"——Mod 字体与游戏字体在运行时没有可靠区别，需要靠排除规则处理
- 含 `<sprite>` 标签的文本在 `Safe` 模式被跳过；`Normal` / `Aggressive` 会替换其字体（精灵图不受字体替换影响）
- 替换模式下，部分游戏 UI 使用非默认材质，`Normal` 会跳过它们，看起来像"没替换到"，其实是保护机制在工作，可用 CompatibilityLog 确认；`PreserveNonCjk` 模式不改材质，不受此项影响

## 调试（Debug）

```ini
[Debug]
CompatibilityLog = false
MaxCompatibilityLogs = 200
LogTextDetails = false
MaxLoggedTexts = 80
```

排查"某个 UI 为什么没被替换 / 被误替换"时，把 `CompatibilityLog` 改为 `true`，插件输出单行决策记录：

```text
[Compat] Scene=MainMenu Object=SomeModMenuTitle Font=FancyModFont Material=FancyGlowMaterial Action=Skipped Reason=CustomMaterial
[Compat] Scene=Game Object=DamageText Font=LibSans SDF Material=LibSans SDF Atlas Action=Replaced From=PixelFont
[Compat] Scene=Game Object=DamageText Font=LibSans SDF Material=LibSans SDF Atlas Action=MaterialFixed From=TemporaryGlow
```

- `Action=Replaced` 时 `From=` 显示替换前的字体
- `Action=MaterialFixed` 表示 `Aggressive` 只修复了材质，`From=` 显示替换前的材质
- `Reason=` 为跳过原因：`Excluded`、`ExcludedMaterial`、`CustomMaterial`、`CustomShader`、`SpriteTag`、`MaterialMissing`、`FontMissing`、`NativeNonCjk`
- 同一对象的同一决策只记录一次；整个会话最多 `MaxCompatibilityLogs` 条

`LogTextDetails` 是详细诊断开关，会列出扫描到的 TMP 文本及其逐字字体统计（`glyphs=…`），仅在深度排查时使用；开启后也会启用 `[Native]` 详细日志（fallback 挂载、字形接管、空字形诊断）。设置页字体统计只在 `LogTextDetails` 或 `CompatibilityLog` 开启时计算。

## 语言切换刷新

部分本地化 UI 在启动早期生成，需重载本地化界面后才会完全刷新。若仍有少量字体未变，可在游戏内切换一次语言（中文 → English → 中文），场景重载后插件会重新扫描。

## 替换原理

插件不修改游戏资源包：

```text
某个 TMP 文本原本使用游戏字体 A
插件把它改成外部字体 B（仅当原材质为默认材质时同步材质）
B 的 fallback 链只包含游戏原始字体
缺字 → 游戏原始字体；插件字体之间不会互相接替
```

- 缺字、图标与符号由游戏原字体渲染，不会变方块，也不会沿用上一轮选过的字体
- `FallbackOnly` 不改变任何文本，只把 B 加入 TMP 全局 fallback
- 关闭插件、选择「（未选择）」或字体文件被删除时，已替换的 TMP 文本、旧版 `UI.Text` 与输入框还原为游戏字体，并清理插件加入的 fallback 条目

开启 `PreserveNonCjk` 后不再替换文本字体，而是把 B 挂到 A 的 fallback 链首位：

```text
文本继续使用 A：数字、英文、符号、材质与行距都由 A 原样绘制
A 查不到的 CJK 字形 → B（链首）；B 也没有 → 链上其余字体 → 全局 fallback
纯数字/英文文本：完全不参与，A 的字体、材质与行高都不变
```

## 其它限制

- **可变字体**只渲染默认字重：Unity 2022.3 的 TextCore / TMP 无可变字体轴接口
- **字重识别基于文件名**：`msyhbd.ttf` 这类不含关键字的名字无法归入同族，只显示单一字重
- **格式支持**：仅支持 `.ttf` / `.otf` 格式字体
- **系统字体**只在「系统字体优先」开启时使用
- 外部字体**不含 CJK 字形**时，CJK 字形由游戏原字体渲染（游戏字体也没有则缺字）；韩文同样要求字体本身带谚文字形
- 销毁缓存资产后切回该字号会重新解析文件（LRU 销毁机制）
- **仅替换中日韩字形**下，行距按 `AlignNativeMetrics` 对齐到当前界面最常用的游戏字体；若某条文本的原字体与参考字体度量差异很大，仍可能有细微差别，可在面板「行距对齐」里关闭它，改用插件字体自身度量
- 新文本发现依赖 TMP 渲染器的 `OnEnable` 钩子；钩子安装失败时退回周期扫描（≤ `ScanIntervalSeconds`），`PreserveNonCjk` 下退回分帧增量扫描
- 替换模式下，回落到游戏字体的字符使用游戏字体自身的材质；`PreserveCustomMaterials = true` 会跳过使用自定义材质的文本（`PreserveNonCjk` 模式不换材质，不受此影响）
- `ScaleAware` 的 profile 受 `MaxFontProfiles` 限制；被引用的 profile 不会释放，达到上限时对应文本退回主字体，行高可能与原文体不同
- `Consistent` 只在文本回到快照原字体或同族字体时重试；其他 Mod 设置的自定义字体不会被覆盖
- 旧版 `UI.Text` 没有逐字回退能力，「仅替换中日韩字形」只对“纯 CJK”标签整段替换，含数字/英文的保持原生；`CjkOverrideEnabled` 也只覆盖 TMP

## 编译

项目不内置游戏路径，编译时需指定游戏安装目录，解析顺序：

1. `-p:GameDir=...`
2. Steam 注册表 `HKCU\Software\Valve\Steam\SteamPath` + `\steamapps\common\Casualties Unknown`（仅完整框架 MSBuild 可用，`dotnet build` 跳过）
3. `cu-font-loader.csproj` 自定义路径

命令行传入 `GameDir`。下面是示例命令，`D:\path\to\Casualties Unknown` 是占位路径，需要换成你自己的游戏安装目录：

```powershell
# 示例：把路径替换为实际的游戏安装目录
dotnet build -p:GameDir="D:\path\to\Casualties Unknown"
```

也可用 Visual Studio 2022 打开 `cu-font-loader.sln` 生成（`net48`）。输出：

```text
bin/cu-font-loader.dll
```

`GameDir` 指向包含 `BepInEx/` 与 `CasualtiesUnknown_Data/` 的游戏根目录。`CUCoreLib` 只在运行时通过反射探测，编译不依赖它。

## 项目结构

```text
cu-font-loader/
  cu-font-loader.csproj   MSBuild 工程（net48，引用游戏程序集）
  cu-font-loader.sln      Visual Studio 解决方案
  FontLoaderPlugin.cs     插件主体：配置、扫描、替换、fallback、还原
  NativeGlyphGuard.cs     CJK 字形守卫：TMP 加字前缀与空字形诊断、CJK 判定、字形清洗
  FontSettingsPanel.cs    设置面板：面板项注册（运行时反射接 CUCoreLib，无编译期引用）、钩子、双语文案
  TextPipelineHooks.cs    新文本发现：TMP OnEnable、字体与内容的变更钩子
  LICENSE                 MIT 许可证
```

## 许可

MIT，详见 [LICENSE](./LICENSE)。

- 原始项目与上游版权：Copyright (c) 2026 FenChen0211（[FenChen0211/cu-font-loader](https://github.com/FenChen0211/cu-font-loader)）
- 本分支（fork）的修改与维护：[RedAsteroid/cu-font-loader](https://github.com/RedAsteroid/cu-font-loader)

保留原始版权与许可声明即可自由使用、修改与再分发。
