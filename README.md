# CU Font Loader — Custom Font & Fallback Framework for Casualties: Unknown

`cu-font-loader` 是一个面向 **Casualties: Unknown** 的独立 BepInEx 插件，提供自定义字体替换与 CJK 字形回退（glyph fallback）框架，用于改善游戏内文字的可读性与无障碍体验。

- **Custom font replacement**：从外部加载 `.ttf` / `.otf`，运行时替换 TextMeshPro 字体
- **CJK glyph fallback**：缺字回退到游戏原字体，图标与符号不变成方块
- **In-game settings panel**：在游戏「语言」设置页内切换开关、模式、字体与字重（CUCoreLib 为可选依赖）
- **Font weight selection**：同字族的 Thin / Light / Regular / Medium / Bold / Black 可直接切换
- **Mod UI compatibility**：保守的兼容策略，尽量不碰其他 Mod 的 UI、字体与材质
- **No game asset modification**：不修改游戏资源文件与 AssetBundle，卸载即恢复
- **Graceful degradation**：CUCoreLib 缺失时面板不注册，功能全部由配置文件驱动

> **v1.5.0** — 新增设置面板、字重选择与中英双语面板文案；重写 fallback：缺字只回退游戏原字体；字体按需加载，并按（文件, 采样字号）缓存图集。

## 来源与分支

| 项 | 值 |
|---|---|
| 上游项目 | [FenChen0211/cu-font-loader](https://github.com/FenChen0211/cu-font-loader) |
| 本仓库（fork） | [RedAsteroid/cu-font-loader](https://github.com/RedAsteroid/cu-font-loader) |
| 本分支 | `feat/v1.4.0-pre.1-compatibility`（基于上游同名分支） |
| 本分支版本 | v1.5.0 |
| 许可证 | MIT，上游版权归 FenChen0211；详见 [LICENSE](./LICENSE) |

本分支在上游同名分支的基础上继续开发，行为与上游 `main` 不完全一致。反馈请提交到 [Issues](https://github.com/RedAsteroid/cu-font-loader/issues)，附 BepInEx 日志更佳。

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
| 替换模式 | `FallbackOnly` / `Safe` / `Normal` / `Aggressive`，切换后立即重新应用 |
| 采样字号 | 图集采样尺寸（默认 36 px）；只重建当前生效字体，切回用过的档位直接复用 |
| 字体文件 | `fonts/` 内的字体；第一项「（未选择）」表示不替换 |
| 系统字体 | Windows 已安装字体；仅在「系统字体优先」开启时生效 |
| 系统字体优先 | 开启后系统字体覆盖 `fonts/` 中的字体文件 |
| 字重选择 | 当前生效字体的同族字重；无可用项时显示「（未选择）」 |

- cfg 是唯一的配置来源：面板改动立即写入 cfg，打开设置页时按 cfg 显示
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
- 选中字重等同于切换到对应文件，立即重新应用
- **可变字体**（单文件多字重）只支持默认字重：Unity 2022 无可变字体轴接口，面板会标注「（可变字体，仅默认字重）」

## 替换模式（ReplaceMode）

```ini
[General]
Enabled = true
ReplaceMode = Normal
ScanIntervalSeconds = 1
```

- `Enabled`：总开关，等价于面板「启用字体替换」
- `ScanIntervalSeconds`：扫描新文本的间隔（默认 1 秒，最低 0.25）

| 模式 | 行为 | 适合情况 |
|---|---|---|
| `FallbackOnly` | 只把外部字体加入 TMP fallback，不替换文本；切到此模式会撤销已有替换 | 最安全；只补中文缺字 |
| `Safe` | 只替换"普通"文本：跳过含 `<sprite>`、自定义材质、自定义 Shader 的文本 | 想替换字体但装了不少 Mod |
| `Normal` | 替换大多数文本，保留其他 Mod 的 UI 与材质（默认） | 推荐的日常模式 |
| `Aggressive` | 接近旧版 `Persistent`：每轮强制替换所有未排除文本，含自定义材质 | 明确需要强制全局替换 |

`Safe` / `Normal` 对每个文本和输入框只替换一次；`Aggressive` 持续对抗游戏对字体或材质的重新赋值。切换模式后立即重新应用，被新模式判定为跳过的文本会回退到游戏字体。

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
- 每个资产图集为 `AtlasSize²`（Alpha8，`4096² ≈ 16.7 MB`），中文多时还会追加图集页
- `AtlasSize` 建议 `4096`；显存紧张可降为 `2048`，代价是更容易回退到游戏原字体
- `SamplingPointSize` 是图集采样分辨率，**不改变界面文字的实际大小**；数值越大边缘越锐利，显存占用越高

字体选择键：

```ini
[General]
FontFile = HarmonyOS_Sans_SC_Light.ttf
SystemFont = HarmonyOS_Sans_SC_Bold
SystemFontPriority = false
```

- `FontFile`：`fonts/` 内的文件名（含扩展名，不区分大小写），系统字体优先关闭时生效；留空则不替换
- `SystemFont`：系统字体名（不含扩展名），仅在系统字体优先开启时生效
- 选中的文件**不存在**时自动清空并写日志；文件存在但**加载失败**时保留选择并重试

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

排除规则在**所有模式（含 Aggressive）**下生效，修改后立即生效，命中的文本回退到游戏字体。

### 已知限制

- 插件无法可靠识别"其他 Mod 创建的字体"——Mod 字体与游戏字体在运行时没有可靠区别，需要靠排除规则处理
- 含 `<sprite>` 标签的文本在 `Safe` 模式被跳过；`Normal` / `Aggressive` 会替换其字体（精灵图不受字体替换影响）
- 部分游戏 UI 使用非默认材质，`Normal` 会跳过它们，看起来像"没替换到"，其实是保护机制在工作，可用 CompatibilityLog 确认

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
- `Reason=` 为跳过原因：`Excluded`、`ExcludedMaterial`、`CustomMaterial`、`CustomShader`、`SpriteTag`、`MaterialMissing`、`FontMissing`
- 同一对象的同一决策只记录一次；整个会话最多 `MaxCompatibilityLogs` 条

`LogTextDetails` 是旧的诊断开关，会完整列出扫描到的 TMP 文本，仅在深度排查时使用。设置页字体统计只在 `LogTextDetails` 或 `CompatibilityLog` 开启时计算。

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

## 其它限制

- **可变字体**只渲染默认字重：Unity 2022.3 的 TextCore / TMP 无可变字体轴接口
- **字重识别基于文件名**：`msyhbd.ttf` 这类不含关键字的名字无法归入同族，只显示单一字重
- **格式支持**：仅支持 `.ttf` / `.otf` 格式字体
- **系统字体**只在「系统字体优先」开启时使用
- 外部字体**不含中文字形**时，中文由游戏原字体渲染
- 销毁缓存资产后切回该字号会重新解析文件（LRU 销毁机制）

## 编译

项目不内置游戏路径，编译时需指定游戏安装目录，解析顺序：

1. `-p:GameDir=...`
2. Steam 注册表 `HKCU\Software\Valve\Steam\SteamPath` + `\steamapps\common\Casualties Unknown`（仅完整框架 MSBuild 可用，`dotnet build` 跳过）
3. `cu-font-loader.csproj` 自定义路径

命令行传入 `GameDir`：

```powershell
dotnet build -p:GameDir="D:\path\to\Casualties Unknown"
```

也可用 Visual Studio 2022 打开 `cu-font-loader.sln` 生成（`net48`）。输出：

```text
bin/cu-font-loader.dll
```

`GameDir` 指向包含 `BepInEx/` 与 `CasualtiesUnknown_Data/` 的游戏根目录。`CUCoreLib-1.1/` 仅作 API 参考，已通过 `<Compile Remove>` 排除。

## 项目结构

```text
cu-font-loader/
  cu-font-loader.csproj   MSBuild 工程（net48，引用游戏程序集）
  cu-font-loader.sln      Visual Studio 解决方案
  FontLoaderPlugin.cs     插件主体：配置、扫描、替换、fallback、还原
  FontSettingsPanel.cs    设置面板：CUCoreLib 注册、钩子、双语文案
  CUCoreLib-1.1/          CUCoreLib 参考源码（不参与编译）
  LICENSE                 MIT 许可证
```

## 许可

MIT，详见 [LICENSE](./LICENSE)。

- 原始项目与上游版权：Copyright (c) 2026 FenChen0211（[FenChen0211/cu-font-loader](https://github.com/FenChen0211/cu-font-loader)）
- 本分支（fork）的修改与维护：[RedAsteroid/cu-font-loader](https://github.com/RedAsteroid/cu-font-loader)

保留原始版权与许可声明即可自由使用、修改与再分发。
