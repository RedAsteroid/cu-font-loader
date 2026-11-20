// cu-font-loader - External font loader for Casualties: Unknown
// Licensed under MIT License
// https://github.com/FenChen0211/cu-font-loader

using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TextCore;
using UnityEngine.TextCore.LowLevel;

namespace CuFontLoader
{
    [BepInPlugin("fenchen.cu-font-loader", "CU Font Loader", "1.6.1")]
    [BepInDependency("net.cucorelib", BepInDependency.DependencyFlags.SoftDependency)]
    public class FontLoaderPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private static readonly List<TMP_FontAsset> LoadedFonts = new List<TMP_FontAsset>();
        private static readonly List<Font> LoadedUnityFonts = new List<Font>();
        private static TMP_FontAsset PrimaryFontAsset;
        private static Font PrimaryUnityFont;
        private static readonly HashSet<TMP_Text> SeenTexts = new HashSet<TMP_Text>();
        private static readonly HashSet<UnityEngine.UI.Text> SeenLegacyTexts = new HashSet<UnityEngine.UI.Text>();
        private static readonly HashSet<TMP_InputField> SeenInputFields = new HashSet<TMP_InputField>();
        private static readonly List<string> CjkOverridePatterns = new List<string>();
        private static readonly List<string> ExcludedObjectPatterns = new List<string>();
        private static readonly List<string> ExcludedFontPatterns = new List<string>();
        private static readonly List<string> ExcludedMaterialPatterns = new List<string>();
        private static readonly Dictionary<UnityEngine.Object, string> DecisionLogCache = new Dictionary<UnityEngine.Object, string>();
        private static int decisionLogCount;
        private static bool decisionLogCapReached;
        private static int scanPassCount;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ReplaceAllText;
        internal static ConfigEntry<FontReplaceMode> ReplaceMode;
        internal static ConfigEntry<float> ScanIntervalSeconds;
        internal static ConfigEntry<string> PrimaryFont;
        internal static ConfigEntry<string> FontFile;
        internal static ConfigEntry<string> SystemFont;
        internal static ConfigEntry<bool> SystemFontPriority;
        internal static ConfigEntry<int> AtlasSize;
        internal static ConfigEntry<int> SamplingPointSize;
        internal static ConfigEntry<int> AtlasPadding;
        internal static ConfigEntry<bool> PreserveCustomMaterials;
        internal static ConfigEntry<string> ExcludedFontNames;
        internal static ConfigEntry<string> ExcludedMaterialNames;
        internal static ConfigEntry<string> ExcludedObjectNames;
        internal static ConfigEntry<bool> LogTextDetails;
        internal static ConfigEntry<int> MaxLoggedTexts;
        internal static ConfigEntry<bool> CompatibilityLog;
        internal static ConfigEntry<int> MaxCompatibilityLogs;
        internal static ConfigEntry<int> MaxCachedSamplingSizes;
        internal static ConfigEntry<int> MaxFontProfiles;
        internal static ConfigEntry<bool> PreserveNonCjk;
        internal static ConfigEntry<bool> CjkOverrideEnabled;
        internal static ConfigEntry<string> CjkOverrideFonts;
        internal static ConfigEntry<bool> AlignNativeMetrics;

        private static float s_nextScanTime;
        private static bool s_textRefreshPending;

        /// <summary>Monotonic counter used as the LRU stamp of a cached font asset.</summary>
        private static int s_fontUseCounter;

        /// <summary>Earliest time the primary font may be re-resolved after a failed attempt.</summary>
        private static float s_nextPrimaryResolveTime;

        /// <summary>Throttled reporting of scan-pass failures (see LogPassFailure).</summary>
        private static int s_passFailureCount;
        private static string s_passFailureSignature;
        private static float s_nextFailureLogTime;

        /// <summary>Diagnostics for texts restored without a usable snapshot (see LogAdoptedNativeFont).</summary>
        private static int s_adoptedNativeFonts;
        private static int s_unrestorableTexts;
        private static float s_nextUnrestorableLogTime;

        /// <summary>
        /// Set when PreserveNonCjk was switched on; the check in LogPreserveRenderState then runs on the
        /// first pass in a later frame. The layout of the switching frame is not rebuilt until its end,
        /// so a check in the same frame would only report the state from before the switch.
        /// </summary>
        private static bool s_preserveCheckPending;
        private static int s_preserveCheckFrame;

        // Time-sliced PreserveNonCjk scan. A full pass over 1000+ texts every second caused a
        // visible hitch; the same work is now spread across frames.
        private static TMP_Text[] s_preserveTexts;
        private static int s_preserveIndex;
        private static FontReplaceMode s_preserveMode;
        private static Dictionary<string, TMP_FontAsset> s_preserveRestoreCache;
        private static int s_preserveStillOurs;
        private static float s_preserveNextCycleTime;
        private static float s_nextLegacyPreserveScanTime;
        private const int PreserveBatchSize = 24;

        /// <summary>
        /// Whether the glyph guard was active during the previous pass. A false → true edge means the
        /// option was just switched on (or a mode change made it effective), which requires the glyph
        /// purge to look at every asset again.
        /// </summary>
        private static bool s_glyphGuardWasActive;

        /// <summary>Modes exposed in the in-game panel; legacy values are migrated on load.</summary>
        internal static readonly FontReplaceMode[] PanelModes =
        {
            FontReplaceMode.FallbackOnly,
            FontReplaceMode.Safe,
            FontReplaceMode.Normal,
            FontReplaceMode.ScaleAware,
            FontReplaceMode.Consistent,
            FontReplaceMode.Aggressive
        };

        private static readonly List<string> LoadedFontPaths = new List<string>();

        /// <summary>Sampling size each LoadedFonts entry was built at; parallel to LoadedFontPaths.</summary>
        private static readonly List<int> LoadedFontSizes = new List<int>();

        /// <summary>LRU stamp of each LoadedFonts entry; parallel to LoadedFontPaths.</summary>
        private static readonly List<int> LoadedFontStamps = new List<int>();
        private static readonly HashSet<TMP_FontAsset> KnownFonts = new HashSet<TMP_FontAsset>();

        /// <summary>
        /// Per-source metric variants of the primary font. A game font with scale 1.8 cannot be served
        /// by the primary asset's scale 1: TMP multiplies every glyph by fontSize / pointSize * scale,
        /// so the fallback would be visibly smaller. One profile per source font fixes that without
        /// touching the source text's own font.
        /// </summary>
        private sealed class FontProfileEntry
        {
            public TMP_FontAsset Source;
            public TMP_FontAsset Primary;
            public TMP_FontAsset Profile;
            public bool Strict;
            public FaceInfo BaseFaceInfo;
            public int SourcePointSize;
            public float SourceScale;
            public float SourceLineHeight;
            public float SourceCapLine;
            public float SourceAscentLine;
            public float SourceDescentLine;
            public int Stamp;
        }

        private static readonly List<FontProfileEntry> FontProfiles = new List<FontProfileEntry>();
        private static readonly HashSet<TMP_FontAsset> ProfileAssets = new HashSet<TMP_FontAsset>();
        private static readonly HashSet<TMP_FontAsset> ProfileLimitBlocked = new HashSet<TMP_FontAsset>();
        private static readonly HashSet<TMP_FontAsset> ProfileLimitLogged = new HashSet<TMP_FontAsset>();

        /// <summary>
        /// Sources whose profile could not be built. The failure is deterministic while the primary
        /// stays the same, so remembering it keeps every pass and every text from retrying the build.
        /// </summary>
        private static readonly HashSet<TMP_FontAsset> ProfileCreationFailed =
            new HashSet<TMP_FontAsset>();

        private static int s_profileUseCounter;

        /// <summary>
        /// One text that received the whitelist CJK takeover. Source/source material are what the game
        /// text had before the takeover; Profile/AppliedMaterial are what this plugin put there.
        /// </summary>
        private sealed class CjkOverrideState
        {
            public TMP_FontAsset Source;
            public Material SourceMaterial;
            public TMP_FontAsset Profile;
            public Material AppliedMaterial;
            public bool OwnsMaterial;
            public FontReplaceMode Mode;
        }

        private static readonly Dictionary<TMP_Text, CjkOverrideState> CjkOverrides =
            new Dictionary<TMP_Text, CjkOverrideState>();

        /// <summary>"Kind|name" keys already reported, so a repeated skip does not flood the log.</summary>
        private static readonly HashSet<string> CjkOverrideLogCache = new HashSet<string>();

        /// <summary>
        /// Every Unity Font this plugin created. Unlike LoadedUnityFonts it survives a cache trim, so a
        /// legacy label still carrying an older plugin font is recognised after a switch or rebuild;
        /// an entry is dropped only when its asset is destroyed.
        /// </summary>
        private static readonly HashSet<Font> KnownUnityFonts = new HashSet<Font>();

        /// <summary>Files that failed to load; retried only after a new selection or a rebuild.</summary>
        private static readonly HashSet<string> FailedFontPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<TMP_Text, OriginalTextState> OriginalTmpStates =
            new Dictionary<TMP_Text, OriginalTextState>();
        private static readonly Dictionary<UnityEngine.UI.Text, Font> OriginalLegacyFonts =
            new Dictionary<UnityEngine.UI.Text, Font>();
        private static readonly Dictionary<TMP_InputField, TMP_FontAsset> OriginalInputFonts =
            new Dictionary<TMP_InputField, TMP_FontAsset>();

        /// <summary>
        /// name → game font asset, kept across passes. Without it a snapshot whose asset is gone would
        /// trigger a full TMP asset scan on every pass; ResetSeenState clears it so the lookup is
        /// refreshed on scene changes and user actions.
        /// </summary>
        private static readonly Dictionary<string, TMP_FontAsset> OriginalAssetLookup =
            new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Last content scanned for "does this text contain CJK", keyed by the TMP text with a reference
        /// to the string that was scanned: an unchanged HUD costs one reference comparison per pass and
        /// a content change costs one rescan. Nothing is allocated on the hit path.
        /// </summary>
        private static readonly Dictionary<TMP_Text, CjkScanState> CjkScanCache =
            new Dictionary<TMP_Text, CjkScanState>();

        /// <summary>
        /// Fonts whose vertical metrics were matched to the game font. TMP takes each character's
        /// ascender/descender from that character's own font, so a mixed line would otherwise be
        /// stretched by the larger of the two metrics (see EnsureNativeMetrics).
        /// </summary>
        private static readonly HashSet<TMP_FontAsset> NativeMetricsAligned = new HashSet<TMP_FontAsset>();

        /// <summary>Original FaceInfo of every adjusted font, restored when the option goes off.</summary>
        private static readonly Dictionary<TMP_FontAsset, FaceInfo> OriginalFaceInfo =
            new Dictionary<TMP_FontAsset, FaceInfo>();

        /// <summary>Game font whose metrics are copied; resolved lazily and throttled on failure.</summary>
        private static TMP_FontAsset s_metricsReference;
        private static float s_nextMetricsReferenceResolve;

        /// <summary>
        /// Primary font the CJK glyphs currently on screen were resolved with. Changing it updates the
        /// fallback chain, but TMP keeps the old character references in textInfo until the text is
        /// regenerated, so every text that uses the plugin font must be marked dirty.
        /// </summary>
        private static TMP_FontAsset s_lastLayoutPrimary;

        /// <summary>
        /// In PreserveNonCjk mode the digits of a line come from that text's own font, so the only
        /// meaningful reference is the game font most of the CJK texts use. Counted during the pass
        /// (the texts are walked anyway) instead of scanning the scene again.
        /// </summary>
        private static readonly Dictionary<TMP_FontAsset, int> PreserveFontUsage =
            new Dictionary<TMP_FontAsset, int>();

        private static TMP_FontAsset s_preserveMetricsReference;

        /// <summary>
        /// Game fonts whose fallback chain this plugin extended with the selected font. PreserveNonCjk
        /// works by priority fallback: a text keeps its own font (so its digits, Latin and material stay
        /// untouched) and only the CJK glyphs that font cannot draw come from the plugin font.
        /// </summary>
        private static readonly HashSet<TMP_FontAsset> HookedFonts = new HashSet<TMP_FontAsset>();

        /// <summary>Hooked fonts that had no fallback table before, so Unhook can restore null.</summary>
        private static readonly HashSet<TMP_FontAsset> HookedFontsWithoutTable =
            new HashSet<TMP_FontAsset>();

        private static ConfigFile ConfigFile;

        /// <summary>
        /// Session-level "off" state. Mirrors General/Enabled, but the gate trusts it first: if the
        /// config write fails or the value is reverted, replacement must still stay suspended.
        /// </summary>
        private static bool replacementSuspended;

        private static Type s_settingsMenuType;
        private static bool s_settingsMenuTypeResolved;
        private static int s_lastMenuTexts = -1;
        private static int s_lastMenuOurs = -1;

        /// <summary>Folder scanned for external fonts; null when the plugin failed to initialize.</summary>
        internal static string FontsDir { get; private set; }

        private struct OriginalTextState
        {
            public TMP_FontAsset Font;
            public string FontName;
            public string FamilyName;
            public string StyleName;
            public int PointSize;
            public float Scale;
            public float CapLine;
            public string MaterialName;
            public Material Material;
        }

        private struct CjkScanState
        {
            public string Text;
            public bool HasCjk;
        }

        public enum FontReplaceMode
        {
            FallbackOnly,
            ReplaceOnce,
            Persistent,
            Safe,
            Normal,
            Aggressive,
            ScaleAware,
            Consistent
        }

        private struct TextDecision
        {
            public bool Replace;
            public string SkipReason;
        }

        private void Awake()
        {
            Log = Logger;
            ConfigFile = Config;

            Enabled = Config.Bind(
                "General",
                "Enabled",
                true,
                "字体替换总开关。关闭后不替换任何文本，并还原本插件修改过的字体、材质与 TMP fallback 条目；重新打开立即生效。\n" +
                "Master switch. When off, no text is replaced and the fonts, materials and TMP fallback " +
                "entries this plugin changed are reverted; turning it back on applies immediately."
            );

            ReplaceAllText = Config.Bind(
                "General",
                "ReplaceAllText",
                false,
                "已废弃。这是旧版本的开关，现在不再产生任何作用，保留只是为了不破坏旧配置文件。请使用 ReplaceMode。\n" +
                "Deprecated. This old switch has no effect now and is kept only so old config files keep " +
                "loading. Use ReplaceMode instead."
            );

            ReplaceMode = Config.Bind(
                "General",
                "ReplaceMode",
                FontReplaceMode.Normal,
                "FallbackOnly = 只把外部字体追加到 TMP 全局 fallback，不替换游戏字体（最安全）。 " +
                "Safe = 只替换普通文本，跳过明显使用 Sprite、特殊字体或自定义材质的文本。 " +
                "Normal = 替换大多数文本，同时保留其他 Mod 的自定义 UI 和材质。 " +
                "Aggressive = 接近旧版 Persistent 行为，反复强制替换所有文本。 " +
                "ScaleAware = 按原字体的 scale 和行高度量建立匹配的 profile 再替换，布局更接近原字体。 " +
                "Consistent = 与 Normal 相同，但已被替换的文本如果又被游戏改回原字体，会再次替换。 " +
                "开启 PreserveNonCjk 时 Safe / Normal / Aggressive / ScaleAware / Consistent 行为相同：默认都不改写文本字体和材质，只把插件字体挂成 CJK 字形 fallback，" +
                "TMP 排除规则也不参与；例外是 CjkOverrideEnabled 白名单特例，它会改白名单字体的 text.font。" +
                "它与 FallbackOnly 的区别是插件字体会被插到实际使用字体的链首并优先于其他 fallback。 " +
                "旧版 ReplaceOnce / Persistent 会在首次启动时自动迁移为 Normal / Aggressive。\n" +
                "FallbackOnly = append the external font to TMP's global fallback list only and change " +
                "nothing else (safest). Safe = replace ordinary text only, skipping texts that clearly use " +
                "sprites, special fonts or custom materials. Normal = replace most text while keeping other " +
                "mods' custom UI and materials. Aggressive = close to the old Persistent behaviour, forcing " +
                "every text repeatedly. ScaleAware = build a profile that matches the source font's scale " +
                "and line metrics before replacing. Consistent = like Normal, but re-applies to an already " +
                "replaced text when the game sets its original font back. With PreserveNonCjk on, Safe / " +
                "Normal / Aggressive / ScaleAware / Consistent behave the same: " +
                "by default no text font or material is changed, the plugin font is only hooked as a CJK " +
                "fallback and TMP exclusions do not apply. The CjkOverrideEnabled whitelist special case is " +
                "the exception: it rewrites the whitelisted source font's text.font. Unlike FallbackOnly the " +
                "plugin font is inserted at the front of the font a text actually uses and wins over other " +
                "fallbacks. Legacy ReplaceOnce / Persistent are migrated to Normal / Aggressive on first launch."
            );

            ScanIntervalSeconds = Config.Bind(
                "General",
                "ScanIntervalSeconds",
                1.0f,
                "替换模式下定期扫描已加载 TMP 文本的间隔秒数（新出现的文本通常由 OnEnable 钩子立即处理）。" +
                "不建议低于 0.25。开启 PreserveNonCjk 后不再按此间隔扫描：字体和文本变更由 TMP 钩子即时处理，" +
                "钩子不可用时才退回分帧增量扫描。\n" +
                "Seconds between periodic scans of loaded TMP texts in replacement modes; new texts are " +
                "usually handled immediately by the OnEnable hook. Values below 0.25 are not recommended. " +
                "With PreserveNonCjk on this interval is not used: font and text changes are handled by TMP " +
                "hooks, and a time-sliced scan takes over only when a hook cannot be installed."
            );

            PrimaryFont = Config.Bind(
                "General",
                "PrimaryFont",
                "",
                "已废弃。该键不再影响字体选择，保留只是为了不破坏旧配置文件。" +
                "字体选择请使用 FontFile（字体文件）或游戏内面板。\n" +
                "Deprecated. This key no longer affects font selection and is kept only so old config " +
                "files keep loading. Use FontFile or the in-game panel instead."
            );

            FontFile = Config.Bind(
                "General",
                "FontFile",
                "",
                "“字体文件”选中的本地字体（文件名，含扩展名，匹配 fonts 文件夹，不区分大小写）。" +
                "“系统字体优先”关闭时由它接管；关闭且留空时不替换任何文本（游戏原字体生效）。选中后立即应用。" +
                "文件不存在时会自动清空；文件存在但加载失败时保留选择，等下次选中或重建字体资产时重试。\n" +
                "Selected local font file (file name with extension, matched against the fonts folder, " +
                "case-insensitive). It is the source while System font priority is off; leaving it empty " +
                "with the priority off applies no font at all. A missing file is cleared automatically; a " +
                "file that exists but fails to load keeps the selection and is retried after the next " +
                "selection or font asset rebuild."
            );

            SystemFont = Config.Bind(
                "General",
                "SystemFont",
                "",
                "当前选择的系统字体（文件名，不含扩展名），由游戏内面板写入；留空表示未选择。" +
                "只在“系统字体优先”开启时作为字体来源，关闭时即使有值也不使用。\n" +
                "Currently selected system font (file name without extension), written by the in-game " +
                "panel; empty means none is selected. It is the source only while System font priority is " +
                "on; while the priority is off the value is ignored."
            );

            SystemFontPriority = Config.Bind(
                "General",
                "SystemFontPriority",
                false,
                "系统字体优先，决定字体来源。开启时以系统字体为来源，关闭时以字体文件为来源；" +
                "所选来源未选择时不替换任何文本，由游戏原字体渲染。\n" +
                "System font priority. Selects the font source: the system font when on, the font file " +
                "when off. With the selected source empty nothing is replaced and the game's own fonts " +
                "render the text."
            );

            AtlasSize = Config.Bind(
                "Font",
                "AtlasSize",
                4096,
                "外部 TMP 动态字体图集大小。CJK 字符较多，图集越大能容纳的字形越多；" +
                "字形加入失败时会继续沿 fallback 链寻找，可能出现缺字。\n" +
                "Atlas size of the external dynamic TMP font. CJK needs many glyphs, and a larger " +
                "atlas holds more of them; when a glyph cannot be added, TMP keeps walking the fallback " +
                "chain and characters may end up missing."
            );

            SamplingPointSize = Config.Bind(
                "Font",
                "SamplingPointSize",
                36,
                "创建 TMP 字体资产时使用的采样字号。数值越高边缘越锐利，图集占用越大；不改变界面文字的实际大小。\n" +
                "Sampling point size used when the TMP font asset is created. Higher values sharpen glyph " +
                "edges and use more atlas memory; the on-screen text size is unchanged."
            );

            AtlasPadding = Config.Bind(
                "Font",
                "AtlasPadding",
                5,
                "创建 TMP 字体资产时每个字形在图集中的留白，过小会影响描边与辉光效果。\n" +
                "Space kept around each glyph in the TMP atlas; too small a value can clip outline and " +
                "glow effects."
            );

            MaxCachedSamplingSizes = Config.Bind(
                "Font",
                "MaxCachedSamplingSizes",
                3,
                "每个字体文件最多缓存多少个采样字号（最近使用的优先保留）。0 = 不限制，等同于不释放旧图集。" +
                "超出时会在一次完整重新应用之后销毁最久未用的那套资产（含图集纹理与字体数据），" +
                "再次切回该字号会重新解析文件。\n" +
                "How many sampling sizes to cache per font file (most recently used are kept). 0 = " +
                "unlimited, i.e. old atlases are never released. When exceeded, the least recently used " +
                "asset (atlas textures and font data) is destroyed after a full re-apply; switching back " +
                "to that size parses the file again."
            );

            MaxFontProfiles = Config.Bind(
                "Font",
                "MaxFontProfiles",
                4,
                "每个源字体一份度量配置（profile），用于让 CJK fallback 的字形大小与源字体一致。" +
                "本项限制同时保留多少个 profile（最近使用的优先保留）；0 = 不限制。" +
                "每个 profile 使用 AtlasSize 指定的图集，4096² 时约 16.7 MB。达到上限且都被引用时，" +
                "新的源字体退回主字体，CJK 字形大小可能和源字体不一致。\n" +
                "One metric profile per source font, so CJK fallback glyphs match the source font's " +
                "scale and line metrics. This caps how many profiles stay alive (most recently used " +
                "win); 0 = unlimited. Each profile uses the AtlasSize atlas, about 16.7 MB at 4096². " +
                "When the cap is reached and every profile is still referenced, a new source font falls " +
                "back to the primary font and its CJK size may not match."
            );

            PreserveNonCjk = Config.Bind(
                "Font",
                "PreserveNonCjk",
                true,
                "推荐开启。保留游戏原生的数字、英文字母与符号。开启后文本继续使用游戏原字体，插件字体只作为该字体的首选 fallback，" +
                "用来补它画不出来的 CJK 字形（中文、日文假名、韩文谚文）；不含 CJK 字符的文本完全不参与。这是为中日韩用户保留部分游戏字体美术风格用的：" +
                "只需要英文、或希望所选字体作用于英文文本时请不要开启，否则英文字体不会生效（英文仍由游戏原字体渲染）。" +
                "韩文要求所选字体本身带谚文字形，字体没有的字会继续沿 fallback 链查找。" +
                "旧版 UI.Text 没有逐字 fallback，只有纯 CJK 标签会整段换字体。默认路径不改写 TMP 文本字体；" +
                "只有 CjkOverrideEnabled 白名单特例会改。开销低于普通替换；" +
                "已加载文本的补挂/恢复由 TMP 钩子即时处理，钩子不可用时才退回分帧扫描；开启时剔除插件字体里已有的非 CJK 字形（图集不变）。" +
                "默认开启；游戏内面板切换立即生效；直接编辑 cfg 需重启。\n" +
                "Recommended, on by default. Keep the game's native digits, Latin letters and symbols. Texts keep the game font and the " +
                "plugin font is added as that font's first fallback, only to supply the CJK glyphs it cannot " +
                "draw (Chinese, Japanese kana, Korean Hangul); texts without CJK are not touched. Intended for " +
                "CJK users who want to keep part of the game font's art style: English-only users, or anyone who " +
                "wants the selected font applied to English text, should leave this off, because English text " +
                "keeps the game font. Korean needs a selected font that actually contains Hangul; glyphs the " +
                "font lacks keep walking the fallback chain. Legacy " +
                "UI.Text has no per-glyph fallback, so only pure-CJK labels are switched wholesale. The " +
                "default path does not rewrite TMP fonts; only the CjkOverrideEnabled whitelist special " +
                "case does. It costs less than replacement: loaded texts are re-hooked and restored through " +
                "TMP hooks, with a time-sliced scan only when a hook cannot be installed; it also strips the " +
                "non-CJK glyphs already in the plugin font (the atlas is unchanged). Panel changes apply " +
                "immediately; editing the cfg directly needs a restart."
            );

            CjkOverrideEnabled = Config.Bind(
                "Font",
                "CjkOverrideEnabled",
                true,
                "中日韩字体替换白名单（仅 PreserveNonCjk 生效）。开启后，CjkOverrideFonts 白名单里的游戏字体" +
                "（默认 ScienceUnifont 和 ConsoleFont）即使自带 CJK 字形，也优先把文本字体换成一个 CJK-only 的 profile，" +
                "由插件字体绘制中日韩文字；数字、英文、符号仍由原字体绘制。默认开启。FallbackOnly 不生效；" +
                "关闭“仅替换中日韩字形”后本开关也不改变替换方式：替换模式下这些字体仍会被整体替换（数字、英文一起换），FallbackOnly 下不被替换；" +
                "Safe/Normal/Aggressive 都生效；Safe 仍跳过含 <sprite> 的文本，自定义材质按 PreserveCustomMaterials 克隆或跳过。" +
                "源字体和插件字体都缺的非 CJK 字符会显示缺字，" +
                "不会改用插件字体补。游戏内面板切换立即生效；直接编辑 cfg 需重启。\n" +
                "CJK font replacement whitelist (PreserveNonCjk only). When on, a game font listed in " +
                "CjkOverrideFonts (ScienceUnifont and ConsoleFont by default) has its text switched to a " +
                "CJK-only profile even if it contains CJK glyphs; the plugin font draws CJK text while " +
                "digits, Latin and symbols stay on the original font. On by default. FallbackOnly ignores it, " +
                "and with CJK-only mode off the switch no longer changes how text is replaced: replacement " +
                "modes replace these fonts like any other font, while FallbackOnly replaces nothing. " +
                "Safe/Normal/Aggressive all apply it. Safe still skips <sprite> text; custom materials are " +
                "cloned or skipped according to PreserveCustomMaterials. A non-CJK " +
                "character missing from both the source font and the plugin font stays missing rather than " +
                "being filled by the plugin. Panel changes apply immediately; editing the cfg directly needs " +
                "a restart."
            );

            CjkOverrideFonts = Config.Bind(
                "Font",
                "CjkOverrideFonts",
                "ScienceUnifont, ConsoleFont",
                "允许特殊接管的源字体名称，逗号分隔，不区分大小写，名称包含关键字即命中。" +
                "默认 ScienceUnifont 和 ConsoleFont：前者用于结局相关界面，后者是控制台字体。" +
                "只在 CjkOverrideEnabled=true 且 PreserveNonCjk=true 时生效；留空表示没有白名单。\n" +
                "Source font asset names allowed to use the special CJK takeover, comma-separated, " +
                "case-insensitive, substring match. Defaults to ScienceUnifont (the ending-related screens) " +
                "and ConsoleFont (the console). Used only when CjkOverrideEnabled=true and " +
                "PreserveNonCjk=true; empty means no whitelist."
            );

            AlignNativeMetrics = Config.Bind(
                "Font",
                "AlignNativeMetrics",
                true,
                "行距对齐（仅 PreserveNonCjk 模式）。开启时把插件字体的垂直度量（行高、ascent/descent、基线等）按比例对齐到参考字体" +
                "（当前界面里 CJK 文本最常用的那款游戏字体，也就是实际绘制这些文本数字/英文的字体），避免混排行距被撑高、基线错位。" +
                "只调整插件字体自身的 FaceInfo，不改任何文本的字体或材质；关闭后使用插件字体自身度量。游戏内面板切换立即生效；直接编辑 cfg 需重启。\n" +
                "Line spacing alignment (PreserveNonCjk mode only). When on, the plugin font's vertical " +
                "metrics (line height, ascent/descent, baseline, etc.) are aligned proportionally to a " +
                "reference font: the game font that the CJK text on screen uses most, i.e. the font " +
                "that actually draws those digits and Latin letters, so mixed lines are not stretched and " +
                "baselines do not shift. Only the plugin font's own FaceInfo is changed, never any text's " +
                "font or material; when off the plugin font keeps its own metrics. Panel changes apply " +
                "immediately; editing the cfg directly needs a restart."
            );

            PreserveCustomMaterials = Config.Bind(
                "Compatibility",
                "PreserveCustomMaterials",
                true,
                "保护其他 Mod 的自定义材质。开启时，Safe / Normal 模式会整体跳过使用非默认材质（描边、辉光、自定义 Shader 等）的文本。" +
                "Aggressive 模式始终恢复旧版强制行为；关闭后，Safe / Normal 也会替换这些文本的字体和材质。" +
                "PreserveNonCjk 的普通 fallback 不改写字体和材质，本项不参与；只有 CjkOverrideEnabled 的" +
                "白名单特例会尝试克隆自定义材质，克隆失败时跳过该文本。仅 cfg 可改，改后需重启。\n" +
                "Protect other mods' custom materials. When on, Safe / Normal skip texts whose material is " +
                "not the current font's default (outline, glow, custom shaders, etc.) entirely. Aggressive " +
                "always keeps the old forced behaviour; when off, Safe / Normal also replace those texts' " +
                "fonts and materials. Ordinary PreserveNonCjk fallback never changes a text's font or " +
                "material, so this is not used there; the CjkOverrideEnabled whitelist special case tries " +
                "to clone a custom material and skips the text when cloning fails. Cfg only; a change needs a restart."
            );

            ExcludedFontNames = Config.Bind(
                "Compatibility",
                "ExcludedFontNames",
                "",
                "替换模式（PreserveNonCjk 关闭）下，不替换使用这些字体（FontAsset 名称）的文本。逗号分隔，不区分大小写，名称包含关键字即命中。" +
                "PreserveNonCjk 模式下普通 TMP 文本不改写字体，排除项不参与，仍可能由插件字体补 CJK 字形。修改后下一次扫描起生效；直接编辑 cfg 需重启。\n" +
                "In replacement modes (PreserveNonCjk off), texts using these font assets (FontAsset names) " +
                "are not replaced. Comma-separated, case-insensitive, substring match. PreserveNonCjk does " +
                "not rewrite fonts, so exclusions do not apply to ordinary TMP texts, which may still get CJK " +
                "glyphs from the plugin font. Changes apply from the next scan; editing the cfg directly needs a restart."
            );

            ExcludedMaterialNames = Config.Bind(
                "Compatibility",
                "ExcludedMaterialNames",
                "",
                "替换模式（PreserveNonCjk 关闭）下，不替换使用这些材质（材质名称）的文本。逗号分隔，不区分大小写，名称包含关键字即命中。" +
                "PreserveNonCjk 模式下普通 TMP 文本保留原有材质，排除项不参与，仍可能由插件字体补 CJK 字形。修改后下一次扫描起生效；直接编辑 cfg 需重启。\n" +
                "In replacement modes (PreserveNonCjk off), texts using these materials (material names) are " +
                "not replaced. Comma-separated, case-insensitive, substring match. PreserveNonCjk keeps each " +
                "text's own material, so exclusions do not apply to ordinary TMP texts, which may still get CJK " +
                "glyphs from the plugin font. Changes apply from the next scan; editing the cfg directly needs a restart."
            );

            ExcludedObjectNames = Config.Bind(
                "Compatibility",
                "ExcludedObjectNames",
                "",
                "替换模式（PreserveNonCjk 关闭）下，不替换这些 GameObject 名称的 TMP / 旧版文本。逗号分隔，不区分大小写，名称包含关键字即命中。" +
                "PreserveNonCjk 模式下普通 TMP 文本不做逐文本替换，排除项不参与。修改后下一次扫描起生效；直接编辑 cfg 需重启。\n" +
                "In replacement modes (PreserveNonCjk off), TMP and legacy texts on GameObjects whose names " +
                "match are not replaced. Comma-separated, case-insensitive, substring match. PreserveNonCjk " +
                "does not replace texts one by one, so exclusions do not apply to ordinary TMP texts. Changes " +
                "apply from the next scan; editing the cfg directly needs a restart."
            );

            ExcludedFontNames.SettingChanged += OnExclusionSettingChanged;
            ExcludedMaterialNames.SettingChanged += OnExclusionSettingChanged;
            ExcludedObjectNames.SettingChanged += OnExclusionSettingChanged;
            CjkOverrideFonts.SettingChanged += OnExclusionSettingChanged;
            RefreshExclusionCache();

            LogTextDetails = Config.Bind(
                "Debug",
                "LogTextDetails",
                false,
                "调试用。开启后输出 TMP 文本对象名称、激活状态、字体、材质、文本片段，以及 glyphs=…" +
                "（可见字符中插件字体/原生字体各绘制多少、被插件字体接管的非 CJK 字符样本、实际绘制非 CJK 字符的字体名）；" +
                "同时启用 [Native] 详细日志（fallback 挂载、字形接管，以及插件字体把 CJK 字符映射成空字形的情况）。\n" +
                "Debug. When on, logs each TMP text's object name, active state, font, material and text " +
                "sample, plus glyphs=… (how many visible characters each font draws, samples of non-CJK " +
                "characters taken over by the plugin font, and the font that actually draws non-CJK " +
                "characters). Also enables [Native] detailed logs (fallback hooking, glyph takeover, and " +
                "CJK characters the plugin font maps to an empty glyph)."
            );

            MaxLoggedTexts = Config.Bind(
                "Debug",
                "MaxLoggedTexts",
                80,
                "LogTextDetails 开启时，每次字体扫描最多输出多少个 TMP 文本对象。\n" +
                "With LogTextDetails on, how many TMP text objects to log per font scan."
            );

            CompatibilityLog = Config.Bind(
                "Debug",
                "CompatibilityLog",
                false,
                "兼容性调试日志。开启后，文本被替换或跳过时会输出一行决策记录（场景、对象名、字体、材质、原因）。" +
                "同一对象的同一决策只记录一次，不会持续刷屏；PreserveNonCjk 模式下没有 TMP 逐文本替换决策，" +
                "但仍会启用 [Native] 详细日志（字形接管、fallback 挂载）。排查其他 Mod UI 被误伤时开启。\n" +
                "Compatibility debug log. When on, every replaced or skipped text produces one decision line " +
                "(scene, object name, font, material, reason); the same decision for the same object is " +
                "logged once. PreserveNonCjk makes no per-text replacement decisions but still enables " +
                "[Native] detailed logs (glyph takeover, fallback hooking). Use it to diagnose other mods' " +
                "UI being affected."
            );

            MaxCompatibilityLogs = Config.Bind(
                "Debug",
                "MaxCompatibilityLogs",
                200,
                "CompatibilityLog 开启时，本次游戏会话最多输出多少条决策记录，防止日志过大。\n" +
                "With CompatibilityLog on, the maximum number of decision lines per game session, to keep " +
                "the log small."
            );

            MigrateLegacyReplaceMode();
            replacementSuspended = !Enabled.Value;

            // The glyph guard keeps non-CJK characters out of the plugin font. It is an optimisation for
            // PreserveNonCjk, not a requirement: if the TMP entry point cannot be patched, fallback
            // priority still keeps digits and Latin on the game font.
            NativeGlyphGuard.Install();
            TextPipelineHooks.Install();
            if (PreserveNonCjk.Value)
            {
                Log.LogInfo("PreserveNonCjk: text fonts are kept; the plugin font is added as their first " +
                    "CJK fallback (glyph guard=" + (NativeGlyphGuard.Installed ? "on" : "off") +
                    ", new text discovery=" + (TextPipelineHooks.DiscoveryInstalled ? "on" : "off") + ").");
            }
            if (!string.IsNullOrEmpty(PrimaryFont.Value))
            {
                Log.LogInfo("PrimaryFont is deprecated and ignored ('" + PrimaryFont.Value +
                    "'); font selection now uses FontFile.");
            }

            FontsDir = Path.Combine(Paths.PluginPath, "cu-font-loader", "fonts");
            if (!Directory.Exists(FontsDir))
            {
                try
                {
                    Directory.CreateDirectory(FontsDir);
                    Log.LogInfo("fonts/ dir missing, created: " + FontsDir);
                }
                catch (Exception ex)
                {
                    // LogError reaches both LogOutput.log and the BepInEx console window.
                    Log.LogError("无法创建 fonts 目录 / Could not create the fonts folder: " + FontsDir +
                        " (" + ex.Message + "). Create it manually and put .ttf/.otf files inside; " +
                        "until then the plugin stays idle.");
                }
            }

            // Only the file names are listed here: no font is read until it is actually needed. The
            // selection below pulls the font in effect, everything else loads on first use.
            List<string> fontPaths = ScanFontFiles(FontsDir);
            if (fontPaths.Count == 0)
            {
                Log.LogWarning(
                    "No .ttf/.otf fonts found in " + FontsDir +
                    " - the plugin stays idle until a font is added there or a system font is selected.");
            }
            else
            {
                Log.LogInfo(fontPaths.Count + " font file(s) found in " + FontsDir +
                    "; files load on demand when a font is selected (nothing is preloaded).");
            }

            ResolvePrimaryFont();
            if (PrimaryFontAsset != null)
            {
                Log.LogInfo("Font in effect: " + PrimaryFontAsset.name + " (" + LoadedFonts.Count +
                    " asset(s) loaded so far; the rest load when selected).");
            }
            else if (IsReplacementEnabled())
            {
                string key = SystemFontPriority.Value ? "SystemFont" : "FontFile";
                string selection = SystemFontPriority.Value ? SystemFont.Value : FontFile.Value;
                if (string.IsNullOrEmpty(selection))
                {
                    Log.LogInfo(
                        "No font selected (General/" + key + " is empty); nothing is replaced until a font " +
                        "is chosen in the settings panel.");
                }
                else
                {
                    Log.LogInfo(
                        "Selected font '" + selection + "' (General/" + key + ") could not be loaded; " +
                        "nothing is replaced until it loads. The selection is kept, see the warning above.");
                }
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            if (!IsReplacementEnabled())
            {
                Log.LogInfo("Font replacement disabled in cfg (General/Enabled = false).");
            }

            ApplyToLoadedText(true);
            ApplyToLoadedLegacyText(true);
            FontSettingsPanel.Initialize();
        }

        private void Update()
        {
            FontSettingsPanel.Tick();

            if (s_textRefreshPending)
            {
                // Deferred re-apply requested from a settings-row callback, see RequestTextRefresh.
                try
                {
                    RefreshAllTexts();
                }
                catch (Exception ex)
                {
                    LogPassFailure("text refresh", ex);
                }
            }

            // The primary font is resolved here rather than gating on LoadedFonts.Count: with fonts loaded
            // on demand, a selection whose file was locked during startup still gets retried (the lookups
            // are throttled in GetPrimaryFontAsset), and an empty selection costs one null check.
            if (!IsReplacementEnabled() || GetPrimaryFontAsset() == null ||
                GetEffectiveMode() == FontReplaceMode.FallbackOnly)
            {
                // The enable hook keeps queueing texts while nothing can consume them (plugin off, no
                // font selected, FallbackOnly). Clearing here keeps the queue from growing all session.
                TextPipelineHooks.ClearQueue();
                return;
            }

            // Newly enabled texts are applied on the frame after they appear; the scheduled scan
            // below is too slow for that.
            TextPipelineHooks.Flush();

            if (IsPreserveNonCjkEnabled)
            {
                // Normal path: OnEnable, the TMP_Text.font setter and the TMP_Text.text setter
                // cover every event PreserveNonCjk needs. The incremental scan is only a fallback
                // for a TMP build where one of those hooks could not be installed.
                bool hooksComplete = TextPipelineHooks.DiscoveryInstalled &&
                    TextPipelineHooks.FontChangeInstalled &&
                    (!IsCjkOverrideActive || TextPipelineHooks.TextChangeInstalled);

                if (!hooksComplete)
                {
                    PreserveIncrementalTick();
                    return;
                }

                // Legacy UI.Text has no OnEnable hook in this plugin and only a few dozen labels
                // exist in practice; a low-frequency scan of that small set is cheap and keeps
                // pure-CJK legacy labels correct. TMP (1000+ texts) is no longer scanned.
                if (Time.unscaledTime >= s_nextLegacyPreserveScanTime)
                {
                    s_nextLegacyPreserveScanTime = Time.unscaledTime + 5f;
                    ApplyToLoadedLegacyText(false);
                }
                return;
            }

            s_preserveTexts = null;
            s_preserveIndex = 0;
            s_preserveRestoreCache = null;
            s_preserveNextCycleTime = 0f;
            s_nextLegacyPreserveScanTime = 0f;

            float interval = Mathf.Max(0.25f, ScanIntervalSeconds.Value);
            if (Time.unscaledTime >= s_nextScanTime)
            {
                s_nextScanTime = Time.unscaledTime + interval;
                ApplyToLoadedText(false);
                ApplyToLoadedLegacyText(false);
            }
        }

        /// <summary>
        /// Queues a full re-apply for the next frame. Panel callbacks use this instead of calling
        /// RefreshAllTexts directly: re-fonting every text while a settings row is still executing
        /// rebuilds that row's own labels (the "panel text vanished" symptom). An explicit
        /// RefreshAllTexts call consumes the request.
        /// </summary>
        internal static void RequestTextRefresh()
        {
            s_textRefreshPending = true;
        }

        /// <summary>
        /// Master switch: when off, no text is touched at all. The panel sets a session flag in
        /// addition to the cfg entry, so the gate closes even if the config write does not stick.
        /// </summary>
        internal static bool IsReplacementEnabled()
        {
            if (replacementSuspended)
            {
                return false;
            }

            return Enabled == null || Enabled.Value;
        }

        /// <summary>
        /// True when non-CJK characters must stay on the game's font. Inert while replacement is off
        /// and in FallbackOnly mode: nothing carries our font there, so there is nothing to narrow.
        /// </summary>
        internal static bool IsPreserveNonCjkEnabled
        {
            get
            {
                if (PreserveNonCjk == null || !PreserveNonCjk.Value)
                {
                    return false;
                }

                if (replacementSuspended || (Enabled != null && !Enabled.Value) || ReplaceMode == null)
                {
                    return false;
                }

                return GetEffectiveMode() != FontReplaceMode.FallbackOnly;
            }
        }

        /// <summary>
        /// True when the whitelist special case may rewrite a text's font. FallbackOnly is excluded by
        /// IsPreserveNonCjkEnabled; CjkOverrideEnabled is the independent opt-in.
        /// </summary>
        internal static bool IsCjkOverrideActive
        {
            get
            {
                return IsPreserveNonCjkEnabled &&
                       CjkOverrideEnabled != null && CjkOverrideEnabled.Value;
            }
        }

        /// <summary>Cheap check for the per-text text-setter hook: only whitelisted fonts and
        /// texts already carrying a takeover need the special-case pass.</summary>
        internal static bool MayNeedCjkOverride(TMP_Text text)
        {
            if (text == null)
            {
                return false;
            }

            if (CjkOverrides.ContainsKey(text))
            {
                return true;
            }

            return text.font != null && IsCjkOverrideFont(text.font);
        }

        /// <summary>
        /// True when a newly enabled text can be re-fonted immediately in the OnEnable postfix.
        /// Safe mode and the CJK-only mode inspect the text's content, so they still go through the
        /// next-frame queue; the replacement modes only need the font and material state.
        /// </summary>
        internal static bool CanApplyOnTextEnabled
        {
            get
            {
                if (ReplaceMode == null)
                {
                    return false;
                }

                if (IsPreserveNonCjkEnabled)
                {
                    // Fallback hooking does not need the text's content, and the whitelist special
                    // case can be applied to an empty label before it is filled. Applying on
                    // OnEnable removes the need for a periodic full scan.
                    return true;
                }

                FontReplaceMode mode = GetEffectiveMode();
                return mode != FontReplaceMode.FallbackOnly &&
                    mode != FontReplaceMode.Safe;
            }
        }

        private static FontReplaceMode GetEffectiveMode()
        {
            // Text hooks can fire before Awake bound the config; treat that as the default mode.
            FontReplaceMode configured = ReplaceMode != null ? ReplaceMode.Value : FontReplaceMode.Normal;
            switch (configured)
            {
                case FontReplaceMode.ReplaceOnce:
                    return FontReplaceMode.Normal;
                case FontReplaceMode.Persistent:
                    return FontReplaceMode.Aggressive;
                default:
                    return configured;
            }
        }

        private void MigrateLegacyReplaceMode()
        {
            switch (ReplaceMode.Value)
            {
                case FontReplaceMode.ReplaceOnce:
                    Log.LogInfo("Migrating legacy ReplaceMode: ReplaceOnce -> Normal");
                    ReplaceMode.Value = FontReplaceMode.Normal;
                    Config.Save();
                    break;
                case FontReplaceMode.Persistent:
                    Log.LogInfo("Migrating legacy ReplaceMode: Persistent -> Aggressive");
                    ReplaceMode.Value = FontReplaceMode.Aggressive;
                    Config.Save();
                    break;
            }
        }

        private static TextDecision EvaluateTMPText(TMP_Text text)
        {
            TextDecision decision = default(TextDecision);
            decision.Replace = true;
            FontReplaceMode mode = GetEffectiveMode();

            TMP_FontAsset font = text.font;
            if (font == null)
            {
                decision.Replace = false;
                decision.SkipReason = "FontMissing";
                return decision;
            }

            TMP_FontAsset primary = GetPrimaryFontAsset();

            if (ExcludedObjectPatterns.Count > 0 &&
                MatchesExclusion(text.gameObject.name, ExcludedObjectPatterns))
            {
                decision.Replace = false;
                decision.SkipReason = "Excluded";
                return decision;
            }

            if (ExcludedFontPatterns.Count > 0 &&
                MatchesExclusion(font.name, ExcludedFontPatterns))
            {
                decision.Replace = false;
                decision.SkipReason = "Excluded";
                return decision;
            }

            Material material = text.fontSharedMaterial;
            if (material == null)
            {
                if (mode != FontReplaceMode.Aggressive)
                {
                    decision.Replace = false;
                    decision.SkipReason = "MaterialMissing";
                    return decision;
                }
            }

            // A text already carrying one of our fonts is ours to re-font: the custom material and
            // shader guards exist to protect other mods' fonts, not to pin a stale primary font.
            bool fontIsOurs = IsKnownFont(font);

            if (material != null && ExcludedMaterialPatterns.Count > 0 &&
                MatchesExclusion(material.name, ExcludedMaterialPatterns))
            {
                decision.Replace = false;
                decision.SkipReason = "ExcludedMaterial";
                return decision;
            }

            if (material != null &&
                !fontIsOurs &&
                mode != FontReplaceMode.Aggressive &&
                !IsTmpShader(material.shader) &&
                PreserveCustomMaterials.Value)
            {
                decision.Replace = false;
                decision.SkipReason = "CustomShader";
                return decision;
            }

            if (material != null &&
                !fontIsOurs &&
                material != font.material &&
                PreserveCustomMaterials.Value &&
                mode != FontReplaceMode.Aggressive)
            {
                decision.Replace = false;
                decision.SkipReason = "CustomMaterial";
                return decision;
            }

            if (mode == FontReplaceMode.Safe)
            {
                string content = text.text;
                if (content != null && content.IndexOf("<sprite", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    decision.Replace = false;
                    decision.SkipReason = "SpriteTag";
                    return decision;
                }
            }

            if (primary != null && font == primary)
            {
                decision.Replace =
                    mode == FontReplaceMode.Aggressive && material != primary.material;
            }

            return decision;
        }

        /// <summary>
        /// Consistent mode retries only a text that was already replaced and whose font has been
        /// put back to the original font by the game or another mod. A different custom font is
        /// left alone.
        /// </summary>
        internal static bool IsConsistentMode
        {
            get { return GetEffectiveMode() == FontReplaceMode.Consistent; }
        }

        internal static bool NeedsConsistentReplacement(TMP_Text text)
        {
            if (text == null || !SeenTexts.Contains(text) ||
                text.font == null || IsKnownFont(text.font))
            {
                return false;
            }

            OriginalTextState state;
            if (!OriginalTmpStates.TryGetValue(text, out state))
            {
                return false;
            }

            if (state.Font != null && text.font == state.Font)
            {
                return true;
            }

            string currentName = text.font.name;
            if (string.IsNullOrEmpty(currentName) || string.IsNullOrEmpty(state.FontName))
            {
                return false;
            }

            if (string.Equals(currentName, state.FontName, StringComparison.Ordinal))
            {
                return true;
            }

            string currentFamily = FamilyNameOf(currentName);
            string originalFamily = !string.IsNullOrEmpty(state.FamilyName)
                ? state.FamilyName
                : FamilyNameOf(state.FontName);
            return currentFamily.Length > 0 &&
                string.Equals(currentFamily, originalFamily, StringComparison.OrdinalIgnoreCase);
        }

        private static OriginalTextState CaptureTextState(TMP_FontAsset font, Material material)
        {
            OriginalTextState state = default(OriginalTextState);
            if (font == null)
            {
                return state;
            }

            state.Font = font;
            state.FontName = font.name;
            state.FamilyName = FamilyNameOf(font.name);
            state.StyleName = font.name;
            state.PointSize = font.faceInfo.pointSize;
            state.Scale = font.faceInfo.scale;
            state.CapLine = font.faceInfo.capLine;
            state.MaterialName = material != null ? material.name : null;
            state.Material = material;
            return state;
        }

        private static string FamilyNameOf(string fontName)
        {
            if (string.IsNullOrEmpty(fontName))
            {
                return string.Empty;
            }

            string name = fontName;
            string[] suffixes =
            {
                " SDF - Fallback", " SDF", " ExtraBold", " UltraBold", " SemiBold", " DemiBold",
                " ExtraLight", " UltraLight", " Regular", " Normal", " Medium", " Light", " Thin",
                " Black", " Heavy", " Bold Italic", " Bold", " Italic", " Book"
            };
            for (int i = 0; i < suffixes.Length; i++)
            {
                if (name.EndsWith(suffixes[i], StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - suffixes[i].Length).Trim();
                    break;
                }
            }

            return name;
        }

        private static bool IsTmpShader(Shader shader)
        {
            if (shader == null)
            {
                return false;
            }

            string shaderName = shader.name;
            return shaderName != null &&
                (shaderName.StartsWith("TextMeshPro/", StringComparison.OrdinalIgnoreCase) ||
                 shaderName.StartsWith("Text Mesh Pro/", StringComparison.OrdinalIgnoreCase));
        }

        private static void OnExclusionSettingChanged(object sender, EventArgs e)
        {
            RefreshExclusionCache();
        }

        private static void RefreshExclusionCache()
        {
            ParseExclusions(ExcludedObjectNames.Value, ExcludedObjectPatterns);
            ParseExclusions(ExcludedFontNames.Value, ExcludedFontPatterns);
            ParseExclusions(ExcludedMaterialNames.Value, ExcludedMaterialPatterns);
            if (CjkOverrideFonts != null)
            {
                ParseExclusions(CjkOverrideFonts.Value, CjkOverridePatterns);
            }
        }

        private static void ParseExclusions(string raw, List<string> target)
        {
            target.Clear();
            if (string.IsNullOrEmpty(raw))
            {
                return;
            }

            string[] parts = raw.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.Length > 0)
                {
                    target.Add(part);
                }
            }
        }

        private static bool MatchesExclusion(string value, List<string> patterns)
        {
            if (value == null || patterns.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < patterns.Count; i++)
            {
                if (value.IndexOf(patterns[i], StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when a TMP text contains at least one CJK character. The scan is cached against the
        /// exact string instance, which is what keeps a pass cheap on texts that never change.
        /// </summary>
        private static bool TextHasCjk(TMP_Text text)
        {
            string content = text.text;

            CjkScanState cached;
            if (CjkScanCache.TryGetValue(text, out cached) && ReferenceEquals(cached.Text, content))
            {
                return cached.HasCjk;
            }

            cached.Text = content;
            cached.HasCjk = CjkText.ContainsCjk(content);
            CjkScanCache[text] = cached;
            return cached.HasCjk;
        }

        /// <summary>Re-entrance guard: a single-text pass may assign a font and re-trigger the hook.</summary>
        private static readonly HashSet<TMP_Text> ApplyingSingleText = new HashSet<TMP_Text>();

        /// <summary>
        /// Applies the usual replacement decision to a single text, mirroring what the scan pass does
        /// for it. Used by the enable-event queue and the font hook; only the text is touched, while
        /// counters, statistics and the fallback chain stay with the pass.
        /// </summary>
        internal static bool ApplySingleText(TMP_Text text)
        {
            if (text == null || !ApplyingSingleText.Add(text))
            {
                return false;
            }

            try
            {
                return ApplySingleTextCore(text);
            }
            finally
            {
                ApplyingSingleText.Remove(text);
            }
        }

        private static bool ApplySingleTextCore(TMP_Text text)
        {
            if (text == null || !IsLiveSceneObject(text))
            {
                return false;
            }

            if (IsPreserveNonCjkEnabled)
            {
                if (text.font == null)
                {
                    TMP_FontAsset repaired = ResolveFallbackNativeFont();
                    if (repaired != null)
                    {
                        text.font = repaired;
                        text.SetAllDirty();
                    }
                }

                // Whitelisted source fonts get a CJK-only profile as the main font. Everything
                // else keeps the original font and only gets a fallback.
                if (TryApplyCjkOverride(text, GetEffectiveMode()))
                {
                    return true;
                }

                // Priority fallback mode: nothing to replace, only the text's font may need the plugin
                // font as its CJK fallback. A text still carrying our font (leftover from another mode
                // or a prefab clone) is put back on a game font first.
                if (IsKnownFont(text.font))
                {
                    RestoreTextFontIfOurs(text, null);
                }

                if (!IsKnownFont(text.font))
                {
                    // Hook the font even when the label is still empty at OnEnable: the content
                    // often arrives a few frames later. The global strict CJK guard keeps this
                    // harmless for labels that never contain CJK.
                    HookFontFallback(text.font);
                }

                return false;
            }

            FontReplaceMode singleMode = GetEffectiveMode();
            if (SeenTexts.Contains(text) &&
                singleMode != FontReplaceMode.Aggressive &&
                !(singleMode == FontReplaceMode.Consistent && NeedsConsistentReplacement(text)))
            {
                return false;
            }

            TextDecision decision = EvaluateTMPText(text);
            if (!decision.Replace)
            {
                if (decision.SkipReason != null)
                {
                    LogSkipped(text, decision.SkipReason);
                    RestoreTextFontIfOurs(text, null);
                }

                return false;
            }

            TMP_FontAsset previousFont = text.font;
            Material previousMaterial = text.fontSharedMaterial;
            if (!ReplaceTextFont(text))
            {
                return false;
            }

            SeenTexts.Add(text);
            if (previousFont == text.font && previousMaterial != text.fontSharedMaterial)
            {
                LogMaterialFixed(text, previousMaterial);
            }
            else
            {
                LogReplaced(text, previousFont);
            }

            return true;
        }

        private static bool TryBeginDecisionLog(UnityEngine.Object key, string signature)
        {
            if (!CompatibilityLog.Value || decisionLogCapReached)
            {
                return false;
            }

            string existing;
            if (DecisionLogCache.TryGetValue(key, out existing) && existing == signature)
            {
                return false;
            }

            int cap = Mathf.Clamp(MaxCompatibilityLogs.Value, 1, 100000);
            if (decisionLogCount >= cap)
            {
                decisionLogCapReached = true;
                Log.LogInfo("[Compat] decision log cap reached (" + cap + "), further records are suppressed");
                return false;
            }

            DecisionLogCache[key] = signature;
            decisionLogCount++;
            return true;
        }

        private static void LogReplaced(TMP_Text text, TMP_FontAsset previousFont)
        {
            if (!CompatibilityLog.Value || decisionLogCapReached)
            {
                return;
            }

            string from = previousFont != null ? previousFont.name : "<null>";
            if (!TryBeginDecisionLog(text, "R|" + from))
            {
                return;
            }

            string materialName = text.fontSharedMaterial != null ? text.fontSharedMaterial.name : "<null>";
            Log.LogInfo(
                "[Compat] Scene=" + SceneManager.GetActiveScene().name +
                " Object=" + text.gameObject.name +
                " Font=" + text.font.name +
                " Material=" + materialName +
                " Action=Replaced" +
                " From=" + from);
        }

        private static void LogMaterialFixed(TMP_Text text, Material previousMaterial)
        {
            if (!CompatibilityLog.Value || decisionLogCapReached)
            {
                return;
            }

            string from = previousMaterial != null ? previousMaterial.name : "<null>";
            if (!TryBeginDecisionLog(text, "M|" + from))
            {
                return;
            }

            string fontName = text.font != null ? text.font.name : "<null>";
            string materialName = text.fontSharedMaterial != null ? text.fontSharedMaterial.name : "<null>";
            Log.LogInfo(
                "[Compat] Scene=" + SceneManager.GetActiveScene().name +
                " Object=" + text.gameObject.name +
                " Font=" + fontName +
                " Material=" + materialName +
                " Action=MaterialFixed" +
                " From=" + from);
        }

        private static void LogSkipped(TMP_Text text, string reason)
        {
            if (!CompatibilityLog.Value || decisionLogCapReached)
            {
                return;
            }

            string fontName = text.font != null ? text.font.name : "<null>";
            if (!TryBeginDecisionLog(text, "S|" + reason + "|" + fontName))
            {
                return;
            }

            string materialName = text.fontSharedMaterial != null ? text.fontSharedMaterial.name : "<null>";
            Log.LogInfo(
                "[Compat] Scene=" + SceneManager.GetActiveScene().name +
                " Object=" + text.gameObject.name +
                " Font=" + fontName +
                " Material=" + materialName +
                " Action=Skipped" +
                " Reason=" + reason);
        }

        private static void LogLegacyReplaced(UnityEngine.UI.Text text, Font previousFont)
        {
            if (!CompatibilityLog.Value || decisionLogCapReached)
            {
                return;
            }

            string from = previousFont != null ? previousFont.name : "<null>";
            if (!TryBeginDecisionLog(text, "R|" + from))
            {
                return;
            }

            Log.LogInfo(
                "[Compat] Scene=" + SceneManager.GetActiveScene().name +
                " Object=" + text.gameObject.name +
                " Font=" + text.font.name +
                " Action=Replaced" +
                " From=" + from);
        }

        private static void LogLegacySkipped(UnityEngine.UI.Text text, string reason)
        {
            if (!CompatibilityLog.Value || decisionLogCapReached)
            {
                return;
            }

            string fontName = text.font != null ? text.font.name : "<null>";
            if (!TryBeginDecisionLog(text, "S|" + reason + "|" + fontName))
            {
                return;
            }

            Log.LogInfo(
                "[Compat] Scene=" + SceneManager.GetActiveScene().name +
                " Object=" + text.gameObject.name +
                " Font=" + fontName +
                " Action=Skipped" +
                " Reason=" + reason);
        }

        private static void PruneDestroyed()
        {
            SeenTexts.RemoveWhere(t => t == null);
            SeenLegacyTexts.RemoveWhere(t => t == null);
            SeenInputFields.RemoveWhere(input => input == null);

            if (CjkOverrides.Count > 0)
            {
                List<TMP_Text> deadOverrides = new List<TMP_Text>();
                foreach (KeyValuePair<TMP_Text, CjkOverrideState> pair in CjkOverrides)
                {
                    if (pair.Key == null)
                    {
                        deadOverrides.Add(pair.Key);
                    }
                }

                for (int i = 0; i < deadOverrides.Count; i++)
                {
                    CjkOverrideState state;
                    if (CjkOverrides.TryGetValue(deadOverrides[i], out state) &&
                        state.OwnsMaterial && state.AppliedMaterial != null)
                    {
                        Destroy(state.AppliedMaterial);
                    }
                    CjkOverrides.Remove(deadOverrides[i]);
                }
            }

            List<UnityEngine.Object> deadKeys = null;
            foreach (KeyValuePair<UnityEngine.Object, string> pair in DecisionLogCache)
            {
                if (pair.Key == null)
                {
                    if (deadKeys == null)
                    {
                        deadKeys = new List<UnityEngine.Object>();
                    }

                    deadKeys.Add(pair.Key);
                }
            }

            if (deadKeys != null)
            {
                for (int i = 0; i < deadKeys.Count; i++)
                {
                    DecisionLogCache.Remove(deadKeys[i]);
                }
            }

            List<TMP_Text> deadScans = null;
            foreach (KeyValuePair<TMP_Text, CjkScanState> pair in CjkScanCache)
            {
                if (pair.Key == null)
                {
                    if (deadScans == null)
                    {
                        deadScans = new List<TMP_Text>();
                    }

                    deadScans.Add(pair.Key);
                }
            }

            if (deadScans != null)
            {
                for (int i = 0; i < deadScans.Count; i++)
                {
                    CjkScanCache.Remove(deadScans[i]);
                }
            }

            List<TMP_Text> deadTexts = null;
            foreach (KeyValuePair<TMP_Text, OriginalTextState> pair in OriginalTmpStates)
            {
                if (pair.Key == null)
                {
                    if (deadTexts == null)
                    {
                        deadTexts = new List<TMP_Text>();
                    }

                    deadTexts.Add(pair.Key);
                }
            }

            if (deadTexts != null)
            {
                for (int i = 0; i < deadTexts.Count; i++)
                {
                    OriginalTmpStates.Remove(deadTexts[i]);
                }
            }

            List<UnityEngine.UI.Text> deadLegacyTexts = null;
            foreach (KeyValuePair<UnityEngine.UI.Text, Font> pair in OriginalLegacyFonts)
            {
                if (pair.Key == null)
                {
                    if (deadLegacyTexts == null)
                    {
                        deadLegacyTexts = new List<UnityEngine.UI.Text>();
                    }

                    deadLegacyTexts.Add(pair.Key);
                }
            }

            if (deadLegacyTexts != null)
            {
                for (int i = 0; i < deadLegacyTexts.Count; i++)
                {
                    OriginalLegacyFonts.Remove(deadLegacyTexts[i]);
                }
            }

            List<TMP_InputField> deadInputs = null;
            foreach (KeyValuePair<TMP_InputField, TMP_FontAsset> pair in OriginalInputFonts)
            {
                if (pair.Key == null)
                {
                    if (deadInputs == null)
                    {
                        deadInputs = new List<TMP_InputField>();
                    }

                    deadInputs.Add(pair.Key);
                }
            }

            if (deadInputs != null)
            {
                for (int i = 0; i < deadInputs.Count; i++)
                {
                    OriginalInputFonts.Remove(deadInputs[i]);
                }
            }
        }

        /// <summary>
        /// Destroys a font asset together with the atlas textures and the material that
        /// TMP_FontAsset.CreateFontAsset created for it. Destroying the asset alone leaves the
        /// material and textures behind, both on the cache-trim path and on a failed load.
        /// </summary>
        private static void DestroyFontAssetObjects(TMP_FontAsset asset, Font unityFont)
        {
            if (asset != null)
            {
                try
                {
                    Texture2D[] textures = asset.atlasTextures;
                    if (textures != null)
                    {
                        for (int i = 0; i < textures.Length; i++)
                        {
                            if (textures[i] != null)
                            {
                                Destroy(textures[i]);
                            }
                        }
                    }

                    Material material = asset.material;
                    if (material != null)
                    {
                        Destroy(material);
                    }

                    Destroy(asset);
                }
                catch (Exception ex)
                {
                    Log.LogWarning("Destroying font asset '" + asset.name + "' failed: " + ex.Message);
                }
            }

            if (unityFont != null)
            {
                Destroy(unityFont);
            }
        }

        /// <summary>
        /// Drops every registration of a font whose load failed, then destroys its objects.
        /// The asset may already be in LoadedFonts when the glyph guard or the metric alignment
        /// throws, so the cache entry has to go first.
        /// </summary>
        private static void ReleaseFailedFont(TMP_FontAsset asset, Font unityFont)
        {
            if (asset != null)
            {
                try
                {
                    RemoveFontFromFallbacks(asset);
                    int index = LoadedFonts.IndexOf(asset);
                    if (index >= 0)
                    {
                        RemoveCachedFontEntry(index);
                    }
                }
                catch (Exception ex)
                {
                    Log.LogWarning("Cleaning up failed font '" + asset.name + "' failed: " + ex.Message);
                }
            }

            DestroyFontAssetObjects(asset, unityFont);
        }

        private static TMP_FontAsset LoadFont(string fontPath)
        {
            Font dynamicFont = null;
            TMP_FontAsset fontAsset = null;
            try
            {
                float startedAt = Time.realtimeSinceStartup;
                Log.LogInfo("Loading: " + Path.GetFileName(fontPath));
                dynamicFont = new Font(fontPath);
                int atlasSize = Mathf.Clamp(AtlasSize.Value, 512, 8192);
                int samplingPointSize = CurrentSamplingSize();
                int atlasPadding = Mathf.Clamp(AtlasPadding.Value, 1, 16);
                fontAsset = TMP_FontAsset.CreateFontAsset(
                    dynamicFont,
                    samplingPointSize,
                    atlasPadding,
                    GlyphRenderMode.SDFAA,
                    atlasSize,
                    atlasSize,
                    AtlasPopulationMode.Dynamic,
                    true);

                if (fontAsset == null)
                {
                    Log.LogWarning("  FAIL: no font asset was created for " + Path.GetFileName(fontPath));
                    DestroyFontAssetObjects(null, dynamicFont);
                    return null;
                }

                fontAsset.name = Path.GetFileNameWithoutExtension(fontPath);
                fontAsset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                fontAsset.isMultiAtlasTexturesEnabled = true;
                LoadedFonts.Add(fontAsset);
                LoadedUnityFonts.Add(dynamicFont);
                LoadedFontPaths.Add(fontPath);
                LoadedFontSizes.Add(samplingPointSize);
                LoadedFontStamps.Add(++s_fontUseCounter);
                KnownFonts.Add(fontAsset);
                KnownUnityFonts.Add(dynamicFont);

                if (NativeGlyphGuard.IsActive)
                {
                    int stripped = NativeGlyphGuard.EnsurePure(fontAsset);
                    if (stripped > 0)
                    {
                        Log.LogInfo("  CJK-only glyph set: " + stripped +
                            " synthesized non-CJK character(s) removed.");
                    }
                }

                if (IsPreserveNonCjkEnabled && AlignNativeMetrics.Value)
                {
                    // Match the game's vertical proportions before any text uses this asset, so mixed
                    // lines do not re-layout later with a different line spacing.
                    EnsureNativeMetricsForAll();
                }

                Log.LogInfo("  OK: " + fontAsset.name + " (" + atlasSize + "x" + atlasSize +
                    " dynamic atlas, sampling " + samplingPointSize + ", " + ElapsedMs(startedAt) + " ms)");
                if (IsVariableFont(fontPath))
                {
                    Log.LogInfo("  NOTE: " + fontAsset.name +
                        " is a variable font; Unity 2022 cannot apply variation axes, so its default " +
                        "weight is used and other weights need separate static files.");
                }

                return fontAsset;
            }
            catch (System.Exception ex)
            {
                Log.LogError("  FAIL: " + ex.Message);
                ReleaseFailedFont(fontAsset, dynamicFont);
                return null;
            }
        }

        /// <summary>Milliseconds since a start timestamp, for the load-cost log lines.</summary>
        private static string ElapsedMs(float startedAt)
        {
            return Mathf.RoundToInt((Time.realtimeSinceStartup - startedAt) * 1000f).ToString();
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ResetSeenState();
            ApplyToLoadedText(true);
            ApplyToLoadedLegacyText(true);
        }

        /// <summary>
        /// One frame slice of the PreserveNonCjk rescan. The previous implementation re-scanned all
        /// 1000+ texts (and rebuilt all fallback/native-provider structures) in one frame every second.
        /// The work is identical here, but spread over a short sequence of frames so a scan cannot
        /// produce a visible hitch.
        /// </summary>
        private static void PreserveIncrementalTick()
        {
            if (!IsPreserveNonCjkEnabled)
            {
                s_preserveTexts = null;
                s_preserveIndex = 0;
                return;
            }

            if (s_preserveTexts == null)
            {
                if (Time.unscaledTime < s_preserveNextCycleTime)
                {
                    return;
                }

                BeginPreserveIncrementalCycle();
                if (s_preserveTexts == null || s_preserveTexts.Length == 0)
                {
                    EndPreserveIncrementalCycle();
                    return;
                }
            }

            int end = Mathf.Min(s_preserveIndex + PreserveBatchSize, s_preserveTexts.Length);
            float startedAt = Time.realtimeSinceStartup;
            while (s_preserveIndex < end)
            {
                try
                {
                    PreserveScanOne(s_preserveTexts[s_preserveIndex]);
                }
                catch (Exception ex)
                {
                    LogPassFailure("incremental text", ex);
                }
                s_preserveIndex++;

                // Keep a hard ceiling on this frame. One unusually expensive text (profile or
                // material creation) cannot drag the whole batch with it.
                if (Time.realtimeSinceStartup - startedAt > 0.0015f)
                {
                    break;
                }
            }

            if (s_preserveIndex >= s_preserveTexts.Length)
            {
                EndPreserveIncrementalCycle();
            }
        }

        private static void BeginPreserveIncrementalCycle()
        {
            s_preserveIndex = 0;
            s_preserveStillOurs = 0;
            s_preserveMode = GetEffectiveMode();
            s_preserveTexts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            s_preserveRestoreCache =
                new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);
            ProfileLimitBlocked.Clear();
            PreserveFontUsage.Clear();

            try
            {
                RebuildFallbackChain();
            }
            catch (Exception ex)
            {
                LogPassFailure("incremental fallback chain", ex);
            }

            bool guardActive = NativeGlyphGuard.IsActive;
            if (guardActive)
            {
                try
                {
                    if (!s_glyphGuardWasActive)
                    {
                        NativeGlyphGuard.InvalidatePurity();
                    }

                    int stripped = PurgeNonCjkGlyphs();
                    int recovered = 0;
                    if (NativeGlyphGuard.TakeProvidersChanged())
                    {
                        recovered = NativeGlyphGuard.PurgeRecoverable();
                    }

                    if (stripped > 0 || recovered > 0)
                    {
                        MarkSeenTextsDirty(s_preserveTexts);
                    }
                }
                catch (Exception ex)
                {
                    LogPassFailure("incremental glyph purge", ex);
                }
            }

            s_glyphGuardWasActive = guardActive;

            if (IsPreserveNonCjkEnabled && AlignNativeMetrics.Value)
            {
                try
                {
                    if (EnsureNativeMetricsForAll() > 0)
                    {
                        MarkSeenTextsDirty(s_preserveTexts);
                    }
                }
                catch (Exception ex)
                {
                    LogPassFailure("incremental font metrics", ex);
                }
            }
        }

        private static void PreserveScanOne(TMP_Text text)
        {
            if (text == null || !IsLiveSceneObject(text))
            {
                return;
            }

            if (text.font == null)
            {
                TMP_FontAsset repaired = ResolveFallbackNativeFont();
                if (repaired != null)
                {
                    text.font = repaired;
                    text.SetAllDirty();
                }
            }

            if (IsKnownFont(text.font))
            {
                RememberDefaultSnapshot(text);
            }

            if (TryApplyCjkOverride(text, s_preserveMode))
            {
                return;
            }

            RestoreTextFontIfOurs(text, s_preserveRestoreCache);
            if (IsKnownFont(text.font) && !IsFontProfile(text.font))
            {
                s_preserveStillOurs++;
            }

            if (!IsKnownFont(text.font) && TextHasCjk(text))
            {
                HookFontFallback(text.font);
                if (text.font != null)
                {
                    int usage;
                    PreserveFontUsage.TryGetValue(text.font, out usage);
                    PreserveFontUsage[text.font] = usage + 1;
                }
            }
        }

        private static void EndPreserveIncrementalCycle()
        {
            if (s_preserveStillOurs > 0)
            {
                Log.LogWarning("[Preserve] " + s_preserveStillOurs +
                    " text(s) still carry the plugin font: no snapshot and no game font stand-in.");
            }

            try
            {
                UpdatePreserveMetricsReference();
            }
            catch (Exception ex)
            {
                LogPassFailure("incremental metrics reference", ex);
            }

            try
            {
                ApplyToLoadedLegacyText(false);
            }
            catch (Exception ex)
            {
                LogPassFailure("incremental legacy text", ex);
            }

            int scanned = s_preserveTexts != null ? s_preserveTexts.Length : 0;
            s_preserveTexts = null;
            s_preserveIndex = 0;
            s_preserveStillOurs = 0;
            s_preserveRestoreCache = null;
            s_preserveNextCycleTime = Time.unscaledTime + (scanned == 0 ? 2f : 0.25f);
        }

        private static void ApplyToLoadedText(bool logResult)
        {
            if (!IsReplacementEnabled())
            {
                return;
            }

            // Nothing selected: stay completely out of the way, fallbacks included.
            if (GetPrimaryFontAsset() == null && GetEffectiveMode() != FontReplaceMode.FallbackOnly)
            {
                return;
            }

            // One profile-limit retry per source per full pass is enough; without this cache a
            // full pass re-ran TrimFontProfiles (and its live-text reference scan) for every CJK
            // text after the cap was reached.
            ProfileLimitBlocked.Clear();

            // Each stage has its own exception boundary so one failure cannot break the plugin, and
            // failures are logged with a throttle instead of once per scan. A failure inside a loop
            // still ends that stage for this pass; the next pass retries it.
            int chainedFallbacks = 0;
            try
            {
                chainedFallbacks = RebuildFallbackChain();
            }
            catch (Exception ex)
            {
                LogPassFailure("fallback chain", ex);
            }

            // Fetched before the purge blocks so they can regenerate every text that carries our font,
            // including ones that are not tracked in SeenTexts (see MarkSeenTextsDirty).
            TMP_Text[] allTexts = Resources.FindObjectsOfTypeAll<TMP_Text>();

            // A later frame than the switch, report what the screen actually shows; the layout of the
            // switching frame is not rebuilt before its end, so checking earlier proves nothing.
            if (s_preserveCheckPending && Time.frameCount > s_preserveCheckFrame)
            {
                s_preserveCheckPending = false;
                if (IsPreserveNonCjkEnabled)
                {
                    try
                    {
                        LogPreserveRenderState(allTexts);
                    }
                    catch (Exception ex)
                    {
                        LogPassFailure("preserve check", ex);
                    }
                }
            }

            // Glyphs added while the option was off keep being served from the lookup table even with
            // the guard on, so outstanding ones are stripped before the pass re-resolves the texts.
            // The purge only looks at assets it has not seen yet, so a false → true edge (option just
            // switched on, or a mode change made it effective) clears that memory first.
            bool guardActive = NativeGlyphGuard.IsActive;
            if (guardActive)
            {
                if (!s_glyphGuardWasActive)
                {
                    NativeGlyphGuard.InvalidatePurity();
                }

                try
                {
                    int stripped = PurgeNonCjkGlyphs();

                    // Hand back anything the native fonts can render now; a glyph the game really lacks
                    // stays in the plugin font so the text does not turn into boxes. Only provider
                    // changes can make a glyph recoverable, so this scans the font tables at most
                    // once per coverage change instead of once per second.
                    int recovered = 0;
                    if (NativeGlyphGuard.TakeProvidersChanged())
                    {
                        recovered = NativeGlyphGuard.PurgeRecoverable();
                    }

                    if (recovered > 0)
                    {
                        string message = "[Native] returned " + recovered +
                            " glyph(s) the game fonts can render again.";
                        if ((LogTextDetails != null && LogTextDetails.Value) ||
                            (CompatibilityLog != null && CompatibilityLog.Value))
                        {
                            Log.LogInfo(message);
                        }
                        else
                        {
                            Log.LogDebug(message);
                        }
                    }

                    // Both purges drop characters from the plugin font's table, but a text laid out
                    // before them still references the removed glyphs until it regenerates. That is not
                    // limited to CJK texts: a digits-only row can have been drawn by the plugin font, so
                    // MarkSeenTextsDirty looks at the text's actual glyphs (see TextRendersOurGlyph).
                    if (stripped > 0 || recovered > 0)
                    {
                        MarkSeenTextsDirty(allTexts);
                    }
                }
                catch (Exception ex)
                {
                    LogPassFailure("glyph purge", ex);
                }
            }

            // Line metrics are not tied to the glyph guard: fallback-drawn CJK glyphs benefit from
            // matching the game font's vertical proportions even without the guard.
            if (IsPreserveNonCjkEnabled && AlignNativeMetrics.Value)
            {
                try
                {
                    // Late reference resolution, a font built before the option was on, or a different
                    // font taking over the non-CJK glyphs all need their metrics matched here.
                    int aligned = EnsureNativeMetricsForAll();
                    if (aligned > 0)
                    {
                        // Texts laid out earlier still hold the old metrics; force them to re-generate.
                        MarkSeenTextsDirty(allTexts);
                    }
                }
                catch (Exception ex)
                {
                    LogPassFailure("font metrics", ex);
                }
            }

            s_glyphGuardWasActive = guardActive;

            int replaced = 0;
            int skipped = 0;
            int menuTexts = 0;
            int menuOurs = 0;
            int stillOurs = 0;

            // Settings-menu statistics are diagnostics only: skip the per-text hierarchy walk
            // unless one of the debug switches is on.
            bool collectMenuStats = LogTextDetails.Value || CompatibilityLog.Value;
            FontReplaceMode mode = GetEffectiveMode();
            bool enforceRepeatedly = mode == FontReplaceMode.Aggressive;

            // Shared by every revert in this pass so a dead snapshot is looked up at most once.
            Dictionary<string, TMP_FontAsset> restoreCache =
                new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);

            scanPassCount++;
            if (scanPassCount % 32 == 0)
            {
                PruneDestroyed();
            }

            if (IsPreserveNonCjkEnabled)
            {
                PreserveFontUsage.Clear();
            }

            try
            {
                foreach (TMP_Text text in allTexts)
                {
                    if (text == null)
                    {
                        continue;
                    }

                    // Prefab assets are not on screen and must not be re-fonted: the font is baked into
                    // the asset, every later instance is born with it, and such a clone has no snapshot
                    // of its own, so PreserveNonCjk could not put it back on a game font.
                    if (!IsLiveSceneObject(text))
                    {
                        continue;
                    }

                    // A destroyed profile leaves the text holding a fake-null font. Nothing can
                    // render until it is put back on a live game font; otherwise the row stays blank
                    // even after the new primary and its profile have been created.
                    if (text.font == null)
                    {
                        TMP_FontAsset repaired = ResolveFallbackNativeFont();
                        if (repaired != null)
                        {
                            text.font = repaired;
                            text.SetAllDirty();
                        }
                    }

                    // Texts that already carry our font (e.g. instances of a prefab replaced in an
                    // earlier pass) have no snapshot; record a game font stand-in so a later skip, mode
                    // change or disable can actually put them back on a game font.
                    if (IsKnownFont(text.font))
                    {
                        RememberDefaultSnapshot(text);
                    }

                    if (collectMenuStats && IsInSettingsMenu(text.transform))
                    {
                        menuTexts++;
                        if (IsKnownFont(text.font))
                        {
                            menuOurs++;
                        }
                    }

                    if (mode == FontReplaceMode.FallbackOnly)
                    {
                        // FallbackOnly never replaces: a text still carrying our font (from an earlier
                        // mode) goes back to the game font, the font itself stays registered as fallback.
                        RestoreTextFontIfOurs(text, restoreCache);
                        continue;
                    }

                    if (IsPreserveNonCjkEnabled)
                    {
                        // White-listed source fonts (ScienceUnifont) already own the CJK glyphs,
                        // so a fallback can never replace them. Switch those texts to a CJK-only
                        // profile first; all other fonts keep the original fallback-only path.
                        if (TryApplyCjkOverride(text, mode))
                        {
                            continue;
                        }

                        // Priority fallback mode. The text keeps its own font, so every character the
                        // game font can draw - digits, Latin, symbols, its own material, its own line
                        // metrics - stays exactly as shipped. Only the CJK glyphs it cannot draw come
                        // from the plugin font, which is inserted as the first fallback of that font.
                        RestoreTextFontIfOurs(text, restoreCache);
                        if (IsKnownFont(text.font) && !IsFontProfile(text.font))
                        {
                            // The restore found neither a usable snapshot nor a game font to stand in
                            // for it (see ResolveFallbackNativeFont); report it instead of silently
                            // leaving a whole row on the plugin font. A profile kept on purpose is
                            // not this failure case.
                            stillOurs++;
                        }

                        if (!IsKnownFont(text.font) && TextHasCjk(text))
                        {
                            HookFontFallback(text.font);

                            // A text without a font cannot be counted: Dictionary keys must not be null.
                            if (text.font != null)
                            {
                                int usage;
                                PreserveFontUsage.TryGetValue(text.font, out usage);
                                PreserveFontUsage[text.font] = usage + 1;
                            }
                        }

                        continue;
                    }

                    TextDecision decision = EvaluateTMPText(text);
                    if (!decision.Replace)
                    {
                        // A null reason means "already correct, nothing to do" (the text already uses
                        // our font and material), so only a real skip may revert the text.
                        if (decision.SkipReason != null)
                        {
                            skipped++;
                            LogSkipped(text, decision.SkipReason);

                            // A text that no longer qualifies (stricter mode, new exclusion, custom
                            // material) must not keep our font from a previous pass.
                            RestoreTextFontIfOurs(text, restoreCache);
                        }

                        continue;
                    }

                    bool firstSeen = !SeenTexts.Contains(text);
                    if (!enforceRepeatedly && !firstSeen &&
                        !(mode == FontReplaceMode.Consistent && NeedsConsistentReplacement(text)))
                    {
                        continue;
                    }

                    TMP_FontAsset previousFont = text.font;
                    Material previousMaterial = text.fontSharedMaterial;
                    if (ReplaceTextFont(text))
                    {
                        SeenTexts.Add(text);
                        replaced++;
                        if (previousFont == text.font && previousMaterial != text.fontSharedMaterial)
                        {
                            LogMaterialFixed(text, previousMaterial);
                        }
                        else
                        {
                            LogReplaced(text, previousFont);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogPassFailure("text replacement", ex);
            }

            if (IsPreserveNonCjkEnabled && stillOurs > 0)
            {
                Log.LogWarning("[Preserve] " + stillOurs +
                    " text(s) still carry the plugin font: no snapshot of their own and no game font " +
                    "was available to stand in. See the [Preserve] lines above for the object names.");
            }

            if (IsPreserveNonCjkEnabled)
            {
                try
                {
                    UpdatePreserveMetricsReference();

                    // A new primary (local, system or weight) only changes the fallback chain; the
                    // glyphs already resolved in textInfo still point at the previous asset.
                    if (PrimaryFontAsset != s_lastLayoutPrimary)
                    {
                        s_lastLayoutPrimary = PrimaryFontAsset;
                        // Force every live text, not just texts whose current glyph table still points
                        // at a known plugin asset: after switching away from a non-CJK font the old
                        // profile may already be gone, so TextRendersOurGlyph cannot see it. A new
                        // primary is a different fallback chain; every text must be laid out again.
                        MarkSeenTextsDirty(allTexts, true);
                        Log.LogInfo("[Native] plugin font changed to '" +
                            (PrimaryFontAsset != null ? PrimaryFontAsset.name : "<none>") +
                            "'; regenerating texts.");
                    }
                }
                catch (Exception ex)
                {
                    LogPassFailure("preserve metrics reference", ex);
                }
            }

            if (LogTextDetails.Value)
            {
                try
                {
                    LogTextSnapshot(allTexts);
                }
                catch (Exception ex)
                {
                    LogPassFailure("text details", ex);
                }
            }

            int patchedControls = 0;
            try
            {
                patchedControls = PatchTmpControls();
            }
            catch (Exception ex)
            {
                LogPassFailure("input fields", ex);
            }

            if (logResult || replaced > 0)
            {
                Log.LogInfo(
                    "Font pass: scanned " + allTexts.Length +
                    ", replaced " + replaced +
                    ", skipped " + skipped +
                    ", fallback chain " + chainedFallbacks +
                    ", patched controls " + patchedControls);
            }

            if (collectMenuStats)
            {
                LogSettingsMenuStats(menuTexts, menuOurs);
            }
        }

        /// <summary>
        /// Logs a stage failure once with its stack, then at most once a minute with a repeat count, so
        /// a persistent fault stays visible without flooding the log every scan.
        /// </summary>
        private static void LogPassFailure(string stage, Exception ex)
        {
            s_passFailureCount++;
            string signature = stage + "|" + ex.GetType().Name + "|" + ex.Message;
            bool isNew = signature != s_passFailureSignature;
            float now = Time.unscaledTime;

            if (isNew)
            {
                s_passFailureSignature = signature;
                s_passFailureCount = 1;
            }
            else if (now < s_nextFailureLogTime)
            {
                return;
            }

            s_nextFailureLogTime = now + 60f;
            if (isNew)
            {
                Log.LogError("[Pass] " + stage + " failed, remaining stages still run: " +
                    ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
            }
            else
            {
                Log.LogError("[Pass] " + stage + " failed again (" + s_passFailureCount +
                    " times so far): " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>
        /// One diagnostic line, only when the numbers change: how many TMP texts belong to the game's
        /// settings menu and how many of them carry our font. Needed to tell a skip apart from a
        /// restore that did not happen.
        /// </summary>
        private static void LogSettingsMenuStats(int menuTexts, int menuOurs)
        {
            if (menuTexts == s_lastMenuTexts && menuOurs == s_lastMenuOurs)
            {
                return;
            }

            s_lastMenuTexts = menuTexts;
            s_lastMenuOurs = menuOurs;
            Log.LogDebug("[Panel] settings menu TMP texts: " + menuTexts + ", using our font: " + menuOurs);
        }

        /// <summary>
        /// True when the transform lives under the game's settings menu. The type may be internal, so it
        /// is resolved once by name instead of at compile time.
        /// </summary>
        private static bool IsInSettingsMenu(Transform transform)
        {
            Type menuType = GetSettingsMenuType();
            if (menuType == null || transform == null)
            {
                return false;
            }

            Transform current = transform;
            for (int depth = 0; current != null && depth < 16; depth++)
            {
                if (current.GetComponent(menuType) != null)
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        private static Type GetSettingsMenuType()
        {
            if (!s_settingsMenuTypeResolved)
            {
                s_settingsMenuTypeResolved = true;
                try
                {
                    s_settingsMenuType = Type.GetType("SettingsMenu, Assembly-CSharp", false);
                }
                catch (Exception)
                {
                    s_settingsMenuType = null;
                }
            }

            return s_settingsMenuType;
        }

        private static void LogTextSnapshot(TMP_Text[] allTexts)
        {
            int limit = Mathf.Clamp(MaxLoggedTexts.Value, 1, 500);
            int logged = 0;
            foreach (TMP_Text text in allTexts)
            {
                if (text == null)
                {
                    continue;
                }

                GameObject obj = text.gameObject;
                string sample = text.text;
                if (sample == null)
                {
                    sample = string.Empty;
                }
                sample = sample.Replace("\r", " ").Replace("\n", " ");
                if (sample.Length > 32)
                {
                    sample = sample.Substring(0, 32);
                }

                string fontName = text.font != null ? text.font.name : "<null>";
                string materialName = text.fontSharedMaterial != null ? text.fontSharedMaterial.name : "<null>";
                Log.LogInfo(
                    "Text detail: active=" + obj.activeInHierarchy +
                    ", name=" + obj.name +
                    ", type=" + text.GetType().Name +
                    ", font=" + fontName +
                    ", material=" + materialName +
                    ", glyphs=" + DescribeGlyphFonts(text) +
                    ", text=\"" + sample + "\"");

                logged++;
                if (logged >= limit)
                {
                    break;
                }
            }
        }

        private static int PatchTmpControls()
        {
            if (LoadedFonts.Count == 0 || GetEffectiveMode() == FontReplaceMode.FallbackOnly)
            {
                return 0;
            }

            TMP_FontAsset primary = GetPrimaryFontAsset();
            if (primary == null)
            {
                return 0;
            }

            if (IsPreserveNonCjkEnabled)
            {
                // Priority fallback mode: input fields keep their own font and only the CJK glyphs they
                // lack come from the plugin font, so the font must be in their font's fallback chain.
                TMP_InputField[] fields = Resources.FindObjectsOfTypeAll<TMP_InputField>();
                for (int i = 0; i < fields.Length; i++)
                {
                    TMP_InputField field = fields[i];
                    if (field == null || !IsLiveSceneObject(field))
                    {
                        continue;
                    }

                    RestoreInputFontIfOurs(field);
                    if (field.textComponent != null)
                    {
                        HookFontFallback(field.textComponent.font);
                    }
                }

                return 0;
            }

            int patched = 0;
            FontReplaceMode mode = GetEffectiveMode();
            bool enforceRepeatedly = mode == FontReplaceMode.Aggressive;

            TMP_InputField[] inputFields = Resources.FindObjectsOfTypeAll<TMP_InputField>();
            foreach (TMP_InputField inputField in inputFields)
            {
                if (inputField == null || !IsLiveSceneObject(inputField))
                {
                    continue;
                }

                TMP_Text textComponent = inputField.textComponent;
                if (textComponent == null || textComponent.font != primary)
                {
                    // The component is not on our font (skipped, or already reverted): if the field
                    // itself still carries one of ours, revert it so it does not keep our asset alone.
                    RestoreInputFontIfOurs(inputField);
                    continue;
                }

                TextDecision decision = EvaluateTMPText(textComponent);
                if (decision.SkipReason != null)
                {
                    RestoreInputFontIfOurs(inputField);
                    continue;
                }

                bool firstSeen = SeenInputFields.Add(inputField);
                if (!enforceRepeatedly && !firstSeen)
                {
                    continue;
                }

                if (inputField.fontAsset != primary)
                {
                    // Never snapshot our own asset: it would become the "original" to restore to.
                    if (!OriginalInputFonts.ContainsKey(inputField) && !IsKnownFont(inputField.fontAsset))
                    {
                        OriginalInputFonts[inputField] = inputField.fontAsset;
                    }

                    inputField.fontAsset = primary;
                    patched++;
                }
            }

            return patched;
        }

        private static void ApplyToLoadedLegacyText(bool logResult)
        {
            if (!IsReplacementEnabled())
            {
                return;
            }

            if (LoadedUnityFonts.Count == 0 || GetEffectiveMode() == FontReplaceMode.FallbackOnly)
            {
                return;
            }

            if (PrimaryUnityFont == null)
            {
                return;
            }

            FontReplaceMode mode = GetEffectiveMode();
            bool enforceRepeatedly = mode == FontReplaceMode.Aggressive;

            UnityEngine.UI.Text[] allTexts = Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>();
            int replaced = 0;

            try
            {
                ReplaceLoadedLegacyTexts(allTexts, enforceRepeatedly, ref replaced);
            }
            catch (Exception ex)
            {
                LogPassFailure("legacy text replacement", ex);
            }
            finally
            {
                if (logResult || replaced > 0)
                {
                    Log.LogInfo("Legacy UI.Text pass: scanned " + allTexts.Length + ", replaced " + replaced);
                }
            }
        }

        private static void ReplaceLoadedLegacyTexts(UnityEngine.UI.Text[] allTexts, bool enforceRepeatedly,
            ref int replaced)
        {
            bool consistent = GetEffectiveMode() == FontReplaceMode.Consistent;
            foreach (UnityEngine.UI.Text text in allTexts)
            {
                if (text == null)
                {
                    continue;
                }

                if (text.font == null)
                {
                    continue;
                }

                // Prefab assets are not on screen; re-fonting one would bake our font into every later
                // instance and that clone could never be restored (no snapshot of its own).
                if (!IsLiveSceneObject(text))
                {
                    continue;
                }

                if ((ExcludedObjectPatterns.Count > 0 &&
                        MatchesExclusion(text.gameObject.name, ExcludedObjectPatterns)) ||
                    (ExcludedFontPatterns.Count > 0 &&
                        MatchesExclusion(text.font.name, ExcludedFontPatterns)))
                {
                    LogLegacySkipped(text, "Excluded");
                    RestoreLegacyFontIfOurs(text);
                    continue;
                }

                if (IsCjkOverrideActive && text.font != null &&
                    MatchesExclusion(text.font.name, CjkOverridePatterns) &&
                    !CjkText.IsPureCjk(text.text))
                {
                    string legacyKey = "legacy:" + text.gameObject.name;
                    if (CjkOverrideLogCache.Count < 40 && CjkOverrideLogCache.Add(legacyKey))
                    {
                        Log.LogInfo("[Cjk] legacy UI.Text '" + text.gameObject.name + "' uses '" +
                            text.font.name + "' but has mixed content; legacy cannot switch only CJK glyphs.");
                    }
                }

                // Legacy UI.Text has no per-glyph fallback: replacing the font would re-draw digits and
                // Latin with the plugin font too. Only labels that are pure CJK (nothing but CJK
                // characters, whitespace and CJK punctuation) can be re-fonted without breaking the
                // native look.
                if (IsPreserveNonCjkEnabled && !CjkText.IsPureCjk(text.text))
                {
                    LogLegacySkipped(text, "NativeNonCjk");
                    RestoreLegacyFontIfOurs(text);
                    continue;
                }

                bool firstSeen = !SeenLegacyTexts.Contains(text);
                if (!enforceRepeatedly && !firstSeen)
                {
                    Font original;
                    if (!consistent || text.font == PrimaryUnityFont ||
                        !OriginalLegacyFonts.TryGetValue(text, out original) || text.font != original)
                    {
                        continue;
                    }
                }

                Font previousFont = text.font;
                if (text.font != PrimaryUnityFont)
                {
                    // Never snapshot one of our own fonts: it would become the "original" to restore.
                    if (!OriginalLegacyFonts.ContainsKey(text) && !KnownUnityFonts.Contains(previousFont))
                    {
                        OriginalLegacyFonts[text] = previousFont;
                    }

                    text.font = PrimaryUnityFont;
                    SeenLegacyTexts.Add(text);
                    replaced++;
                    LogLegacyReplaced(text, previousFont);
                }
            }
        }

        /// <summary>
        /// Rebuilds the fallback chain from scratch on every pass: our fonts never fall back to each
        /// other, so a previously applied font can never linger and serve glyphs the current font
        /// lacks. The game's own font assets are the only fallback, which is what makes CJK text render
        /// in the vanilla font when the selected font has no glyph for it. The primary is also kept in
        /// the global list, which is what FallbackOnly mode relies on. Returns how many entries changed.
        /// </summary>
        private static int RebuildFallbackChain()
        {
            int changed = 0;

            List<TMP_FontAsset> globalFallbacks = TMP_Settings.fallbackFontAssets;
            if (globalFallbacks != null)
            {
                TMP_FontAsset primary = PrimaryFontAsset;
                bool front = IsPreserveNonCjkEnabled && GetEffectiveMode() != FontReplaceMode.FallbackOnly;

                // Drop plugin fonts that should not be in the global list, but leave the primary alone
                // when it already sits in its target slot: removing and re-adding it every pass would
                // report a change, invalidate the glyph-purity state and force a full purge each scan.
                for (int i = globalFallbacks.Count - 1; i >= 0; i--)
                {
                    TMP_FontAsset entry = globalFallbacks[i];
                    if (!KnownFonts.Contains(entry))
                    {
                        continue;
                    }

                    bool inPlace = entry == primary &&
                        ((front && i == 0) || (!front && i == globalFallbacks.Count - 1));
                    if (inPlace)
                    {
                        continue;
                    }

                    globalFallbacks.RemoveAt(i);
                    changed++;
                }

                // In PreserveNonCjk mode CJK text is served by priority fallback, so the font must be in
                // front of the translation/fallback fonts that are already there. Other modes append it
                // (FallbackOnly is exactly "register globally, change nothing").
                if (primary != null)
                {
                    if (front)
                    {
                        if (globalFallbacks.Count == 0 || globalFallbacks[0] != primary)
                        {
                            globalFallbacks.Remove(primary);
                            globalFallbacks.Insert(0, primary);
                            changed++;
                        }
                    }
                    else if (globalFallbacks.Count == 0 || globalFallbacks[globalFallbacks.Count - 1] != primary)
                    {
                        globalFallbacks.Remove(primary);
                        globalFallbacks.Add(primary);
                        changed++;
                    }
                }
            }

            List<TMP_FontAsset> originals = CollectOriginalFonts();

            // An empty list means no snapshot could be resolved right now (e.g. the previous scene's
            // fonts were unloaded but the new ones are not snapshotted yet). Keep the previous chain:
            // clearing it would leave the plugin font with no native fallback for a whole pass, and
            // every non-CJK glyph rendered in that window would stick to the plugin font.
            if (originals.Count > 0)
            {
                foreach (TMP_FontAsset font in KnownFonts)
                {
                    if (font == null || IsFontProfile(font))
                    {
                        // Profiles own their [source] + source-fallbacks chain; replacing it with
                        // the global originals list would break the source-font fallback and could
                        // make the profile loop back into the primary font.
                        continue;
                    }

                    List<TMP_FontAsset> table = font.fallbackFontAssetTable;
                    if (table == null)
                    {
                        table = new List<TMP_FontAsset>();
                        font.fallbackFontAssetTable = table;
                    }
                    else if (SameFallbackChain(table, originals))
                    {
                        continue;
                    }

                    // Drop every plugin font and stale copies of the originals, then append in a fixed
                    // order. SameFallbackChain already proved the table differs, so this is a change even
                    // when the entry count happens to stay the same (a pure reorder).
                    table.RemoveAll(entry => entry == null || KnownFonts.Contains(entry) || originals.Contains(entry));
                    table.AddRange(originals);
                    changed++;
                }
            }

            // A changed chain means a character may now be reachable from a game font where it was
            // not before: let the guard hand such glyphs back on the purge that follows this call.
            if (changed > 0 && NativeGlyphGuard.IsActive)
            {
                NativeGlyphGuard.InvalidatePurity();
            }

            // TMP searches each fallback font's own chain recursively; flatten everything reachable so
            // the glyph guard knows the whole native coverage before it takes a character for itself.
            NativeGlyphGuard.RefreshNativeProviders(originals);

            return changed;
        }

        private static bool SameFallbackChain(List<TMP_FontAsset> table, List<TMP_FontAsset> originals)
        {
            if (table.Count != originals.Count)
            {
                return false;
            }

            for (int i = 0; i < originals.Count; i++)
            {
                if (table[i] != originals[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Puts the selected font at the front of a game font's fallback chain. In PreserveNonCjk mode
        /// this is how CJK text is replaced without touching the text: TMP keeps drawing digits, Latin
        /// and symbols with the game font and only asks the fallback for the CJK characters it lacks.
        /// </summary>
        private static void HookFontFallback(TMP_FontAsset font)
        {
            if (font == null || PrimaryFontAsset == null || IsKnownFont(font))
            {
                return;
            }

            // A profile matches this source font's scale/metrics. If it cannot be created (cap
            // reached, primary missing), the primary font is still a better fallback than none.
            TMP_FontAsset primary = GetProfile(font, true);
            if (primary == null)
            {
                primary = PrimaryFontAsset;
            }

            if (primary == null)
            {
                return;
            }

            List<TMP_FontAsset> table = font.fallbackFontAssetTable;
            if (table == null)
            {
                table = new List<TMP_FontAsset>();
                font.fallbackFontAssetTable = table;
                HookedFontsWithoutTable.Add(font);
            }

            // Our fonts never stack on each other, and one left over from an earlier primary font is
            // dropped here as well; the game's own fallbacks keep their order after it.
            table.RemoveAll(entry => entry == null || (IsKnownFont(entry) && entry != primary));
            if (table.Count > 0 && table[0] == primary)
            {
                HookedFonts.Add(font);
                return;
            }

            table.Insert(0, primary);
            if (HookedFonts.Add(font) && (LogTextDetails.Value || CompatibilityLog.Value))
            {
                Log.LogInfo("[Native] '" + primary.name + "' is now the first fallback of '" + font.name + "'.");
            }
        }

        /// <summary>Removes the plugin font from every fallback chain this plugin extended.</summary>
        private static void UnhookFontFallbacks()
        {
            if (HookedFonts.Count == 0)
            {
                HookedFontsWithoutTable.Clear();
                return;
            }

            foreach (TMP_FontAsset font in HookedFonts)
            {
                if (font == null)
                {
                    continue;
                }

                List<TMP_FontAsset> table = font.fallbackFontAssetTable;
                if (table == null)
                {
                    continue;
                }

                table.RemoveAll(entry => entry == null || IsKnownFont(entry));
                if (table.Count == 0 && HookedFontsWithoutTable.Contains(font))
                {
                    font.fallbackFontAssetTable = null;
                }
            }

            HookedFonts.Clear();
            HookedFontsWithoutTable.Clear();
        }

        /// <summary>
        /// The game's own font assets, taken from the snapshots. The order is stable so the font that
        /// serves non-CJK glyphs (and that the plugin font's metrics are aligned to) does not drift as
        /// more texts are seen: the game default comes first when it is in use, the rest follow by name.
        /// A still-empty chain is seeded with the game default so the first pass already has a native
        /// target to fall back to.
        /// </summary>
        private static List<TMP_FontAsset> CollectOriginalFonts()
        {
            List<TMP_FontAsset> originals = new List<TMP_FontAsset>();
            foreach (OriginalTextState state in OriginalTmpStates.Values)
            {
                TMP_FontAsset original = ResolveFontAsset(state, OriginalAssetLookup);
                if (original == null || KnownFonts.Contains(original) || originals.Contains(original))
                {
                    continue;
                }

                originals.Add(original);
            }

            TMP_FontAsset gameDefault = null;
            if (IsPreserveNonCjkEnabled)
            {
                try
                {
                    gameDefault = TMP_Settings.defaultFontAsset;
                }
                catch (Exception)
                {
                    gameDefault = null;
                }

                if (gameDefault != null && KnownFonts.Contains(gameDefault))
                {
                    gameDefault = null;
                }
            }

            if (originals.Count == 0 && gameDefault != null)
            {
                originals.Add(gameDefault);
            }

            if (gameDefault != null)
            {
                originals.Sort((a, b) =>
                {
                    if (a == gameDefault)
                    {
                        return b == gameDefault ? 0 : -1;
                    }

                    if (b == gameDefault)
                    {
                        return 1;
                    }

                    return string.CompareOrdinal(a.name, b.name);
                });
            }
            else
            {
                originals.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            }

            return originals;
        }

        /// <summary>
        /// Records a stand-in game font as the snapshot of a text that already carries our font but has
        /// no snapshot of its own (an instance of a prefab that was replaced in an earlier pass). Without
        /// it a skip, a mode change or disabling the plugin could not put the text back on a game font.
        /// </summary>
        private static TMP_FontAsset RememberDefaultSnapshot(TMP_Text text)
        {
            if (text == null || OriginalTmpStates.ContainsKey(text))
            {
                return null;
            }

            TMP_FontAsset native = ResolveFallbackNativeFont();
            if (native == null)
            {
                return null;
            }

            OriginalTmpStates[text] = CaptureTextState(native, native.material);
            return native;
        }

        /// <summary>
        /// Best game font for a text whose original font is unknown: its snapshot is missing (a clone of
        /// a prefab that was re-fonted in an earlier pass) or no longer resolves. The font the rest of the
        /// UI uses is the closest thing to "the game font this text was built with", so usage is preferred
        /// over TMP's global default; the game fonts seen in snapshots are the next best record. Null when
        /// no game font is known at all.
        /// </summary>
        private static TMP_FontAsset ResolveFallbackNativeFont()
        {
            TMP_FontAsset reference = s_preserveMetricsReference;
            if (reference != null && !IsKnownFont(reference))
            {
                return reference;
            }

            TMP_FontAsset best = null;
            int bestCount = 0;
            foreach (KeyValuePair<TMP_FontAsset, int> pair in PreserveFontUsage)
            {
                if (pair.Key == null || IsKnownFont(pair.Key) || pair.Value <= bestCount)
                {
                    continue;
                }

                best = pair.Key;
                bestCount = pair.Value;
            }

            if (best != null)
            {
                return best;
            }

            // First pass after switching the mode on: no usage has been counted yet, so the snapshots
            // are the only record of which game fonts this UI uses. Pick the one the most texts use.
            List<TMP_FontAsset> candidates = new List<TMP_FontAsset>();
            foreach (OriginalTextState state in OriginalTmpStates.Values)
            {
                TMP_FontAsset candidate = state.Font;
                if (candidate == null || IsKnownFont(candidate) || candidates.Contains(candidate))
                {
                    continue;
                }

                candidates.Add(candidate);
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                TMP_FontAsset candidate = candidates[i];
                int count = 0;
                foreach (OriginalTextState state in OriginalTmpStates.Values)
                {
                    if (state.Font == candidate)
                    {
                        count++;
                    }
                }

                if (count > bestCount ||
                    (count == bestCount && best != null &&
                     string.CompareOrdinal(candidate.name, best.name) < 0))
                {
                    best = candidate;
                    bestCount = count;
                }
            }

            if (best != null)
            {
                return best;
            }

            TMP_FontAsset gameDefault = null;
            try
            {
                gameDefault = TMP_Settings.defaultFontAsset;
            }
            catch (Exception)
            {
                gameDefault = null;
            }

            return gameDefault != null && !IsKnownFont(gameDefault) ? gameDefault : null;
        }

        /// <summary>
        /// True/false summary of which font asset actually renders the visible characters. Used by the
        /// text-details diagnostics to tell "the plugin font owns this digit" apart from "the digit is
        /// simply rendered by a fallback font".
        /// </summary>
        private static string DescribeGlyphFonts(TMP_Text text)
        {
            TMP_TextInfo info = text.textInfo;
            if (info == null || info.characterInfo == null || info.characterCount <= 0)
            {
                return "n/a";
            }

            int ours = 0;
            int native = 0;
            string taken = string.Empty;
            string nonCjkFonts = string.Empty;
            int limit = Mathf.Min(info.characterCount, info.characterInfo.Length);
            for (int i = 0; i < limit; i++)
            {
                TMP_CharacterInfo characterInfo = info.characterInfo[i];
                if (!characterInfo.isVisible)
                {
                    continue;
                }

                TMP_TextElement element = characterInfo.textElement;
                TMP_FontAsset font = element != null ? element.textAsset as TMP_FontAsset : null;
                if (IsKnownFont(font))
                {
                    ours++;
                    if (!CjkText.IsCjk(characterInfo.character) && taken.Length < 8)
                    {
                        taken += characterInfo.character;
                    }
                }
                else
                {
                    native++;
                    if (!CjkText.IsCjk(characterInfo.character) && font != null &&
                        nonCjkFonts.IndexOf(font.name, StringComparison.Ordinal) < 0)
                    {
                        nonCjkFonts = nonCjkFonts.Length == 0 ? font.name : nonCjkFonts + "," + font.name;
                    }
                }
            }

            return "ours=" + ours + ",native=" + native +
                (taken.Length > 0 ? ",nonCjkOnOurs=\"" + taken + "\"" : string.Empty) +
                (nonCjkFonts.Length > 0 ? ",nonCjkFonts=\"" + nonCjkFonts + "\"" : string.Empty);
        }

        private static bool ReplaceTextFont(TMP_Text text)
        {
            if (text == null)
            {
                return false;
            }

            TMP_FontAsset replacement = GetPrimaryFontAsset();
            if (replacement == null)
            {
                return false;
            }

            TMP_FontAsset currentFont = text.font;
            if (currentFont == null)
            {
                return false;
            }

            Material currentMaterial = text.fontSharedMaterial;

            // Refresh the baseline when the text does not currently use one of our fonts: the game or
            // another mod may have re-fonted it since the snapshot was taken. Our own asset must never
            // become a snapshot, otherwise missing glyphs would fall back to a previously applied font.
            if (!IsKnownFont(currentFont))
            {
                OriginalTmpStates[text] = CaptureTextState(currentFont, currentMaterial);
            }
            else if (!OriginalTmpStates.ContainsKey(text))
            {
                // First sighting already on our font (a clone of a replaced prefab): remember the game
                // default, so a restore has a target instead of keeping our font.
                TMP_FontAsset gameDefault = TMP_Settings.defaultFontAsset;
                if (gameDefault != null)
                {
                    OriginalTmpStates[text] = CaptureTextState(gameDefault, gameDefault.material);
                }
            }

            FontReplaceMode mode = GetEffectiveMode();
            if (mode == FontReplaceMode.ScaleAware)
            {
                TMP_FontAsset sourceFont = currentFont;
                OriginalTextState sourceState;
                if (IsKnownFont(currentFont) && OriginalTmpStates.TryGetValue(text, out sourceState))
                {
                    sourceFont = sourceState.Font;
                }

                if (sourceFont != null && !IsKnownFont(sourceFont))
                {
                    TMP_FontAsset scaled = GetProfile(sourceFont, false);
                    if (scaled != null)
                    {
                        replacement = scaled;
                    }
                }
            }

            bool replaceMaterial =
                currentMaterial == null ||
                IsOurMaterial(currentMaterial, currentFont) ||
                !PreserveCustomMaterials.Value ||
                mode == FontReplaceMode.Aggressive ||
                mode == FontReplaceMode.ScaleAware;

            bool changed = false;

            if (currentFont != replacement)
            {
                text.font = replacement;
                changed = true;
            }

            if (replaceMaterial && text.fontSharedMaterial != replacement.material)
            {
                text.fontSharedMaterial = replacement.material;
                changed = true;
            }

            if (changed)
            {
                text.SetAllDirty();
            }

            return changed;
        }

        /// <summary>
        /// True when the material belongs to the given font or to any font this plugin created, so a
        /// text left carrying a previous replacement's material is repaired instead of mixing two fonts.
        /// </summary>
        private static bool IsOurMaterial(Material material, TMP_FontAsset currentFont)
        {
            if (material == null)
            {
                return false;
            }

            if (currentFont != null && material == currentFont.material)
            {
                return true;
            }

            foreach (TMP_FontAsset known in KnownFonts)
            {
                if (known != null && known.material == material)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Picks the primary font. SystemFontPriority selects the source: with it on only the
        /// configured system font is used, with it off only the configured local font file. Nothing
        /// selected means nothing is applied — the plugin stays idle instead of silently picking the
        /// first file, and the game's own fonts take over. A selection whose file is gone is cleared;
        /// a file that exists but fails to load is kept, because that failure is usually temporary
        /// (file locked, download in progress).
        /// </summary>
        private static void ResolvePrimaryFont()
        {
            PrimaryFontAsset = null;
            PrimaryUnityFont = null;

            bool systemPreferred = SystemFontPriority.Value;
            string wanted = systemPreferred ? SystemFont.Value : FontFile.Value;
            if (string.IsNullOrEmpty(wanted))
            {
                return;
            }

            string path = systemPreferred ? FindSystemFontPath(wanted) : FindLocalFontPath(wanted);
            if (path == null)
            {
                ClearMissingFontSelection(wanted, systemPreferred);
                return;
            }

            int index = EnsureFontAsset(path);
            if (index >= 0)
            {
                SetPrimaryFont(index);
                return;
            }

            if (FailedFontPaths.Add(path))
            {
                Log.LogWarning("Font '" + wanted + "' could not be loaded; the selection is kept and " +
                    "retried after a new selection or a font asset rebuild.");
            }
        }

        /// <summary>
        /// Drops a selection whose file can no longer be found (deleted, font uninstalled) so the next
        /// start does not retry it; the cfg entry is rewritten, not just ignored. A file that exists but
        /// fails to load is kept instead, because that failure is usually temporary — see ResolvePrimaryFont.
        /// Queues a re-apply so the texts fall back to the game fonts right away.
        /// </summary>
        private static void ClearMissingFontSelection(string wanted, bool systemPreferred)
        {
            ReleaseExternalFallbacks();

            if (systemPreferred)
            {
                SystemFont.Value = string.Empty;
                SaveConfig();
                Log.LogWarning("System font '" + wanted + "' was not found; SystemFont cleared.");
            }
            else
            {
                FontFile.Value = string.Empty;
                SaveConfig();
                Log.LogWarning("Font file '" + wanted + "' was not found in " + FontsDir +
                    "; FontFile cleared.");
            }

            RequestTextRefresh();
        }

        private static void SetPrimaryFont(int index)
        {
            TMP_FontAsset next = LoadedFonts[index];

            // Do not destroy profiles here: the setting row that triggered this switch may still
            // be rendering a label that falls back to one of them. Profiles are rebuilt on the
            // next scan after the primary asset changes; keeping the old atlas for one frame is
            // safer than a visible gap in the settings menu.
            PrimaryFontAsset = next;
            PrimaryUnityFont = next != null && next.sourceFontFile != null
                ? next.sourceFontFile
                : index < LoadedUnityFonts.Count ? LoadedUnityFonts[index] : null;
        }

        /// <summary>Local font files live in FontsDir; everything else is a system font.</summary>
        private static string FindLocalFontPath(string fileName)
        {
            List<string> paths = ScanFontFiles(FontsDir);
            for (int i = 0; i < paths.Count; i++)
            {
                if (string.Equals(Path.GetFileName(paths[i]), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return paths[i];
                }
            }

            return null;
        }

        private static string FindSystemFontPath(string name)
        {
            List<string> paths = ScanSystemFonts();
            for (int i = 0; i < paths.Count; i++)
            {
                if (NameMatches(paths[i], name))
                {
                    return paths[i];
                }
            }

            return null;
        }

        /// <summary>Matches a font file either by full file name or by name without extension.</summary>
        private static bool NameMatches(string path, string wanted)
        {
            return string.Equals(Path.GetFileName(path), wanted, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Path.GetFileNameWithoutExtension(path), wanted, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Index of the cached asset for this file at this sampling size, or -1. Caching per size is what
        /// makes alternating sampling sizes reuse the already built atlases instead of stacking new ones.
        /// </summary>
        private static int IndexOfLoadedFont(string path, int samplingSize)
        {
            for (int i = 0; i < LoadedFontPaths.Count; i++)
            {
                if (LoadedFontSizes[i] == samplingSize &&
                    string.Equals(LoadedFontPaths[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Loads the file when needed and returns its index in LoadedFonts, -1 on failure. A file that
        /// already failed is not retried until the attempts are reset, so a locked download cannot make
        /// every scan pass re-parse it.
        /// </summary>
        private static int EnsureFontAsset(string path)
        {
            if (FailedFontPaths.Contains(path))
            {
                return -1;
            }

            int index = IndexOfLoadedFont(path, CurrentSamplingSize());
            if (index >= 0)
            {
                // Reuse counts as a use for the LRU stamp, so an active size is never evicted first.
                LoadedFontStamps[index] = ++s_fontUseCounter;
                return index;
            }

            return LoadFont(path) != null ? LoadedFonts.Count - 1 : -1;
        }

        /// <summary>True when a font is currently applied; the panel uses it to decide on a re-apply.</summary>
        internal static bool HasPrimaryFont()
        {
            return PrimaryFontAsset != null;
        }

        /// <summary>
        /// Clears the local font choice (the panel's "not selected" entry) and queues a re-apply, which
        /// reverts every replaced text to the game's own font when nothing else is selected. Returns
        /// true when nothing is left to apply, so the caller can rebuild its UI from prefabs.
        /// </summary>
        internal static bool ClearPluginFontSelection()
        {
            return ClearFontSelection(false);
        }

        /// <summary>Same as ClearPluginFontSelection for the system font (see SystemFontPriority).</summary>
        internal static bool ClearSystemFontSelection()
        {
            return ClearFontSelection(true);
        }

        private static bool ClearFontSelection(bool systemFont)
        {
            ConfigEntry<string> selection = systemFont ? SystemFont : FontFile;
            if (!string.IsNullOrEmpty(selection.Value))
            {
                selection.Value = string.Empty;
                SaveConfig();
            }

            ResolvePrimaryFont();
            if (PrimaryFontAsset == null)
            {
                // Nothing selected: our fonts must not stay behind as fallbacks either.
                ReleaseExternalFallbacks();
            }

            // Deferred: this runs from a settings-row callback, see RequestTextRefresh.
            RequestTextRefresh();
            if (PrimaryFontAsset == null)
            {
                RestoreNativeMetrics();

                // Regenerate every text before the profiles are released: a layout that still draws
                // from one would otherwise render a frame against a destroyed atlas.
                MarkSeenTextsDirty(Resources.FindObjectsOfTypeAll<TMP_Text>(), true);
                ClearFontProfiles();
            }

            Log.LogInfo((systemFont ? "System font" : "Font file") + " selection cleared (" +
                DescribePrimaryFont() + ").");
            return PrimaryFontAsset == null;
        }

        /// <summary>True when the asset was created by this plugin, including pre-rebuild assets.</summary>
        internal static bool IsKnownFont(TMP_FontAsset font)
        {
            return font != null && KnownFonts.Contains(font);
        }

        /// <summary>
        /// The plugin's font assets, for the glyph guard. Iterate only: the guard never adds or
        /// removes entries while walking it.
        /// </summary>
        internal static IEnumerable<TMP_FontAsset> KnownFontAssets
        {
            get { return KnownFonts; }
        }

        /// <summary>
        /// True when the component belongs to a live scene object. Resources.FindObjectsOfTypeAll also
        /// returns prefab assets and other loaded assets: re-fonting one of those bakes the plugin font
        /// into every later instance, and such a clone never has a snapshot that a restore could use, so
        /// it would keep the plugin font for good (e.g. freshly spawned settings rows).
        /// </summary>
        private static bool IsLiveSceneObject(Component component)
        {
            if (component == null)
            {
                return false;
            }

            GameObject owner = component.gameObject;
            return owner != null && owner.scene.IsValid();
        }

        /// <summary>
        /// Returns the original asset, or looks it up by name when it has been unloaded since the
        /// snapshot was taken. Returns null when it is gone for good. Results are memoized because a
        /// single restore pass may resolve the same name for many texts.
        /// </summary>
        private static TMP_FontAsset ResolveFontAsset(OriginalTextState state,
            Dictionary<string, TMP_FontAsset> cache)
        {
            TMP_FontAsset found = ResolveFontAsset(state.FontName, state.Font, cache);
            if (found != null && !KnownFonts.Contains(found))
            {
                return found;
            }

            if (string.IsNullOrEmpty(state.FamilyName))
            {
                return null;
            }

            string key = "family|" + state.FamilyName + "|" + state.PointSize;
            TMP_FontAsset cached;
            if (cache != null && cache.TryGetValue(key, out cached))
            {
                return cached;
            }

            TMP_FontAsset best = null;
            TMP_FontAsset[] assets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            for (int i = 0; i < assets.Length; i++)
            {
                TMP_FontAsset asset = assets[i];
                if (asset == null || KnownFonts.Contains(asset))
                {
                    continue;
                }

                if (!string.Equals(asset.name, state.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                    !asset.name.StartsWith(state.FamilyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (state.PointSize > 0 && asset.faceInfo.pointSize > 0 &&
                    Mathf.Abs(asset.faceInfo.pointSize - state.PointSize) > 0.1f)
                {
                    continue;
                }

                best = asset;
                if (string.Equals(asset.name, state.FamilyName, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            if (cache != null)
            {
                cache[key] = best;
            }

            return best;
        }

        private static TMP_FontAsset ResolveFontAsset(string name, TMP_FontAsset reference,
            Dictionary<string, TMP_FontAsset> cache)
        {
            if (reference != null)
            {
                return reference;
            }

            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            TMP_FontAsset cached;
            if (cache != null && cache.TryGetValue(name, out cached))
            {
                return cached;
            }

            TMP_FontAsset found = null;
            TMP_FontAsset[] assets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            for (int i = 0; i < assets.Length; i++)
            {
                TMP_FontAsset asset = assets[i];
                if (asset != null && !KnownFonts.Contains(asset) &&
                    string.Equals(asset.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    found = asset;
                    break;
                }
            }

            if (cache != null)
            {
                cache[name] = found;
            }

            return found;
        }

        // ---- In-game panel support; all state stays in this class (see FontSettingsPanel) ----

        internal static int GetPanelModeIndex()
        {
            FontReplaceMode mode = GetEffectiveMode();
            int index = Array.IndexOf(PanelModes, mode);
            return index >= 0 ? index : Array.IndexOf(PanelModes, FontReplaceMode.Normal);
        }

        internal static bool ApplyPanelMode(int index)
        {
            if (index < 0 || index >= PanelModes.Length)
            {
                return false;
            }

            FontReplaceMode mode = PanelModes[index];
            if (ReplaceMode.Value != mode)
            {
                ReplaceMode.Value = mode;
                SaveConfig();
            }

            // Re-resolve first: a font file deleted at runtime must not be re-applied from memory.
            // The queued pass reverts the screen itself when the new mode is FallbackOnly.
            ResolvePrimaryFont();
            if (!IsPreserveNonCjkEnabled)
            {
                // Leaving PreserveNonCjk must not leave the plugin fonts on the reference font's
                // vertical metrics.
                RestoreNativeMetrics();
            }

            RequestTextRefresh();
            Log.LogInfo("ReplaceMode = " + mode + " (from panel)");
            return true;
        }

        internal static void SaveConfig()
        {
            try
            {
                if (ConfigFile != null)
                {
                    ConfigFile.Save();
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("Config save failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Undoes every font and material change made by this plugin.
        ///
        /// Snapshots are kept: a later replacement must never record our own font as the original,
        /// otherwise one failed restore makes every following restore a no-op. Texts that no longer
        /// carry one of our fonts refresh their snapshot instead of being reverted to a stale font.
        /// </summary>
        private static void RestoreOriginalFonts()
        {
            RestoreAllCjkOverrides();
            UnhookFontFallbacks();

            int restored = 0;
            int unresolved = 0;
            Dictionary<string, TMP_FontAsset> resolved =
                new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);

            List<TMP_Text> textKeys = new List<TMP_Text>(OriginalTmpStates.Keys);
            for (int i = 0; i < textKeys.Count; i++)
            {
                TMP_Text text = textKeys[i];
                if (text == null)
                {
                    continue;
                }

                OriginalTextState state = OriginalTmpStates[text];
                if (!IsKnownFont(text.font))
                {
                    if (text.font != null)
                    {
                        OriginalTmpStates[text] = CaptureTextState(text.font, text.fontSharedMaterial);
                    }

                    continue;
                }

                TMP_FontAsset original = ResolveFontAsset(state, resolved);
                Material material = state.Material;
                if (original == null || KnownFonts.Contains(original))
                {
                    // Gone, or a poisoned snapshot holding one of our fonts: use a game font stand-in
                    // instead of leaving the text on the plugin font.
                    original = ResolveFallbackNativeFont();
                    material = original != null ? original.material : null;
                }

                if (original == null)
                {
                    unresolved++;
                    continue;
                }

                text.font = original;
                if (material != null)
                {
                    text.fontSharedMaterial = material;
                }

                text.SetAllDirty();
                restored++;
            }

            int legacyRestored = 0;
            List<UnityEngine.UI.Text> legacyKeys = new List<UnityEngine.UI.Text>(OriginalLegacyFonts.Keys);
            for (int i = 0; i < legacyKeys.Count; i++)
            {
                UnityEngine.UI.Text text = legacyKeys[i];
                if (text == null)
                {
                    continue;
                }

                Font original = OriginalLegacyFonts[text];
                if (!KnownUnityFonts.Contains(text.font))
                {
                    if (text.font != null)
                    {
                        OriginalLegacyFonts[text] = text.font;
                    }

                    continue;
                }

                if (original != null)
                {
                    text.font = original;
                    legacyRestored++;
                }
            }

            // Labels that were already carrying our font when they were first seen (a clone of a prefab
            // that was re-fonted earlier) have no snapshot. Put them back on the game font most of the
            // other legacy labels use, or on Unity's built-in font as a last resort.
            int legacySwept = 0;
            int legacyUnresolved = 0;
            Font legacyFallback = ResolveLegacyFallbackFont();
            UnityEngine.UI.Text[] legacyTexts = Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>();
            for (int i = 0; i < legacyTexts.Length; i++)
            {
                UnityEngine.UI.Text text = legacyTexts[i];
                if (text == null || !IsLiveSceneObject(text) || !KnownUnityFonts.Contains(text.font) ||
                    OriginalLegacyFonts.ContainsKey(text))
                {
                    continue;
                }

                if (legacyFallback == null)
                {
                    legacyUnresolved++;
                    continue;
                }

                text.font = legacyFallback;
                text.SetAllDirty();
                legacySwept++;
            }

            if (legacyUnresolved > 0)
            {
                Log.LogWarning("Could not restore " + legacyUnresolved +
                    " legacy label(s): no game font is available as a fallback, they keep the plugin font.");
            }

            // Input fields are found by scanning, not by the snapshot keys: a field that was already
            // carrying one of our fonts when it was first seen has no snapshot of its own.
            int inputRestored = 0;
            TMP_InputField[] inputFields = Resources.FindObjectsOfTypeAll<TMP_InputField>();
            for (int i = 0; i < inputFields.Length; i++)
            {
                TMP_InputField inputField = inputFields[i];
                if (inputField == null || !IsLiveSceneObject(inputField) || !IsKnownFont(inputField.fontAsset))
                {
                    continue;
                }

                TMP_FontAsset original;
                if (!OriginalInputFonts.TryGetValue(inputField, out original) || original == null ||
                    IsKnownFont(original))
                {
                    // No usable snapshot: use a game font stand-in instead of leaving ours.
                    original = ResolveFallbackNativeFont();
                }

                if (original != null)
                {
                    inputField.fontAsset = original;
                    inputRestored++;
                }
                else
                {
                    unresolved++;
                }
            }

            // Instances of a prefab that was replaced earlier carry our font without a snapshot entry.
            // Put them back on a game font so disabling really removes every trace of the plugin.
            int swept = 0;
            TMP_FontAsset gameDefault = ResolveFallbackNativeFont();
            if (gameDefault != null)
            {
                TMP_Text[] loadedTexts = Resources.FindObjectsOfTypeAll<TMP_Text>();
                for (int i = 0; i < loadedTexts.Length; i++)
                {
                    TMP_Text text = loadedTexts[i];
                    if (text == null || !IsLiveSceneObject(text) || !IsKnownFont(text.font) ||
                        OriginalTmpStates.ContainsKey(text))
                    {
                        continue;
                    }

                    text.font = gameDefault;
                    if (gameDefault.material != null)
                    {
                        text.fontSharedMaterial = gameDefault.material;
                    }

                    text.SetAllDirty();
                    swept++;
                }
            }

            // With no primary left, the CJK glyphs already resolved in textInfo would otherwise keep
            // the old plugin font until their content changes.
            s_lastLayoutPrimary = null;
            MarkSeenTextsDirty(Resources.FindObjectsOfTypeAll<TMP_Text>());

            Log.LogInfo("Original fonts restored: " + restored + " TMP text(s), " + legacyRestored +
                " legacy text(s), " + inputRestored + " input field(s)" +
                (legacySwept > 0
                    ? ", " + legacySwept + " legacy label(s) moved to '" + legacyFallback.name + "'"
                    : string.Empty) +
                (swept > 0 ? ", " + swept + " untracked text(s) moved to a game font" : string.Empty) +
                (unresolved > 0
                    ? "; " + unresolved + " entry(ies) could not be resolved to a game font and were left as they are"
                    : string.Empty) + ".");
        }

        /// <summary>
        /// Best guess for a legacy label whose original font was never snapshotted: the font most other
        /// legacy labels use, otherwise Unity's built-in font. Null when neither can be found, in which
        /// case the label is left alone rather than being moved to an arbitrary font.
        /// </summary>
        private static Font ResolveLegacyFallbackFont()
        {
            UnityEngine.UI.Text[] texts = Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>();
            Dictionary<Font, int> counts = new Dictionary<Font, int>();
            for (int i = 0; i < texts.Length; i++)
            {
                UnityEngine.UI.Text text = texts[i];
                if (text == null || text.font == null || KnownUnityFonts.Contains(text.font))
                {
                    continue;
                }

                int count;
                counts.TryGetValue(text.font, out count);
                counts[text.font] = count + 1;
            }

            Font best = null;
            int bestCount = 0;
            foreach (KeyValuePair<Font, int> pair in counts)
            {
                Font font = pair.Key;
                if (font == null)
                {
                    continue;
                }

                int count = pair.Value;
                if (count > bestCount ||
                    (count == bestCount && best != null && string.CompareOrdinal(font.name, best.name) < 0))
                {
                    best = font;
                    bestCount = count;
                }
            }

            if (best != null)
            {
                return best;
            }

            // Labels still carrying the plugin font do not count above, so on the pass that switches
            // PreserveNonCjk on there may be no live candidate at all. The snapshots of the labels that
            // were replaced earlier still name a game font, which is the right stand-in.
            Dictionary<Font, int> snapshotCounts = new Dictionary<Font, int>();
            foreach (Font snapshotted in OriginalLegacyFonts.Values)
            {
                if (snapshotted == null || KnownUnityFonts.Contains(snapshotted))
                {
                    continue;
                }

                int count;
                snapshotCounts.TryGetValue(snapshotted, out count);
                snapshotCounts[snapshotted] = count + 1;
            }

            foreach (KeyValuePair<Font, int> pair in snapshotCounts)
            {
                if (pair.Key == null)
                {
                    continue;
                }

                if (pair.Value > bestCount ||
                    (pair.Value == bestCount && best != null && string.CompareOrdinal(pair.Key.name, best.name) < 0))
                {
                    best = pair.Key;
                    bestCount = pair.Value;
                }
            }

            if (best != null)
            {
                return best;
            }

            // Unity 2022 ships LegacyRuntime.ttf; older versions exposed Arial.ttf. Neither is guaranteed
            // to carry CJK, so this is only the last resort for labels with no snapshot at all.
            string[] builtinNames = { "LegacyRuntime.ttf", "Arial.ttf" };
            for (int i = 0; i < builtinNames.Length; i++)
            {
                try
                {
                    Font builtin = Resources.GetBuiltinResource<Font>(builtinNames[i]);
                    if (builtin != null && !KnownUnityFonts.Contains(builtin))
                    {
                        return builtin;
                    }
                }
                catch (Exception)
                {
                    // Try the next built-in name.
                }
            }

            return null;
        }

        /// <summary>
        /// Re-evaluates every loaded text, e.g. after a mode or font change. With no font left to apply
        /// (selected file deleted, nothing loaded) the game's fonts are restored instead of leaving the
        /// last replacement on screen. Also consumes a queued RequestTextRefresh.
        /// </summary>
        internal static void RefreshAllTexts()
        {
            // This pass satisfies a queued refresh.
            s_textRefreshPending = false;

            // Profiles built from the previous primary carry its metrics and their own atlas, so they
            // can never be reused. Release them before the pass; a font or sampling-size switch would
            // otherwise keep one abandoned 4096² atlas per profile until the LRU cap evicts it.
            ReleaseStaleProfiles();

            s_preserveTexts = null;
            s_preserveIndex = 0;
            s_preserveRestoreCache = null;
            s_preserveStillOurs = 0;
            s_preserveNextCycleTime = 0f;
            s_nextLegacyPreserveScanTime = 0f;

            if (!IsCjkOverrideActive)
            {
                RestoreAllCjkOverrides();
            }

            // Every refresh re-hooks from scratch, so a font switch can never leave the previous asset
            // referenced by a game font's chain (it must stay collectible by the cache trim below).
            UnhookFontFallbacks();

            ResetSeenState();
            if (!IsReplacementEnabled())
            {
                TrimFontCache();
                return;
            }

            if (GetPrimaryFontAsset() == null)
            {
                // No font selected: the plugin font must not linger as a fallback either, and every
                // text has to regenerate. ReleaseStaleProfiles has just released the profiles; a
                // layout that still pointed at one would keep drawing from a destroyed atlas
                // instead of falling back to the game fonts, which looks like missing text.
                ReleaseExternalFallbacks();
                RestoreOriginalFonts();
                MarkSeenTextsDirty(Resources.FindObjectsOfTypeAll<TMP_Text>(), true);
                TrimFontCache();
                return;
            }

            // FallbackOnly leaves no replacement on screen: texts, legacy labels and input fields all
            // go back to the game fonts, while the selected font stays registered as a fallback.
            if (GetEffectiveMode() == FontReplaceMode.FallbackOnly)
            {
                RestoreOriginalFonts();
            }

            ApplyToLoadedText(true);
            ApplyToLoadedLegacyText(true);

            // Every text is now on the current font or back on a game font, so the oldest unused
            // sampling-size caches can be released safely.
            TrimFontCache();
        }

        /// <summary>
        /// Hands non-CJK characters back to the game font by dropping them from the plugin fonts'
        /// character tables. The atlas and its glyphs stay: the guard blocks new adds, and turning
        /// the option off restores the entries from those glyphs without re-rasterising anything.
        /// Runs at most once per asset until something invalidates the purity flag.
        /// </summary>
        private static int PurgeNonCjkGlyphs()
        {
            int removed = NativeGlyphGuard.PurgeAll();
            if (removed > 0)
            {
                Log.LogInfo("[Native] stripped " + removed +
                    " non-CJK glyph(s) from the plugin font(s); those characters now use the game font.");
            }

            return removed;
        }

        /// <summary>
        /// Copies the vertical proportions of a game font onto every plugin font. TMP computes a
        /// character's ascender/descender from its own font and takes the line's maximum, so a line
        /// mixing the plugin font's CJK with game-font digits would be stretched to the larger of the
        /// two. The reference is the font that renders the digits: in PreserveNonCjk mode the game font
        /// most CJK texts use (per text), otherwise the first native entry of the fallback chain.
        /// Returns how many fonts were adjusted.
        /// </summary>
        private static int EnsureNativeMetricsForAll()
        {
            if (!IsPreserveNonCjkEnabled || AlignNativeMetrics == null || !AlignNativeMetrics.Value)
            {
                return 0;
            }

            TMP_FontAsset reference = ResolveMetricsReference();
            if (reference == null)
            {
                return 0;
            }

            // The first fallback entry can change when a new game font is used for the first time. The
            // metrics must follow it, otherwise the line maximum would be taken from another font again.
            if (reference != s_metricsReference)
            {
                if (s_metricsReference != null && NativeMetricsAligned.Count > 0)
                {
                    Log.LogInfo(IsPreserveNonCjkEnabled
                        ? "[Native] line metrics reference is now '" + reference.name + "'; re-aligning."
                        : "[Native] non-CJK glyphs now come from '" + reference.name + "'; re-aligning line metrics.");
                }

                s_metricsReference = reference;
                NativeMetricsAligned.Clear();
            }

            int applied = 0;
            foreach (TMP_FontAsset font in KnownFonts)
            {
                if (IsFontProfile(font))
                {
                    // Profiles already carry their source font's scale and metrics; aligning them
                    // to the global reference would undo that and re-introduce the mismatch.
                    continue;
                }

                if (EnsureNativeMetrics(font, reference))
                {
                    applied++;
                }
            }

            return applied;
        }

        private static bool EnsureNativeMetrics(TMP_FontAsset font, TMP_FontAsset reference)
        {
            if (font == null || reference == null || font == reference ||
                !NativeMetricsAligned.Add(font))
            {
                return false;
            }

            FaceInfo source = reference.faceInfo;
            FaceInfo current = font.faceInfo;
            if (source.pointSize <= 0 || current.pointSize <= 0 || source.lineHeight <= 0f ||
                source.ascentLine <= 0f)
            {
                NativeMetricsAligned.Remove(font);
                return false;
            }

            float ratio = (float)current.pointSize / source.pointSize;
            if (!OriginalFaceInfo.ContainsKey(font))
            {
                OriginalFaceInfo[font] = current;
            }

            // TMP scales every glyph by fontSize / pointSize * scale. The old code copied the
            // source's vertical metrics but left the plugin font's scale at 1, so a source font
            // with scale 1.71 was still drawn 71% larger than the fallback. Copy the scale as
            // well; the metrics above are already expressed in the plugin font's point size.
            float sourceScale = source.scale > 0f ? source.scale : 1f;

            FaceInfo aligned = current;
            aligned.scale = sourceScale;
            aligned.lineHeight = source.lineHeight * ratio;
            aligned.ascentLine = source.ascentLine * ratio;
            aligned.descentLine = source.descentLine * ratio;
            aligned.baseline = source.baseline * ratio;
            aligned.capLine = source.capLine * ratio;
            aligned.meanLine = source.meanLine * ratio;
            aligned.underlineOffset = source.underlineOffset * ratio;
            aligned.strikethroughOffset = source.strikethroughOffset * ratio;
            font.faceInfo = aligned;

            FaceInfo applied = font.faceInfo;
            if (Mathf.Abs(applied.lineHeight - aligned.lineHeight) > 0.01f)
            {
                Log.LogWarning("[Native] could not apply line metrics to '" + font.name +
                    "'; the font keeps its own line spacing.");
            }
            else
            {
                float sourceCap = CapFactorOf(source);
                float appliedCap = CapFactorOf(applied);
                string detail = "scale " + applied.scale.ToString("0.###") +
                    " (source " + sourceScale.ToString("0.###") + ")" +
                    ", cap " + appliedCap.ToString("0.###") + " vs " + sourceCap.ToString("0.###");

                Log.LogInfo("[Native] line metrics of '" + font.name + "' aligned to '" + reference.name +
                    "' (x" + ratio.ToString("0.###") + "; " + detail + "); " +
                    (IsPreserveNonCjkEnabled
                        ? "mixed lines follow that font's line spacing."
                        : "mixed lines keep the game's line spacing."));

                // A few percent of difference is normal because the glyph outlines differ; a
                // large gap means the FaceInfo copy and TMP's per-glyph scale disagree again.
                if (sourceCap > 0.01f && Mathf.Abs(appliedCap / sourceCap - 1f) > 0.02f)
                {
                    Log.LogWarning("[Native] cap factor mismatch after aligning '" + font.name +
                        "' to '" + reference.name + "': " + appliedCap.ToString("0.###") +
                        " vs " + sourceCap.ToString("0.###") +
                        " (TMP will draw fallback glyphs at a different size).");
                }
            }

            return true;
        }

        /// <summary>
        /// Cap height per fontSize unit for a FaceInfo, using the same terms TMP uses when it draws
        /// a character: (capLine - baseline) / pointSize * scale. Used by the metrics diagnostics
        /// and by the special-case profile validation.
        /// </summary>
        internal static float CapFactorOf(FaceInfo face)
        {
            if (face.pointSize <= 0)
            {
                return 0f;
            }

            float scale = face.scale > 0f ? face.scale : 1f;
            return (face.capLine - face.baseline) / face.pointSize * scale;
        }

        /// <summary>
        /// Finds or builds the metric variant of the primary font for one source font. The profile
        /// carries the source's scale and (when AlignNativeMetrics is on) its vertical metrics, so a
        /// CJK fallback glyph is drawn at the same size and baseline as the source's own glyphs.
        /// </summary>
        private static TMP_FontAsset GetProfile(TMP_FontAsset source, bool strict, bool force = false)
        {
            if (source == null || IsKnownFont(source))
            {
                return null;
            }

            // Once this source failed to get a profile at the cap, do not retry the expensive
            // reference scan for every text and every scan. The set is cleared at the start of a
            // full pass and when profiles are released.
            if (!force && ProfileLimitBlocked.Contains(source))
            {
                return null;
            }

            if (ProfileCreationFailed.Contains(source))
            {
                return null;
            }

            FontProfileEntry existing = null;
            for (int i = 0; i < FontProfiles.Count; i++)
            {
                FontProfileEntry entry = FontProfiles[i];
                if (entry != null && entry.Source == source && entry.Primary == PrimaryFontAsset &&
                    entry.Strict == strict)
                {
                    existing = entry;
                    break;
                }
            }

            if (existing != null)
            {
                if (ProfileMatches(existing, source, strict))
                {
                    existing.Stamp = ++s_profileUseCounter;
                    return existing.Profile;
                }

                DestroyProfile(existing);
            }

            int limit = ProfileLimit();
            if (limit > 0 && FontProfiles.Count >= limit)
            {
                TrimFontProfiles(limit - 1);
                if (FontProfiles.Count >= limit && !force)
                {
                    ProfileLimitBlocked.Add(source);
                    if (ProfileLimitLogged.Add(source))
                    {
                        Log.LogWarning("[Metrics] profile limit reached (" + limit +
                            "); '" + source.name + "' keeps the primary font, CJK size may differ.");
                    }
                    return null;
                }

                if (FontProfiles.Count >= limit)
                {
                    // A whitelisted special case must not silently lose its CJK-only profile just
                    // because ordinary source fonts filled the cache. One extra profile is bounded
                    // and is released as soon as the takeover state goes away.
                    Log.LogInfo("[Metrics] profile limit (" + limit + ") exceeded for special font '" +
                        source.name + "'; keeping its profile until the takeover is released.");
                }
            }

            FaceInfo baseFace;
            TMP_FontAsset created = CreateProfile(source, strict, out baseFace);
            if (created == null)
            {
                ProfileCreationFailed.Add(source);
                return null;
            }

            FontProfileEntry added = new FontProfileEntry();
            added.Source = source;
            added.Primary = PrimaryFontAsset;
            added.Profile = created;
            added.Strict = strict;
            added.BaseFaceInfo = baseFace;
            added.SourcePointSize = source.faceInfo.pointSize;
            added.SourceScale = source.faceInfo.scale;
            added.SourceLineHeight = source.faceInfo.lineHeight;
            added.SourceCapLine = source.faceInfo.capLine;
            added.SourceAscentLine = source.faceInfo.ascentLine;
            added.SourceDescentLine = source.faceInfo.descentLine;
            added.Stamp = ++s_profileUseCounter;
            FontProfiles.Add(added);
            return created;
        }

        private static bool ProfileMatches(FontProfileEntry entry, TMP_FontAsset source, bool strict)
        {
            if (entry == null || entry.Profile == null || entry.Source != source ||
                entry.Primary != PrimaryFontAsset || entry.Strict != strict)
            {
                return false;
            }

            FaceInfo face = source.faceInfo;
            return entry.SourcePointSize == face.pointSize &&
                   Mathf.Abs(entry.SourceScale - face.scale) < 0.0001f &&
                   Mathf.Abs(entry.SourceLineHeight - face.lineHeight) < 0.01f &&
                   Mathf.Abs(entry.SourceCapLine - face.capLine) < 0.01f &&
                   Mathf.Abs(entry.SourceAscentLine - face.ascentLine) < 0.01f &&
                   Mathf.Abs(entry.SourceDescentLine - face.descentLine) < 0.01f;
        }

        private static TMP_FontAsset CreateProfile(TMP_FontAsset source, bool strict,
            out FaceInfo baseFace)
        {
            baseFace = default(FaceInfo);
            if (PrimaryFontAsset == null)
            {
                return null;
            }

            // The source font stored on the primary asset is authoritative. PrimaryUnityFont is
            // indexed from a parallel cache list and can point at the previous primary after a
            // switch; building the profile from it would name the profile after the new font but
            // rasterize the old font's glyphs (which is how a Chinese font came back with no CJK).
            Font sourceFont = PrimaryFontAsset.sourceFontFile != null
                ? PrimaryFontAsset.sourceFontFile
                : PrimaryUnityFont;
            if (sourceFont == null)
            {
                return null;
            }

            TMP_FontAsset profile = null;
            try
            {
                int sampling = PrimaryFontAsset.faceInfo.pointSize > 0
                    ? PrimaryFontAsset.faceInfo.pointSize
                    : CurrentSamplingSize();
                int atlasSize = Mathf.Clamp(AtlasSize.Value, 512, 8192);
                int padding = Mathf.Clamp(AtlasPadding.Value, 1, 16);

                profile = TMP_FontAsset.CreateFontAsset(
                    sourceFont,
                    sampling,
                    padding,
                    GlyphRenderMode.SDFAA,
                    atlasSize,
                    atlasSize,
                    AtlasPopulationMode.Dynamic,
                    true);
                if (profile == null)
                {
                    Log.LogWarning("[Metrics] could not create a profile for '" + source.name + "'.");
                    return null;
                }

                baseFace = profile.faceInfo;
                profile.name = PrimaryFontAsset.name + "@" + source.name;
                profile.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                profile.isMultiAtlasTexturesEnabled = true;

                FaceInfo src = source.faceInfo;
                FaceInfo face = profile.faceInfo;
                face.pointSize = sampling;
                face.scale = src.scale > 0f ? src.scale : 1f;

                // Full ScaleAware profiles always follow the source font's metrics. Strict
                // PreserveNonCjk profiles only do so when AlignNativeMetrics is on; otherwise they
                // keep the plugin font's own line spacing.
                if ((!strict || AlignNativeMetrics.Value) && src.pointSize > 0)
                {
                    float ratio = (float)sampling / src.pointSize;
                    face.lineHeight = src.lineHeight * ratio;
                    face.ascentLine = src.ascentLine * ratio;
                    face.descentLine = src.descentLine * ratio;
                    face.baseline = src.baseline * ratio;
                    face.capLine = src.capLine * ratio;
                    face.meanLine = src.meanLine * ratio;
                    face.underlineOffset = src.underlineOffset * ratio;
                    face.strikethroughOffset = src.strikethroughOffset * ratio;
                    face.tabWidth = src.tabWidth * ratio;
                }

                profile.faceInfo = face;
                profile.fallbackFontAssetTable = BuildProfileFallback(source);

                KnownFonts.Add(profile);
                ProfileAssets.Add(profile);
                if (strict)
                {
                    NativeGlyphGuard.EnsurePure(profile);
                }

                Log.LogInfo("[Metrics] profile '" + profile.name + "': source pt=" + src.pointSize +
                    " scale=" + (src.scale > 0f ? src.scale : 1f).ToString("0.###") +
                    ", profile pt=" + sampling + " scale=" + face.scale.ToString("0.###") +
                    ", cap " + CapFactorOf(profile.faceInfo).ToString("0.###") +
                    " vs " + CapFactorOf(src).ToString("0.###") +
                    ", font='" + sourceFont.name + "'");
                return profile;
            }
            catch (Exception ex)
            {
                Log.LogWarning("[Metrics] profile for '" + source.name + "' failed: " + ex.Message);
                if (profile != null)
                {
                    try
                    {
                        KnownFonts.Remove(profile);
                        ProfileAssets.Remove(profile);
                        NativeMetricsAligned.Remove(profile);
                        OriginalFaceInfo.Remove(profile);
                        RemoveFontFromFallbacks(profile);
                    }
                    catch (Exception cleanup)
                    {
                        Log.LogWarning("[Metrics] profile cleanup for '" + source.name +
                            "' failed: " + cleanup.Message);
                    }

                    DestroyFontAssetObjects(profile, null);
                }

                return null;
            }
        }

        private static void RebuildStrictProfilesForMetrics()
        {
            List<FontProfileEntry> oldEntries = new List<FontProfileEntry>();
            for (int i = 0; i < FontProfiles.Count; i++)
            {
                FontProfileEntry entry = FontProfiles[i];
                if (entry != null && entry.Strict && entry.Profile != null && entry.Source != null)
                {
                    oldEntries.Add(entry);
                }
            }

            if (oldEntries.Count == 0)
            {
                return;
            }

            Dictionary<TMP_FontAsset, TMP_FontAsset> replacements =
                new Dictionary<TMP_FontAsset, TMP_FontAsset>();
            Dictionary<FontProfileEntry, FontProfileEntry> newEntries =
                new Dictionary<FontProfileEntry, FontProfileEntry>();
            int rebuilt = 0;
            for (int i = 0; i < oldEntries.Count; i++)
            {
                FontProfileEntry old = oldEntries[i];
                FaceInfo baseFace;
                TMP_FontAsset replacement = CreateProfile(old.Source, true, out baseFace);
                if (replacement == null)
                {
                    continue;
                }

                FontProfileEntry next = new FontProfileEntry();
                next.Source = old.Source;
                next.Primary = old.Primary;
                next.Profile = replacement;
                next.Strict = true;
                next.BaseFaceInfo = baseFace;
                next.SourcePointSize = old.SourcePointSize;
                next.SourceScale = old.SourceScale;
                next.SourceLineHeight = old.SourceLineHeight;
                next.SourceCapLine = old.SourceCapLine;
                next.SourceAscentLine = old.SourceAscentLine;
                next.SourceDescentLine = old.SourceDescentLine;
                next.Stamp = old.Stamp;
                replacements[old.Profile] = replacement;
                newEntries[old] = next;
                rebuilt++;
            }

            if (rebuilt == 0)
            {
                return;
            }

            // CjkOverride states own both the profile reference and possibly a cloned material.
            List<TMP_Text> overrideKeys = new List<TMP_Text>(CjkOverrides.Keys);
            for (int i = 0; i < overrideKeys.Count; i++)
            {
                TMP_Text text = overrideKeys[i];
                CjkOverrideState state;
                if (!CjkOverrides.TryGetValue(text, out state) || state.Profile == null)
                {
                    continue;
                }

                TMP_FontAsset oldProfile = state.Profile;
                TMP_FontAsset replacement;
                if (!replacements.TryGetValue(oldProfile, out replacement) || replacement == null)
                {
                    continue;
                }

                Material oldApplied = state.AppliedMaterial;
                Material applied = replacement.material;
                bool owns = false;
                if (state.OwnsMaterial && state.SourceMaterial != null)
                {
                    Material clone;
                    bool cloneOwns;
                    if (TryBuildCjkOverrideMaterial(state.Source, replacement, state.SourceMaterial,
                            out clone, out cloneOwns))
                    {
                        applied = clone;
                        owns = cloneOwns;
                    }
                }

                state.Profile = replacement;
                state.AppliedMaterial = applied;
                state.OwnsMaterial = owns;
                CjkOverrides[text] = state;

                if (text != null)
                {
                    if (text.font == oldProfile || text.font == null)
                    {
                        text.font = replacement;
                        text.fontSharedMaterial = applied;
                        text.SetAllDirty();
                    }
                }

                if (oldApplied != null && oldApplied != applied &&
                    oldApplied != oldProfile.material)
                {
                    Destroy(oldApplied);
                }
            }

            TMP_Text[] texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text text = texts[i];
                if (text == null || text.font == null)
                {
                    continue;
                }

                TMP_FontAsset replacement;
                if (replacements.TryGetValue(text.font, out replacement) && replacement != null)
                {
                    text.font = replacement;
                    text.fontSharedMaterial = replacement.material;
                    text.SetAllDirty();
                }
            }

            // Replace profile references in every fallback table before the old assets are destroyed.
            TMP_FontAsset[] assets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            for (int i = 0; i < assets.Length; i++)
            {
                if (assets[i] != null)
                {
                    ReplaceProfileReferences(assets[i].fallbackFontAssetTable, replacements);
                }
            }

            try
            {
                ReplaceProfileReferences(TMP_Settings.fallbackFontAssets, replacements);
            }
            catch (Exception)
            {
                // TMP settings may not be readable yet; the per-font tables are the important path.
            }

            List<FontProfileEntry> merged = new List<FontProfileEntry>(FontProfiles.Count);
            for (int i = 0; i < FontProfiles.Count; i++)
            {
                FontProfileEntry entry = FontProfiles[i];
                FontProfileEntry next;
                if (entry != null && newEntries.TryGetValue(entry, out next))
                {
                    merged.Add(next);
                }
                else
                {
                    merged.Add(entry);
                }
            }

            FontProfiles.Clear();
            FontProfiles.AddRange(merged);

            for (int i = 0; i < oldEntries.Count; i++)
            {
                if (newEntries.ContainsKey(oldEntries[i]))
                {
                    DestroyProfile(oldEntries[i]);
                }
            }

            ProfileLimitBlocked.Clear();
            ProfileLimitLogged.Clear();
            Log.LogInfo("[Metrics] rebuilt " + rebuilt + " strict profile(s) for the new line metrics.");
        }

        private static void ReplaceProfileReferences(List<TMP_FontAsset> table,
            Dictionary<TMP_FontAsset, TMP_FontAsset> replacements)
        {
            if (table == null)
            {
                return;
            }

            for (int i = 0; i < table.Count; i++)
            {
                TMP_FontAsset replacement;
                if (table[i] != null && replacements.TryGetValue(table[i], out replacement) &&
                    replacement != null)
                {
                    table[i] = replacement;
                }
            }
        }

        private static List<TMP_FontAsset> BuildProfileFallback(TMP_FontAsset source)
        {
            List<TMP_FontAsset> chain = new List<TMP_FontAsset>();
            if (source != null && !IsKnownFont(source))
            {
                chain.Add(source);
            }

            List<TMP_FontAsset> sourceChain = source != null ? source.fallbackFontAssetTable : null;
            if (sourceChain != null)
            {
                for (int i = 0; i < sourceChain.Count; i++)
                {
                    TMP_FontAsset entry = sourceChain[i];
                    if (entry != null && entry != source && !IsKnownFont(entry) && !chain.Contains(entry))
                    {
                        chain.Add(entry);
                    }
                }
            }

            return chain;
        }

        /// <summary>
        /// Special case for whitelisted fonts that already contain CJK (ScienceUnifont, ConsoleFont):
        /// TMP checks the text's own font first, so a fallback can never replace those glyphs. The
        /// text is switched to a CJK-only profile and the source becomes its fallback, which keeps
        /// digits, Latin and symbols on the source. Returns true when this text is handled here.
        /// </summary>
        private static bool TryApplyCjkOverride(TMP_Text text, FontReplaceMode mode)
        {
            if (text == null)
            {
                return false;
            }

            CjkOverrideState state;
            bool hasState = CjkOverrides.TryGetValue(text, out state);

            if (!IsCjkOverrideActive)
            {
                if (hasState)
                {
                    RestoreCjkOverride(text);
                }
                return false;
            }

            if (!TextHasCjk(text))
            {
                // Talker / terminal labels are frequently cleared and refilled character by
                // character. Restoring on the empty intermediate state would drop the profile
                // before the CJK text arrives. Keep the takeover for an empty label; a non-empty
                // label without CJK is genuinely not a CJK label and is restored.
                if (!string.IsNullOrEmpty(text.text))
                {
                    if (hasState)
                    {
                        RestoreCjkOverride(text);
                    }
                    return false;
                }
            }

            if (hasState && (state.Source == null || !IsCjkOverrideFont(state.Source)))
            {
                RestoreCjkOverride(text);
                return false;
            }

            if (hasState && !CjkOverrideAllowed(text, state.Source, mode, state.SourceMaterial))
            {
                RestoreCjkOverride(text);
                return false;
            }

            if (hasState)
            {
                if (state.Profile != null && text.font == state.Profile)
                {
                    if (IsCurrentFontProfile(state.Profile))
                    {
                        if (state.AppliedMaterial != null && text.fontSharedMaterial != state.AppliedMaterial)
                        {
                            text.fontSharedMaterial = state.AppliedMaterial;
                        }
                        state.Mode = mode;
                        return true;
                    }

                    // The primary changed: this profile is stale. Restore the source and rebuild.
                    RestoreCjkOverride(text);
                }
                else
                {
                    // The game put a font back after the takeover. This is an explicit opt-in
                    // special case, so reassert it for every mode; waiting for another scan would
                    // leave ScienceUnifont text looking untranslated. A non-whitelisted font is
                    // still left alone below.
                    RestoreCjkOverride(text);
                }
            }

            // A text may already carry a normal fallback profile from PreserveNonCjk (it was seen
            // before the special case applied, or before it gained CJK). Recover its source font so
            // the takeover happens in this pass instead of the next one.
            if (!hasState && IsFontProfile(text.font))
            {
                TMP_FontAsset profileSource = GetProfileSource(text.font);
                if (profileSource != null && IsCjkOverrideFont(profileSource))
                {
                    text.font = profileSource;
                    text.fontSharedMaterial = profileSource.material;
                    text.SetAllDirty();
                }
            }

            TMP_FontAsset source = text.font;
            if (source == null || !IsCjkOverrideFont(source))
            {
                return false;
            }

            if (!CjkOverrideAllowed(text, source, mode))
            {
                return false;
            }

            TMP_FontAsset profile = GetProfile(source, true, true);
            if (profile == null)
            {
                // Reached again on every pass for this text; report it once.
                LogCjkOnce("noprofile:" + source.name,
                    "[Cjk] no profile for whitelisted font '" + source.name +
                    "'; the text keeps its original CJK glyphs.");
                return false;
            }

            if (!NativeGlyphGuard.Installed)
            {
                LogCjkOnce("noguard:" + source.name,
                    "[Cjk] glyph guard is not installed; skipping special takeover for '" +
                    source.name + "'.");
                return false;
            }

            Material sourceMaterial = text.fontSharedMaterial;
            Material appliedMaterial;
            bool ownsMaterial;
            if (!TryBuildCjkOverrideMaterial(source, profile, sourceMaterial,
                    out appliedMaterial, out ownsMaterial))
            {
                LogCjkOnce("material:" + text.gameObject.name,
                    "[Cjk] custom material of '" + text.gameObject.name +
                    "' could not be cloned; keeping the source font.");
                return false;
            }

            OriginalTmpStates[text] = CaptureTextState(source, sourceMaterial);

            state = new CjkOverrideState();
            state.Source = source;
            state.SourceMaterial = sourceMaterial;
            state.Profile = profile;
            state.AppliedMaterial = appliedMaterial;
            state.OwnsMaterial = ownsMaterial;
            state.Mode = mode;
            CjkOverrides[text] = state;

            // Warm the profile with the CJK characters already present in this text. TMP normally
            // adds them on demand, but doing it before the font swap makes the takeover visible on
            // the same frame and avoids relying on a later layout pass.
            PrewarmCjkGlyphs(profile, text.text);

            text.font = profile;
            text.fontSharedMaterial = appliedMaterial;
            text.SetAllDirty();

            if (CjkOverrideLogCache.Add("apply:" + source.name))
            {
                Log.LogInfo("[Cjk] takeover applied: '" + source.name + "' -> '" + profile.name + "'.");
            }

            if ((CompatibilityLog != null && CompatibilityLog.Value) ||
                (LogTextDetails != null && LogTextDetails.Value))
            {
                Log.LogInfo("[Cjk] '" + text.gameObject.name + "' uses profile '" + profile.name +
                    "' (source '" + source.name + "', mode=" + mode + ").");
            }
            else
            {
                Log.LogDebug("[Cjk] takeover: '" + source.name + "' -> '" + profile.name + "'.");
            }

            return true;
        }

        private static bool CjkOverrideAllowed(TMP_Text text, TMP_FontAsset source, FontReplaceMode mode,
            Material materialOverride = null)
        {
            if (ExcludedObjectPatterns.Count > 0 &&
                MatchesExclusion(text.gameObject.name, ExcludedObjectPatterns))
            {
                LogCjkSkip(text, "ExcludedObject");
                return false;
            }

            if (ExcludedFontPatterns.Count > 0 &&
                MatchesExclusion(source.name, ExcludedFontPatterns))
            {
                LogCjkSkip(text, "ExcludedFont");
                return false;
            }

            Material material = materialOverride != null ? materialOverride : text.fontSharedMaterial;
            if (material != null && ExcludedMaterialPatterns.Count > 0 &&
                MatchesExclusion(material.name, ExcludedMaterialPatterns))
            {
                LogCjkSkip(text, "ExcludedMaterial");
                return false;
            }

            if (mode == FontReplaceMode.Safe)
            {
                string content = text.text;
                if (content != null && content.IndexOf("<sprite", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    LogCjkSkip(text, "SpriteTag");
                    return false;
                }
            }

            return true;
        }

        private static void PrewarmCjkGlyphs(TMP_FontAsset profile, string content)
        {
            if (profile == null || string.IsNullOrEmpty(content))
            {
                return;
            }

            try
            {
                for (int i = 0; i < content.Length; i++)
                {
                    char character = content[i];
                    if (CjkText.IsCjk(character))
                    {
                        profile.HasCharacter(character, false, true);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogDebug("[Cjk] prewarm failed: " + ex.Message);
            }
        }

        private static void LogCjkSkip(TMP_Text text, string reason)
        {
            string key = (text != null ? text.gameObject.name : "<null>") + "|" + reason;
            if (CjkOverrideLogCache.Count < 40 && CjkOverrideLogCache.Add(key))
            {
                Log.LogInfo("[Cjk] skipped '" + key + "'.");
            }
        }

        /// <summary>
        /// Warns once per key. The CjkOverride path is re-entered for every whitelisted text on every
        /// pass, so an unconditional warning would flood the log while the condition persists.
        /// </summary>
        private static void LogCjkOnce(string key, string message)
        {
            if (CjkOverrideLogCache.Count < 40 && CjkOverrideLogCache.Add(key))
            {
                Log.LogWarning(message);
            }
        }

        private static bool IsCjkOverrideFont(TMP_FontAsset font)
        {
            // Asset names in the full game frequently carry an " SDF" suffix; the whitelist is
            // substring-based for the same reason the other exclusion lists are.
            // A profile is named "<plugin>@<source>" and would otherwise match the source name;
            // profiles are not source fonts and must never be passed to GetProfile again.
            return font != null && !IsFontProfile(font) &&
                MatchesExclusion(font.name, CjkOverridePatterns);
        }

        private static bool TryBuildCjkOverrideMaterial(TMP_FontAsset source, TMP_FontAsset profile,
            Material sourceMaterial, out Material applied, out bool ownsMaterial)
        {
            applied = null;
            ownsMaterial = false;

            if (sourceMaterial == null || sourceMaterial == source.material)
            {
                applied = profile.material;
                return applied != null;
            }

            if (!PreserveCustomMaterials.Value)
            {
                applied = profile.material;
                return applied != null;
            }

            try
            {
                Texture2D atlas = profile.atlasTextures != null && profile.atlasTextures.Length > 0
                    ? profile.atlasTextures[0]
                    : null;
                if (atlas == null)
                {
                    Log.LogWarning("[Cjk] profile '" + profile.name +
                        "' has no atlas texture; keeping the source font.");
                    return false;
                }

                Material clone = new Material(sourceMaterial);
                clone.name = sourceMaterial.name + " (CJK profile)";
                clone.SetTexture(ShaderUtilities.ID_MainTex, atlas);
                clone.SetFloat(ShaderUtilities.ID_TextureWidth, profile.atlasWidth);
                clone.SetFloat(ShaderUtilities.ID_TextureHeight, profile.atlasHeight);
                applied = clone;
                ownsMaterial = true;
                return true;
            }
            catch (Exception ex)
            {
                Log.LogWarning("[Cjk] material clone failed: " + ex.Message);
                return false;
            }
        }

        private static void RestoreCjkOverride(TMP_Text text)
        {
            if (text == null)
            {
                return;
            }

            CjkOverrideState state;
            if (!CjkOverrides.TryGetValue(text, out state))
            {
                return;
            }

            CjkOverrides.Remove(text);
            if (text.font == null || text.font == state.Profile)
            {
                if (state.Source != null)
                {
                    text.font = state.Source;
                }

                // Never write a null material back: TMP renders nothing without one. The captured
                // material is normally set, but a text that was taken over while inactive may not
                // have had one yet.
                text.fontSharedMaterial = state.SourceMaterial != null
                    ? state.SourceMaterial
                    : state.Source != null ? state.Source.material : null;
                text.SetAllDirty();
            }
            else if (text.fontSharedMaterial == state.AppliedMaterial)
            {
                // Another mod changed the font but left our cloned material behind; do not destroy
                // a material that is still attached to the text.
                text.fontSharedMaterial = state.SourceMaterial != null
                    ? state.SourceMaterial
                    : state.Source != null ? state.Source.material : null;
                text.SetAllDirty();
            }

            if (state.OwnsMaterial && state.AppliedMaterial != null)
            {
                Destroy(state.AppliedMaterial);
            }
        }

        /// <summary>
        /// Applies or reverts the special case on the affected texts in the same frame as the panel
        /// click; a full refresh alone can miss a text whose CjkOverrideState was lost or that became
        /// relevant through the event-driven path. The ordinary Preserve fallbacks are rebuilt by the
        /// refresh queued at the end.
        /// </summary>
        private static void RefreshCjkOverrideNow()
        {
            int relevantTexts = 0;
            TMP_Text[] allTexts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            for (int i = 0; i < allTexts.Length; i++)
            {
                TMP_Text text = allTexts[i];
                if (text == null || !IsLiveSceneObject(text))
                {
                    continue;
                }

                bool relevant = CjkOverrides.ContainsKey(text) ||
                    IsFontProfile(text.font) ||
                    (text.font != null && IsCjkOverrideFont(text.font));
                if (relevant)
                {
                    relevantTexts++;
                    try
                    {
                        ApplySingleText(text);
                    }
                    catch (Exception ex)
                    {
                        LogPassFailure("CJK override refresh", ex);
                    }
                }
            }

            Log.LogInfo("[Cjk] toggle refresh: " + relevantTexts + " relevant TMP text(s), " +
                allTexts.Length + " scanned.");
            RequestTextRefresh();
        }

        private static void RestoreAllCjkOverrides()
        {
            if (CjkOverrides.Count == 0)
            {
                return;
            }

            List<TMP_Text> texts = new List<TMP_Text>(CjkOverrides.Keys);
            for (int i = 0; i < texts.Count; i++)
            {
                RestoreCjkOverride(texts[i]);
            }
        }

        /// <summary>
        /// Belt-and-braces restore for texts that still carry one of our profiles but lost their
        /// CjkOverrideState (for example after a primary switch or a mod-side font assignment).
        /// Toggling the special case off must not depend on the state dictionary being perfect.
        /// </summary>
        private static void RestoreProfileTextFonts()
        {
            TMP_Text[] texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text text = texts[i];
                if (text == null || !IsLiveSceneObject(text))
                {
                    continue;
                }

                // A released font asset reads as null, which is exactly how a text whose restore
                // was missed shows up as blank instead of falling back visibly.
                bool released = text.font == null;
                if (!released && !IsStrictProfile(text.font))
                {
                    continue;
                }

                TMP_FontAsset source = released ? null : GetProfileSource(text.font);
                Material material = source != null ? source.material : null;

                OriginalTextState original;
                if (OriginalTmpStates.TryGetValue(text, out original) &&
                    original.Font != null && !KnownFonts.Contains(original.Font))
                {
                    source = original.Font;
                    material = original.Material;
                }

                if (source == null)
                {
                    source = ResolveFallbackNativeFont();
                    material = source != null ? source.material : null;
                }
                if (source == null)
                {
                    continue;
                }

                if (material == null)
                {
                    material = source.material;
                }

                text.font = source;
                text.fontSharedMaterial = material;
                text.SetAllDirty();
            }
        }

        internal static bool ApplyPanelCjkOverride(bool value)
        {
            if (CjkOverrideEnabled != null && CjkOverrideEnabled.Value != value)
            {
                CjkOverrideEnabled.Value = value;
                SaveConfig();
            }

            // The takeover only exists inside CJK-only mode. Outside it the switch does not change how
            // fonts are replaced, but the restore below still runs as a recovery pass: a text left on
            // a takeover profile (or on its already released asset) would otherwise stay blank.
            if (!IsPreserveNonCjkEnabled)
            {
                RestoreAllCjkOverrides();
                RestoreProfileTextFonts();

                Log.LogInfo("CjkOverrideEnabled = " + value +
                    " (inactive while CJK-only mode is off; leftover takeover fonts were restored)");
                return true;
            }

            if (!value)
            {
                RestoreAllCjkOverrides();
                RestoreProfileTextFonts();
            }

            if (value && !NativeGlyphGuard.Installed)
            {
                Log.LogWarning("[Cjk] special takeover requested, but the glyph guard is not installed; " +
                    "texts keep their source font.");
            }

            ResolvePrimaryFont();
            RefreshCjkOverrideNow();
            Log.LogInfo("CjkOverrideEnabled = " + value + " (whitelist='" +
                (CjkOverrideFonts != null ? CjkOverrideFonts.Value : string.Empty) + "')");
            return true;
        }

        internal static bool IsFontProfile(TMP_FontAsset font)
        {
            return font != null && ProfileAssets.Contains(font);
        }

        /// <summary>
        /// True when the asset is a CJK-only takeover profile. ScaleAware profiles are also font
        /// profiles but must not be released by the whitelist restore paths.
        /// </summary>
        private static bool IsStrictProfile(TMP_FontAsset font)
        {
            if (font == null)
            {
                return false;
            }

            for (int i = 0; i < FontProfiles.Count; i++)
            {
                FontProfileEntry entry = FontProfiles[i];
                if (entry != null && entry.Strict && entry.Profile == font)
                {
                    return true;
                }
            }

            return false;
        }

        private static TMP_FontAsset GetProfileSource(TMP_FontAsset profile)
        {
            if (profile == null)
            {
                return null;
            }

            for (int i = 0; i < FontProfiles.Count; i++)
            {
                FontProfileEntry entry = FontProfiles[i];
                if (entry != null && entry.Profile == profile)
                {
                    return entry.Source;
                }
            }

            return null;
        }

        /// <summary>True when the profile was built from the primary font currently in effect.</summary>
        internal static bool IsCurrentFontProfile(TMP_FontAsset font)
        {
            if (font == null)
            {
                return false;
            }

            for (int i = 0; i < FontProfiles.Count; i++)
            {
                FontProfileEntry entry = FontProfiles[i];
                if (entry != null && entry.Profile == font && entry.Primary == PrimaryFontAsset)
                {
                    return true;
                }
            }

            return false;
        }

        private static int ProfileLimit()
        {
            return MaxFontProfiles == null ? 4 : Mathf.Clamp(MaxFontProfiles.Value, 0, 16);
        }

        private static void TrimFontProfiles(int limit)
        {
            if (limit < 0)
            {
                return;
            }

            while (FontProfiles.Count > limit)
            {
                FontProfileEntry victim = null;
                for (int i = 0; i < FontProfiles.Count; i++)
                {
                    FontProfileEntry entry = FontProfiles[i];
                    if (entry == null || entry.Profile == null)
                    {
                        victim = entry;
                        break;
                    }

                    if (IsFontReferenced(entry.Profile))
                    {
                        continue;
                    }

                    if (victim == null || entry.Stamp < victim.Stamp)
                    {
                        victim = entry;
                    }
                }

                if (victim == null)
                {
                    break;
                }

                DestroyProfile(victim);
            }
        }

        /// <summary>
        /// Releases the profiles that were built from a primary font other than the current one: their
        /// metrics and atlas belong to the previous font and cannot be reused. Texts still pointing at
        /// them are moved back to their source font and re-hooked by the pass that called this.
        /// </summary>
        private static void ReleaseStaleProfiles()
        {
            for (int i = FontProfiles.Count - 1; i >= 0; i--)
            {
                FontProfileEntry entry = FontProfiles[i];
                if (entry != null && entry.Primary != PrimaryFontAsset)
                {
                    DestroyProfile(entry);
                }
            }

            // A failed build is only worth retrying after an explicit refresh of the whole scene.
            ProfileCreationFailed.Clear();
        }

        private static void ClearFontProfiles()
        {
            for (int i = FontProfiles.Count - 1; i >= 0; i--)
            {
                DestroyProfile(FontProfiles[i]);
            }

            FontProfiles.Clear();
            ProfileAssets.Clear();
            ProfileLimitBlocked.Clear();
            ProfileLimitLogged.Clear();
            ProfileCreationFailed.Clear();
        }

        private static void DetachProfileFromTexts(TMP_FontAsset profile)
        {
            if (profile == null)
            {
                return;
            }

            TMP_Text[] texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text text = texts[i];
                if (text == null)
                {
                    continue;
                }

                if (text.font != profile)
                {
                    continue;
                }

                TMP_FontAsset source = GetProfileSource(profile);
                Material material = source != null ? source.material : null;

                CjkOverrideState state;
                if (CjkOverrides.TryGetValue(text, out state) && state.Profile == profile)
                {
                    source = state.Source;
                    material = state.SourceMaterial;
                    CjkOverrides.Remove(text);
                    if (state.OwnsMaterial && state.AppliedMaterial != null &&
                        state.AppliedMaterial != profile.material)
                    {
                        Destroy(state.AppliedMaterial);
                    }
                }

                OriginalTextState original;
                if ((source == null || KnownFonts.Contains(source)) &&
                    OriginalTmpStates.TryGetValue(text, out original) &&
                    original.Font != null && !KnownFonts.Contains(original.Font))
                {
                    source = original.Font;
                    material = original.Material;
                }

                if (source == null || KnownFonts.Contains(source))
                {
                    source = ResolveFallbackNativeFont();
                    material = source != null ? source.material : null;
                }

                if (source != null)
                {
                    text.font = source;

                    // Always attach a material: keeping the profile's material on a source font, or
                    // leaving none at all, renders nothing.
                    text.fontSharedMaterial = material != null ? material : source.material;
                    text.SetAllDirty();
                    SeenTexts.Remove(text);
                }
            }
        }

        private static void DestroyProfile(FontProfileEntry entry)
        {
            if (entry == null)
            {
                return;
            }

            TMP_FontAsset profile = entry.Profile;
            if (profile == null)
            {
                FontProfiles.Remove(entry);
                return;
            }

            // Never destroy an atlas that a live text still uses. This is the last line of
            // defence for a stale reference after a hot reload or a missed swap.
            DetachProfileFromTexts(profile);

            FontProfiles.Remove(entry);
            ProfileAssets.Remove(profile);
            KnownFonts.Remove(profile);
            NativeMetricsAligned.Remove(profile);
            OriginalFaceInfo.Remove(profile);
            RemoveFontFromFallbacks(profile);
            Log.LogInfo("[Metrics] releasing profile '" + profile.name + "'.");

            try
            {
                Texture2D[] textures = profile.atlasTextures;
                if (textures != null)
                {
                    for (int i = 0; i < textures.Length; i++)
                    {
                        if (textures[i] != null)
                        {
                            Destroy(textures[i]);
                        }
                    }
                }

                Material material = profile.material;
                if (material != null)
                {
                    Destroy(material);
                }

                Destroy(profile);
            }
            catch (Exception ex)
            {
                Log.LogWarning("[Metrics] releasing profile '" + profile.name + "' failed: " + ex.Message);
            }
        }

        /// <summary>
        /// In PreserveNonCjk mode the reference is the game font the most CJK texts use: their
        /// digits are drawn by that font, so the plugin font's CJK metrics are matched to it. The usage
        /// is counted during the pass (see PreserveFontUsage), which avoids a second scene scan.
        /// </summary>
        private static void UpdatePreserveMetricsReference()
        {
            TMP_FontAsset best = null;
            int bestCount = 0;
            foreach (KeyValuePair<TMP_FontAsset, int> pair in PreserveFontUsage)
            {
                TMP_FontAsset font = pair.Key;
                if (font == null || IsKnownFont(font))
                {
                    continue;
                }

                int count = pair.Value;
                if (count > bestCount ||
                    (count == bestCount && best != null && string.CompareOrdinal(font.name, best.name) < 0))
                {
                    best = font;
                    bestCount = count;
                }
            }

            if (best == null || best == s_preserveMetricsReference)
            {
                return;
            }

            s_preserveMetricsReference = best;
            Log.LogInfo("[Native] line metrics reference: '" + best.name + "' (" + bestCount +
                " CJK text(s) use it).");
        }

        /// <summary>
        /// The font the plugin font's metrics should follow. With PreserveNonCjk the digits of a text
        /// are drawn by that text's own font, so the most used one is the only meaningful reference;
        /// other modes fall back to the first usable entry of the plugin font's chain.
        /// </summary>
        private static TMP_FontAsset ResolveMetricsReference()
        {
            if (IsPreserveNonCjkEnabled)
            {
                TMP_FontAsset preserve = s_preserveMetricsReference;
                return preserve != null && !KnownFonts.Contains(preserve) ? preserve : null;
            }

            TMP_FontAsset primary = PrimaryFontAsset;
            if (primary != null)
            {
                List<TMP_FontAsset> chain = primary.fallbackFontAssetTable;
                if (chain != null)
                {
                    for (int i = 0; i < chain.Count; i++)
                    {
                        TMP_FontAsset font = chain[i];
                        if (font != null && !KnownFonts.Contains(font))
                        {
                            return font;
                        }
                    }
                }
            }

            if (s_metricsReference != null && !KnownFonts.Contains(s_metricsReference))
            {
                return s_metricsReference;
            }

            if (Time.unscaledTime < s_nextMetricsReferenceResolve)
            {
                return null;
            }

            s_nextMetricsReferenceResolve = Time.unscaledTime + 5f;
            try
            {
                TMP_FontAsset gameDefault = TMP_Settings.defaultFontAsset;
                if (gameDefault != null && !KnownFonts.Contains(gameDefault))
                {
                    s_metricsReference = gameDefault;
                    return s_metricsReference;
                }

                List<TMP_FontAsset> originals = CollectOriginalFonts();
                if (originals.Count > 0)
                {
                    s_metricsReference = originals[0];
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("Font metrics reference unavailable: " + ex.Message);
            }

            return s_metricsReference;
        }

        /// <summary>
        /// Forces a re-layout of every text that renders at least one glyph from the plugin font.
        /// SeenTexts alone is not enough: a refresh clears it, and in PreserveNonCjk mode the texts keep
        /// their own font (only the CJK glyphs come from ours), so they are not tracked there either.
        /// </summary>
        private static void MarkSeenTextsDirty(TMP_Text[] allTexts, bool force = false)
        {
            foreach (TMP_Text text in SeenTexts)
            {
                if (text != null)
                {
                    text.SetAllDirty();
                }
            }

            if (allTexts == null)
            {
                return;
            }

            for (int i = 0; i < allTexts.Length; i++)
            {
                TMP_Text text = allTexts[i];
                if (text == null || !IsLiveSceneObject(text))
                {
                    continue;
                }

                if (force)
                {
                    text.SetAllDirty();
                    continue;
                }

                // "The text's font is ours" misses the common PreserveNonCjk case: the text keeps a
                // game font while single characters (digits, Latin, icons) come from the plugin font
                // through the fallback chain. The actual glyphs are the only reliable answer.
                if (IsKnownFont(text.font) || TextRendersOurGlyph(text))
                {
                    text.SetAllDirty();
                }
            }
        }

        /// <summary>
        /// True when the current layout draws at least one visible character from a plugin font. Used to
        /// decide whether a text has to regenerate after glyphs were stripped from the plugin font; a
        /// digits-only text can qualify, so checking for CJK content is not enough.
        /// </summary>
        private static bool TextRendersOurGlyph(TMP_Text text)
        {
            TMP_TextInfo info = text.textInfo;
            if (info == null || info.characterInfo == null || info.characterCount <= 0)
            {
                return false;
            }

            int limit = Mathf.Min(info.characterCount, info.characterInfo.Length);
            for (int i = 0; i < limit; i++)
            {
                TMP_CharacterInfo characterInfo = info.characterInfo[i];
                if (!characterInfo.isVisible)
                {
                    continue;
                }

                TMP_TextElement element = characterInfo.textElement;
                TMP_FontAsset font = element != null ? element.textAsset as TMP_FontAsset : null;
                if (IsKnownFont(font))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// One-line report of what the screen still draws with the plugin font after PreserveNonCjk was
        /// switched on: texts whose own font is ours, and texts that draw a non-CJK character with it
        /// (those are what makes digits and Latin look like the plugin font). Object names are listed for
        /// a few of them so a failure can be traced to a UI instead of guessed at. Inactive texts are
        /// skipped: their layout is not rebuilt until they are enabled again, so it would only report
        /// stale glyphs from before the switch.
        /// </summary>
        private static void LogPreserveRenderState(TMP_Text[] allTexts)
        {
            int onOurs = 0;
            int nonCjkOnOurs = 0;
            string samples = string.Empty;

            for (int i = 0; i < allTexts.Length; i++)
            {
                TMP_Text text = allTexts[i];
                if (text == null || !IsLiveSceneObject(text) || !text.isActiveAndEnabled)
                {
                    continue;
                }

                if (IsKnownFont(text.font))
                {
                    onOurs++;
                }

                TMP_TextInfo info = text.textInfo;
                if (info == null || info.characterInfo == null || info.characterCount <= 0)
                {
                    continue;
                }

                int nonCjk = 0;
                int limit = Mathf.Min(info.characterCount, info.characterInfo.Length);
                for (int c = 0; c < limit; c++)
                {
                    TMP_CharacterInfo characterInfo = info.characterInfo[c];
                    if (!characterInfo.isVisible || CjkText.IsCjk(characterInfo.character))
                    {
                        continue;
                    }

                    TMP_TextElement element = characterInfo.textElement;
                    TMP_FontAsset glyphFont = element != null ? element.textAsset as TMP_FontAsset : null;
                    if (IsKnownFont(glyphFont) && NativeGlyphGuard.HasNativeGlyph(characterInfo.character))
                    {
                        // The plugin font draws a character a game font could draw: either a glyph it
                        // took over earlier and still has in this layout, or a routing failure.
                        nonCjk++;
                    }
                }

                if (nonCjk > 0)
                {
                    nonCjkOnOurs++;
                    if (samples.Length < 180)
                    {
                        samples = samples.Length == 0
                            ? DescribeObject(text) + " x" + nonCjk
                            : samples + ", " + DescribeObject(text) + " x" + nonCjk;
                    }
                }
            }

            // Old UI.Text has no per-glyph fallback: pure-CJK labels are meant to keep the plugin font in
            // this mode, every other label must be back on a game font.
            UnityEngine.UI.Text[] legacyTexts = Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>();
            int legacyOurs = 0;
            for (int i = 0; i < legacyTexts.Length; i++)
            {
                UnityEngine.UI.Text legacy = legacyTexts[i];
                if (legacy == null || !IsLiveSceneObject(legacy) || !legacy.isActiveAndEnabled ||
                    !KnownUnityFonts.Contains(legacy.font) || CjkText.IsPureCjk(legacy.text))
                {
                    continue;
                }

                legacyOurs++;
                if (samples.Length < 180)
                {
                    samples = samples.Length == 0
                        ? "legacy " + legacy.gameObject.name
                        : samples + ", legacy " + legacy.gameObject.name;
                }
            }

            if (onOurs == 0 && nonCjkOnOurs == 0 && legacyOurs == 0)
            {
                Log.LogInfo("[Preserve] check after switching on: every active text draws its non-CJK " +
                    "characters with a game font.");
                return;
            }

            Log.LogWarning("[Preserve] check after switching on: " + onOurs +
                " text(s) still use the plugin font, " + nonCjkOnOurs +
                " text(s) draw non-CJK characters with it that a game font could render, " + legacyOurs +
                " legacy label(s) still use it" +
                (samples.Length > 0 ? ": " + samples : string.Empty) + ".");
        }

        /// <summary>Puts every adjusted font back on its own metrics (option switched off).</summary>
        private static void RestoreNativeMetrics()
        {
            if (OriginalFaceInfo.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<TMP_FontAsset, FaceInfo> pair in OriginalFaceInfo)
            {
                if (pair.Key != null)
                {
                    pair.Key.faceInfo = pair.Value;
                }
            }

            OriginalFaceInfo.Clear();
            NativeMetricsAligned.Clear();
            s_metricsReference = null;
            s_nextMetricsReferenceResolve = 0f;

            // The CJK glyphs already on screen still use the aligned metrics; make them regenerate.
            MarkSeenTextsDirty(Resources.FindObjectsOfTypeAll<TMP_Text>());
        }

        /// <summary>
        /// Reports a text that had to be moved to a stand-in game font, so a future log shows why a few
        /// rows look like the game font instead of the one they were designed with. Bounded to a handful
        /// of lines per session.
        /// </summary>
        private static void LogAdoptedNativeFont(TMP_Text text, TMP_FontAsset font)
        {
            s_adoptedNativeFonts++;
            if (s_adoptedNativeFonts > 10)
            {
                if (s_adoptedNativeFonts == 11)
                {
                    Log.LogInfo("[Preserve] further texts restored to a stand-in game font are not listed.");
                }

                return;
            }

            Log.LogInfo("[Preserve] " + DescribeObject(text) +
                " had no original font snapshot; restored it to the game font '" + font.name + "'.");
        }

        /// <summary>Reports a text that keeps the plugin font because no game font is known yet.</summary>
        private static void LogUnrestorableText(TMP_Text text)
        {
            s_unrestorableTexts++;
            float now = Time.unscaledTime;
            if (now < s_nextUnrestorableLogTime)
            {
                return;
            }

            s_nextUnrestorableLogTime = now + 30f;
            Log.LogWarning("[Preserve] no game font is available to restore " + DescribeObject(text) +
                "; it keeps the plugin font (" + s_unrestorableTexts + " text(s) so far).");
        }

        private static string DescribeObject(TMP_Text text)
        {
            if (text == null || text.gameObject == null)
            {
                return "<null>";
            }

            TMP_FontAsset font = text.font;
            return text.gameObject.name + " (font=" + (font != null ? font.name : "<null>") + ")";
        }

        /// <summary>
        /// Reverts one text to its snapshotted font when it still carries ours but no longer qualifies
        /// (stricter mode, new exclusion, FallbackOnly, PreserveNonCjk switched on). The snapshot is kept
        /// so it can be re-applied. The cache memoizes name→asset lookups across one pass.
        /// </summary>
        private static void RestoreTextFontIfOurs(TMP_Text text, Dictionary<string, TMP_FontAsset> cache)
        {
            if (text == null || !IsKnownFont(text.font))
            {
                return;
            }

            // A profile is a working CJK asset with its own source-font fallback. Keep it while it
            // belongs to the current primary: restoring a CJK label to the most-used native font
            // can blank the label. A profile left over from the previous primary (for example a
            // JetBrains profile after switching back to a CJK font) must still be replaced,
            // otherwise the CJK text keeps using the old, CJK-less profile.
            if (IsPreserveNonCjkEnabled && CjkOverrides.ContainsKey(text) &&
                IsCurrentFontProfile(text.font))
            {
                return;
            }

            OriginalTextState state;
            TMP_FontAsset original = null;
            Material material = null;
            if (OriginalTmpStates.TryGetValue(text, out state))
            {
                original = ResolveFontAsset(state, cache);
                if (original == null || KnownFonts.Contains(original))
                {
                    // No longer resolvable, or a poisoned snapshot holding one of our fonts: look for a
                    // game font stand-in instead of leaving the plugin font on the text.
                    original = null;
                }
                else
                {
                    material = state.Material;
                }
            }

            if (original == null)
            {
                // An instance of a prefab that was re-fonted in an earlier pass has no snapshot of its
                // own, so its original font is simply unknown. Staying on the plugin font is not an
                // option: whole rows (e.g. the settings panel) would keep it after PreserveNonCjk is
                // switched on. Adopt the best game font stand-in and remember it, so later passes stay
                // on that font instead of re-deciding every time.
                original = ResolveFallbackNativeFont();
                if (original == null)
                {
                    LogUnrestorableText(text);
                    return;
                }

                material = original.material;
                OriginalTmpStates[text] = CaptureTextState(original, material);
                LogAdoptedNativeFont(text, original);
            }

            text.font = original;

            // Always attach a material. Keeping the profile's material here (or a null one) would
            // leave the text drawing against an atlas that is about to be destroyed.
            text.fontSharedMaterial = material != null ? material : original.material;
            text.SetAllDirty();
            SeenTexts.Remove(text);
        }

        /// <summary>Same as RestoreTextFontIfOurs for legacy UI.Text components.</summary>
        private static void RestoreLegacyFontIfOurs(UnityEngine.UI.Text text)
        {
            if (text == null || !KnownUnityFonts.Contains(text.font))
            {
                return;
            }

            Font original;
            if (!OriginalLegacyFonts.TryGetValue(text, out original) || original == null ||
                KnownUnityFonts.Contains(original))
            {
                // No usable snapshot: the label was already carrying the plugin font when it was first
                // seen (a clone of a re-fonted prefab). Use the font the other labels use instead of
                // keeping ours; PreserveNonCjk has no per-glyph fallback to hide this on.
                original = ResolveLegacyFallbackFont();
                if (original == null)
                {
                    return;
                }

                OriginalLegacyFonts[text] = original;
            }

            text.font = original;
            text.SetAllDirty();
            SeenLegacyTexts.Remove(text);
        }

        /// <summary>Same as RestoreTextFontIfOurs for input fields, with a game font as fallback.</summary>
        private static void RestoreInputFontIfOurs(TMP_InputField inputField)
        {
            if (inputField == null || !IsKnownFont(inputField.fontAsset))
            {
                return;
            }

            TMP_FontAsset original;
            if (!OriginalInputFonts.TryGetValue(inputField, out original) || original == null ||
                IsKnownFont(original))
            {
                original = ResolveFallbackNativeFont();
                if (original != null)
                {
                    OriginalInputFonts[inputField] = original;
                }
            }

            if (original != null)
            {
                inputField.fontAsset = original;
                if (inputField.textComponent != null)
                {
                    // Restore the material as well: a takeover leaves the text on the profile's
                    // material, which would be destroyed with the profile.
                    inputField.textComponent.fontSharedMaterial = original.material;
                    inputField.textComponent.SetAllDirty();
                }
            }

            SeenInputFields.Remove(inputField);
        }

        /// <summary>
        /// Called after the fonts folder was rescanned. Clears a selection whose file disappeared and
        /// re-resolves; the re-apply is queued for the next frame like every other panel-driven change.
        /// Removing every file must revert the texts to the game's fonts across all scenes and tabs.
        /// Returns true when a previously applied font went away, so the caller can rebuild its UI.
        /// </summary>
        internal static bool RevalidateFontSelection()
        {
            bool hadPrimary = PrimaryFontAsset != null;

            // The folder was just rescanned: a file that failed to load earlier deserves a new attempt,
            // it may have finished downloading or been replaced by a valid one.
            FailedFontPaths.Clear();

            string wanted = FontFile.Value;
            if (!string.IsNullOrEmpty(wanted) && FindLocalFontPath(wanted) == null)
            {
                FontFile.Value = string.Empty;
                SaveConfig();
                Log.LogWarning("Font file '" + wanted + "' is gone from " + FontsDir + "; FontFile cleared.");
            }

            ResolvePrimaryFont();
            RequestTextRefresh();

            // Only a font that used to be applied and is now gone needs the UI rebuilt; a session that
            // never picked a font stays as it is.
            return hadPrimary && PrimaryFontAsset == null;
        }

        internal static List<string> ScanFontFiles(string directory)
        {
            List<string> paths = new List<string>();
            CollectFontFiles(directory, paths);
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths;
        }

        private static void CollectFontFiles(string directory, List<string> paths)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            foreach (string pattern in new[] { "*.ttf", "*.otf" })
            {
                try
                {
                    paths.AddRange(Directory.GetFiles(directory, pattern));
                }
                catch (Exception ex)
                {
                    Log.LogWarning("Font scan failed in " + directory + ": " + ex.Message);
                }
            }
        }

        internal static List<string> ScanSystemFonts()
        {
            List<string> paths = new List<string>();
            try
            {
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(windows))
                {
                    CollectFontFiles(Path.Combine(windows, "Fonts"), paths);
                }

                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (!string.IsNullOrEmpty(localAppData))
                {
                    CollectFontFiles(Path.Combine(localAppData, "Microsoft", "Windows", "Fonts"), paths);
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("System font scan failed: " + ex.Message);
            }

            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths;
        }

        /// <summary>
        /// Selects a local font file. It is stored as the exact file name and only drives the primary
        /// font while SystemFontPriority is off.
        /// </summary>
        internal static bool SelectPluginFont(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            // An explicit pick is a deliberate retry, even for a file that failed before.
            FailedFontPaths.Remove(path);

            int index = EnsureFontAsset(path);
            if (index < 0)
            {
                return false;
            }

            string fileName = Path.GetFileName(path);
            FontFile.Value = fileName;
            SaveConfig();

            // Apply directly from the asset we just built. Resolving by file name again could
            // miss a file whose casing or directory changed between the two scans.
            if (!SystemFontPriority.Value)
            {
                SetPrimaryFont(index);
                RequestTextRefresh();
            }

            Log.LogInfo("Selected font file: " + fileName +
                (SystemFontPriority.Value
                    ? " (recorded; SystemFontPriority is on, keeping " + DescribePrimaryFont() + ")"
                    : " (" + DescribePrimaryFont() + ")"));
            return true;
        }

        /// <summary>
        /// Records the system font selection. It is used only while SystemFontPriority is on; with the
        /// switch off the local font file stays in charge (and nothing is applied when it is empty).
        /// </summary>
        internal static bool SelectSystemFont(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string name = Path.GetFileNameWithoutExtension(path);
            bool systemPreferred = SystemFontPriority.Value;

            // Load first, then commit the config. The old order wrote SystemFont even when the
            // file could not be loaded, leaving the panel pointing at a font the plugin could not
            // build and making the next refresh look like the panel font vanished.
            int index = -1;
            if (systemPreferred)
            {
                FailedFontPaths.Remove(path);
                index = EnsureFontAsset(path);
                if (index < 0)
                {
                    Log.LogWarning("Selected system font '" + Path.GetFileName(path) +
                        "' could not be built; keeping the current font.");
                    return false;
                }
            }

            SystemFont.Value = name;
            SaveConfig();

            if (systemPreferred)
            {
                // Use the asset index we just loaded; resolving by name again could fail after
                // the config write and clear the selection, which looked like the font vanished.
                SetPrimaryFont(index);
                RequestTextRefresh();
                Log.LogInfo("Selected system font: " + name + " (" + DescribePrimaryFont() + ")");
            }
            else
            {
                Log.LogInfo(
                    "Selected system font: " + name + " (recorded; SystemFontPriority is off, keeping " +
                    DescribePrimaryFont() + ")");
            }

            return true;
        }

        // ---- Font weight selection (panel "字重选择") ----

        /// <summary>Weight keywords recognised at the end of a font file name.</summary>
        private static readonly string[] WeightTokens =
        {
            "ExtraBold", "UltraBold", "SemiBold", "DemiBold", "ExtraLight", "UltraLight",
            "Regular", "Normal", "Book", "Thin", "Light", "Medium", "Bold", "Black", "Heavy"
        };

        /// <summary>Canonical weight names, light to heavy; also the display order.</summary>
        private static readonly string[] WeightOrder =
        {
            "Thin", "ExtraLight", "Light", "Regular", "Medium", "SemiBold", "Bold", "ExtraBold", "Black"
        };

        /// <summary>
        /// Weight names available for the font that is currently effective, plus the index of the
        /// current selection. Both come from a single directory scan, so the panel can refresh every
        /// affected row without repeating the scan. The source follows SystemFontPriority: system fonts
        /// when it is on and a system font is selected, local files otherwise. The family is taken from
        /// the selected file, so only its siblings are listed.
        /// </summary>
        internal static string[] GetFontWeightChoices(out int selectedIndex)
        {
            string[] labels;
            List<string> paths = GetWeightCandidates(out labels);

            selectedIndex = -1;
            if (paths.Count > 0)
            {
                bool system = SystemFontPriority.Value;
                string current = system ? SystemFont.Value : FontFile.Value;
                for (int i = 0; i < paths.Count; i++)
                {
                    if (NameMatches(paths[i], current))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }

            return labels;
        }

        /// <summary>Switches the effective font to the chosen weight by selecting that file.</summary>
        internal static bool SelectFontWeight(int index)
        {
            string ignored;
            return SelectFontWeight(index, out ignored);
        }

        /// <summary>
        /// Same as SelectFontWeight, but also returns the file path behind the choice. The panel
        /// uses it to select the exact system-font row; matching by SystemFont's base name can hit
        /// the wrong entry when two font files share a name.
        /// </summary>
        internal static bool SelectFontWeight(int index, out string selectedPath)
        {
            selectedPath = null;
            string[] labels;
            List<string> paths = GetWeightCandidates(out labels);
            bool system = SystemFontPriority.Value;
            if (index < 0 || index >= paths.Count)
            {
                Log.LogWarning("[Weight] no candidate at index " + index + " (count " + paths.Count +
                    ", system=" + system + ", current=" +
                    (system ? SystemFont.Value : FontFile.Value) + ").");
                return false;
            }

            selectedPath = paths[index];
            Log.LogInfo("[Weight] index " + index + " -> " + Path.GetFileName(selectedPath) +
                " (" + (system ? "system" : "local") + ").");
            return system ? SelectSystemFont(selectedPath) : SelectPluginFont(selectedPath);
        }

        /// <summary>
        /// True when the font currently in effect is a variable font. Unity 2022 exposes no
        /// variation-axis API (checked against the game's TextCore and TMP assemblies), so such a
        /// font only ever renders its default instance and its other weights are unreachable.
        /// </summary>
        internal static bool ActiveFontIsVariable()
        {
            if (PrimaryFontAsset == null)
            {
                return false;
            }

            int index = LoadedFonts.IndexOf(PrimaryFontAsset);
            string path = index >= 0 && index < LoadedFontPaths.Count ? LoadedFontPaths[index] : null;
            return IsVariableFont(path);
        }

        private static readonly Dictionary<string, bool> VariableFontCache = new Dictionary<string, bool>();

        /// <summary>True when the file contains an fvar table, which only variable fonts have.</summary>
        private static bool IsVariableFont(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            bool known;
            if (VariableFontCache.TryGetValue(path, out known))
            {
                return known;
            }

            bool variable = false;
            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    if (stream.Length >= 12)
                    {
                        stream.Position = 4;
                        int tableCount = reader.ReadUInt16();
                        stream.Position = 12;
                        for (int i = 0; i < tableCount && stream.Position + 16 <= stream.Length; i++)
                        {
                            string tag = new string(reader.ReadChars(4));
                            reader.ReadUInt32();
                            reader.ReadUInt32();
                            reader.ReadUInt32();
                            if (tag == "fvar")
                            {
                                variable = true;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                variable = false;
            }

            VariableFontCache[path] = variable;
            return variable;
        }

        /// <summary>
        /// Files of the effective source that belong to the same family as the current selection,
        /// ordered light to heavy. Empty when nothing is selected or the selection is not in the
        /// source, and a single entry when the family has only one file. Scans the directory once.
        /// </summary>
        private static List<string> GetWeightCandidates(out string[] labels)
        {
            labels = new string[0];

            // The weight list follows the source SystemFontPriority selects.
            bool system = SystemFontPriority.Value;
            string current = system ? SystemFont.Value : FontFile.Value;
            if (string.IsNullOrEmpty(current))
            {
                return new List<string>();
            }

            List<string> source = system ? ScanSystemFonts() : ScanFontFiles(FontsDir);
            HashSet<string> baseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < source.Count; i++)
            {
                baseNames.Add(Path.GetFileNameWithoutExtension(source[i]));
            }

            // Weight detection is file-name work; one pass also yields the family of the current
            // selection, which the ordered sibling list below needs.
            WeightInfo[] infos = new WeightInfo[source.Count];
            string family = null;
            for (int i = 0; i < source.Count; i++)
            {
                infos[i] = DescribeWeight(source[i], system, baseNames);
                if (family == null && NameMatches(source[i], current))
                {
                    family = infos[i].Family;
                }
            }

            List<string> paths = new List<string>();
            if (string.IsNullOrEmpty(family))
            {
                return paths;
            }

            List<string> order = new List<string>();
            for (int w = 0; w < WeightOrder.Length; w++)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    if (!string.Equals(infos[i].Family, family, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(infos[i].Weight, WeightOrder[w], StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    order.Add(WeightOrder[w]);
                    paths.Add(source[i]);
                    break;
                }
            }

            labels = order.ToArray();
            return paths;
        }

        private struct WeightInfo
        {
            public string Family;
            public string Weight;
        }

        /// <summary>
        /// File-name weights are explicit for the fonts/ folder (HarmonyOS_Sans_SC_Bold.ttf). Windows
        /// system fonts usually use short style codes instead (arial.ttf, arialbd.ttf, Dengb.ttf,
        /// corbell.ttf), so a suffix is only treated as a weight when the remaining prefix is another
        /// file in the same directory. That keeps a family such as "corbel" from being split at its
        /// last letter while still grouping corbel/corbelb/corbell/corbeli.
        /// </summary>
        private static WeightInfo DescribeWeight(string path, bool system, HashSet<string> baseNames)
        {
            WeightInfo info = default(WeightInfo);
            string baseName = Path.GetFileNameWithoutExtension(path);
            ExtractWeight(baseName, out info.Weight, out info.Family);

            if (!system || !string.Equals(info.Family, baseName, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }

            for (int i = 0; i < SystemStyleSuffixes.Length; i++)
            {
                string suffix = SystemStyleSuffixes[i];
                if (baseName.Length <= suffix.Length ||
                    !baseName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string prefix = baseName.Substring(0, baseName.Length - suffix.Length).TrimEnd('_', '-', ' ');
                if (prefix.Length == 0 || !baseNames.Contains(prefix))
                {
                    continue;
                }

                info.Family = prefix;
                info.Weight = SystemStyleWeights[i];
                return info;
            }

            return info;
        }

        /// <summary>Short style suffixes used by Windows system font files, longest first.</summary>
        private static readonly string[] SystemStyleSuffixes =
        {
            "blk", "bd", "bi", "sb", "lt", "li", "sl", "rg", "md", "it", "l", "b", "i", "z", "h"
        };

        private static readonly string[] SystemStyleWeights =
        {
            "Black", "Bold", "Bold", "SemiBold", "Light", "Light", "Light", "Regular", "Medium",
            "Regular", "Light", "Bold", "Regular", "Bold", "Heavy"
        };

        /// <summary>
        /// Splits "Family_Weight" style names into family and canonical weight. A name without a known
        /// keyword is treated as the regular weight of its own family.
        /// </summary>
        private static bool ExtractWeight(string baseName, out string weight, out string family)
        {
            weight = "Regular";
            family = baseName;
            if (string.IsNullOrEmpty(baseName))
            {
                return false;
            }

            for (int i = 0; i < WeightTokens.Length; i++)
            {
                string token = WeightTokens[i];
                if (baseName.Length <= token.Length ||
                    !baseName.EndsWith(token, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string prefix = baseName.Substring(0, baseName.Length - token.Length).TrimEnd('_', '-', ' ');
                if (prefix.Length == 0)
                {
                    continue;
                }

                family = prefix;
                weight = CanonicalWeight(token);
                return true;
            }

            return true;
        }

        private static string CanonicalWeight(string token)
        {
            if (string.Equals(token, "UltraBold", StringComparison.OrdinalIgnoreCase))
            {
                return "ExtraBold";
            }

            if (string.Equals(token, "DemiBold", StringComparison.OrdinalIgnoreCase))
            {
                return "SemiBold";
            }

            if (string.Equals(token, "UltraLight", StringComparison.OrdinalIgnoreCase))
            {
                return "ExtraLight";
            }

            if (string.Equals(token, "Normal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "Book", StringComparison.OrdinalIgnoreCase))
            {
                return "Regular";
            }

            return token;
        }

        internal static bool ApplyPanelEnabled(bool value)
        {
            replacementSuspended = !value;

            if (Enabled.Value != value)
            {
                Enabled.Value = value;
                SaveConfig();
            }

            if (value)
            {
                RequestTextRefresh();
            }
            else
            {
                RestoreOriginalFonts();
                ReleaseExternalFallbacks();
                RestoreNativeMetrics();
                ClearFontProfiles();
                ResetSeenState();
            }

            Log.LogInfo("Font replacement " + (value ? "enabled" : "disabled") +
                " (settings panel); cfg=" + Enabled.Value + ", suspended=" + replacementSuspended);
            return true;
        }

        /// <summary>
        /// PreserveNonCjk's guard only permits CJK glyphs in plugin assets. Before enabling it, put
        /// every text that still uses a replacement/ScaleAware profile back on its source font, so
        /// the guard's first purge cannot remove glyphs a live text is still rendering.
        /// </summary>
        private static void RestoreReplacementFontsBeforePreserve()
        {
            int restored = 0;
            Dictionary<string, TMP_FontAsset> cache =
                new Dictionary<string, TMP_FontAsset>(StringComparer.OrdinalIgnoreCase);
            TMP_Text[] texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text text = texts[i];
                if (text == null || !IsLiveSceneObject(text) || !IsKnownFont(text.font))
                {
                    continue;
                }

                // A whitelist takeover owns its strict profile and is re-asserted by the normal pass.
                if (CjkOverrides.ContainsKey(text) && IsCurrentFontProfile(text.font))
                {
                    continue;
                }

                TMP_FontAsset before = text.font;
                RestoreTextFontIfOurs(text, cache);
                if (before != text.font)
                {
                    restored++;
                }
            }

            // CjkOverride profiles are already CJK-only; profiles behind ScaleAware/replacement are
            // not. Release only those non-strict variants now; strict profiles may still belong to
            // active CjkOverride states.
            int released = 0;
            for (int i = FontProfiles.Count - 1; i >= 0; i--)
            {
                FontProfileEntry entry = FontProfiles[i];
                if (entry != null && !entry.Strict)
                {
                    DestroyProfile(entry);
                    released++;
                }
            }

            ProfileLimitBlocked.Clear();
            ProfileLimitLogged.Clear();

            if (restored > 0 || released > 0)
            {
                Log.LogInfo("[Preserve] prepared " + restored + " replacement text(s), released " +
                    released + " non-strict profile(s) before the glyph guard.");
            }
        }

        private static void ReleaseStrictProfiles()
        {
            int released = 0;
            for (int i = FontProfiles.Count - 1; i >= 0; i--)
            {
                FontProfileEntry entry = FontProfiles[i];
                if (entry != null && entry.Strict)
                {
                    DestroyProfile(entry);
                    released++;
                }
            }

            if (released > 0)
            {
                Log.LogInfo("[Metrics] released " + released +
                    " strict profile(s) after leaving PreserveNonCjk.");
            }
        }

        /// <summary>
        /// Panel toggle for PreserveNonCjk. Switching on strips the non-CJK entries explicitly: a
        /// glyph already in the character table keeps being served no matter what the guard allows
        /// later. Switching off restores the plugin fonts' own vertical metrics as well.
        /// </summary>
        internal static bool ApplyPanelPreserveNonCjk(bool value)
        {
            if (PreserveNonCjk.Value != value)
            {
                PreserveNonCjk.Value = value;
                SaveConfig();
            }

            if (value)
            {
                // The glyph guard purges non-CJK glyphs from every plugin asset as soon as the mode
                // turns on. A ScaleAware or replacement text still points at its full profile at that
                // moment, so restore the replacement font first; otherwise the purge leaves the text
                // referencing removed glyphs and it disappears until the next layout pass.
                RestoreReplacementFontsBeforePreserve();

                NativeGlyphGuard.InvalidatePurity();
                s_preserveCheckPending = true;
                s_preserveCheckFrame = Time.frameCount;
                if (AlignNativeMetrics.Value)
                {
                    // Re-copy the proportions (the backups are kept) before the screen is rebuilt.
                    NativeMetricsAligned.Clear();
                    s_nextMetricsReferenceResolve = 0f;
                    EnsureNativeMetricsForAll();
                }
                else
                {
                    // A font aligned by an earlier PreserveNonCjk session must return to its own
                    // metrics before this session uses it again.
                    RestoreNativeMetrics();
                }
            }
            else
            {
                RestoreAllCjkOverrides();
                RestoreProfileTextFonts();
                RestoreNativeMetrics();
                ReleaseStrictProfiles();
                ProfileLimitBlocked.Clear();
                ProfileLimitLogged.Clear();
            }

            // Deferred: the toggle runs inside a settings-row callback, see RequestTextRefresh.
            RequestTextRefresh();
            MarkSeenTextsDirty(Resources.FindObjectsOfTypeAll<TMP_Text>(), true);

            Log.LogInfo("PreserveNonCjk = " + value + " (" +
                (NativeGlyphGuard.Installed ? "priority fallback + glyph guard" : "priority fallback") + "; " +
                DescribePrimaryFont() + ")");
            return true;
        }

        /// <summary>
        /// Panel toggle for AlignNativeMetrics. The option only matters in PreserveNonCjk mode, so making
        /// it effective means copying the reference font's proportions onto the plugin fonts (or restoring
        /// their own), then regenerating the texts that already render plugin glyphs: changing FaceInfo
        /// does not invalidate any text on its own.
        /// </summary>
        internal static bool ApplyPanelAlignNativeMetrics(bool value)
        {
            if (AlignNativeMetrics.Value != value)
            {
                AlignNativeMetrics.Value = value;
                SaveConfig();
            }

            // AlignNativeMetrics only affects PreserveNonCjk profiles. Full ScaleAware profiles use
            // the source font's metrics and must not be rebuilt or destroyed by this toggle.
            if (!IsPreserveNonCjkEnabled)
            {
                // The option is inactive outside PreserveNonCjk, but a plugin font may still carry
                // the aligned face from an earlier session. Restore it so a later mode switch does
                // not start from shifted metrics.
                RestoreNativeMetrics();
                Log.LogInfo("AlignNativeMetrics = " + value +
                    " (inactive while PreserveNonCjk is off).");
                return true;
            }

            int aligned = 0;
            if (value)
            {
                NativeMetricsAligned.Clear();
                s_nextMetricsReferenceResolve = 0f;
                aligned = EnsureNativeMetricsForAll();
            }
            else
            {
                RestoreNativeMetrics();
            }

            // A profile's FaceInfo cannot simply be edited in place: its already rasterized glyph
            // metrics were built from the old face. Build replacement profiles and swap every
            // reference before destroying the old assets, so no text points at a dead atlas.
            RebuildStrictProfilesForMetrics();

            // The CJK glyphs already on screen still hold the metrics they were laid out with; a
            // full re-layout is the only way to apply or revert the change.
            MarkSeenTextsDirty(Resources.FindObjectsOfTypeAll<TMP_Text>(), true);

            string detail;
            if (!value)
            {
                detail = "plugin font uses its own line metrics";
            }
            else if (aligned > 0)
            {
                detail = "plugin font follows the game font's line metrics (" + aligned + " font(s) aligned)";
            }
            else
            {
                detail = "no reference game font resolved yet; aligns on the next scan";
            }

            Log.LogInfo("AlignNativeMetrics = " + value + " (" + detail + ")");
            return true;
        }

        internal static bool ApplyPanelSystemFontPriority(bool value)
        {
            if (SystemFontPriority.Value != value)
            {
                SystemFontPriority.Value = value;
                SaveConfig();
            }

            ResolvePrimaryFont();
            RequestTextRefresh();
            Log.LogInfo("SystemFontPriority = " + value + " (" + DescribePrimaryFont() + ")");
            return true;
        }

        /// <summary>
        /// Rebuilds (or reuses) the font in effect at the new sampling point size and queues the
        /// re-apply. The atlas is built here; re-fonting every text waits for the next frame.
        /// </summary>
        internal static bool ApplyPanelSamplingSize(int size)
        {
            int clamped = Mathf.Clamp(size, 8, 128);
            if (SamplingPointSize.Value == clamped)
            {
                // Same size as before: the cached asset is already in use, nothing to rebuild.
                Log.LogInfo("Sampling size = " + clamped + " unchanged; nothing to rebuild.");
                return true;
            }

            SamplingPointSize.Value = clamped;
            SaveConfig();

            int rebuilt = RebuildFontAssets();
            ResolvePrimaryFont();
            RequestTextRefresh();
            Log.LogInfo("Sampling size = " + clamped + ", " + rebuilt +
                " asset(s) built for this size; earlier sizes stay cached for reuse.");
            return true;
        }

        /// <summary>
        /// Makes sure the font in effect exists at the current sampling size. Assets are cached per
        /// (file, size) pair: switching back to a size that was used before reuses the existing atlas
        /// instead of building a new one, so alternating sizes neither grows memory per change nor
        /// re-parses the file. Assets of other sizes are kept, never destroyed: texts skipped by the
        /// replacement rules may still reference them.
        /// </summary>
        private static int RebuildFontAssets()
        {
            int samplingSize = CurrentSamplingSize();

            // A new size is a fresh attempt for a file that failed at another size.
            FailedFontPaths.Clear();

            // Same source rule as ResolvePrimaryFont: only the font that is in effect is built now.
            bool systemPreferred = SystemFontPriority.Value;
            string wanted = systemPreferred ? SystemFont.Value : FontFile.Value;
            string path = string.IsNullOrEmpty(wanted)
                ? null
                : (systemPreferred ? FindSystemFontPath(wanted) : FindLocalFontPath(wanted));

            if (path == null)
            {
                Log.LogInfo("Sampling size = " + samplingSize +
                    ": no font in effect, nothing to rebuild (" + LoadedFonts.Count + " asset(s) cached).");
                return 0;
            }

            if (IndexOfLoadedFont(path, samplingSize) >= 0)
            {
                Log.LogInfo("Sampling size = " + samplingSize + ": '" + Path.GetFileName(path) +
                    "' is already cached at this size (" + LoadedFonts.Count + " asset(s) cached).");
                return 0;
            }

            bool built = LoadFont(path) != null;
            if (!built)
            {
                // Keep the selection but do not re-parse the same file again for this size.
                FailedFontPaths.Add(path);
            }

            Log.LogInfo("Sampling size = " + samplingSize + ": " + (built ? "built" : "failed to build") +
                " '" + Path.GetFileName(path) + "'; " + LoadedFonts.Count +
                " asset(s) cached, the rest of the folder loads when a font is selected.");
            return built ? 1 : 0;
        }

        /// <summary>Sampling size the font assets are built at; clamped and shared by every caller.</summary>
        private static int CurrentSamplingSize()
        {
            return Mathf.Clamp(SamplingPointSize.Value, 8, 128);
        }

        /// <summary>
        /// Enforces Font/MaxCachedSamplingSizes: for every file, only the most recently used sizes are
        /// kept and older ones are destroyed. Destroys nothing that is still referenced, and is meant to
        /// run after a full re-apply so no text can hold a superseded asset any more.
        /// </summary>
        private static void TrimFontCache()
        {
            int profileLimit = ProfileLimit();
            if (profileLimit > 0)
            {
                TrimFontProfiles(profileLimit);
            }

            int limit = Mathf.Clamp(MaxCachedSamplingSizes.Value, 0, 12);
            if (limit <= 0 || LoadedFontPaths.Count <= limit)
            {
                return;
            }

            // Group the cached entries per file, then pick the least recently used ones to release.
            List<string> paths = new List<string>();
            for (int i = 0; i < LoadedFontPaths.Count; i++)
            {
                if (!paths.Contains(LoadedFontPaths[i]))
                {
                    paths.Add(LoadedFontPaths[i]);
                }
            }

            List<int> victims = new List<int>();
            for (int p = 0; p < paths.Count; p++)
            {
                List<int> indexes = new List<int>();
                for (int i = 0; i < LoadedFontPaths.Count; i++)
                {
                    if (string.Equals(LoadedFontPaths[i], paths[p], StringComparison.OrdinalIgnoreCase))
                    {
                        indexes.Add(i);
                    }
                }

                while (indexes.Count > limit)
                {
                    int victim = -1;
                    int oldestStamp = int.MaxValue;
                    for (int k = 0; k < indexes.Count; k++)
                    {
                        int index = indexes[k];
                        if (LoadedFonts[index] == PrimaryFontAsset || LoadedFontSizes[index] == CurrentSamplingSize())
                        {
                            continue;
                        }

                        if (LoadedFontStamps[index] < oldestStamp)
                        {
                            oldestStamp = LoadedFontStamps[index];
                            victim = index;
                        }
                    }

                    if (victim < 0 || IsFontReferenced(LoadedFonts[victim],
                            victim < LoadedUnityFonts.Count ? LoadedUnityFonts[victim] : null))
                    {
                        // Nothing releasable for this file right now; retry on the next pass.
                        break;
                    }

                    victims.Add(victim);
                    indexes.Remove(victim);
                }
            }

            if (victims.Count == 0)
            {
                return;
            }

            // Highest index first: removing an entry shifts only the entries after it.
            victims.Sort();
            int released = 0;
            for (int i = victims.Count - 1; i >= 0; i--)
            {
                if (DestroyCachedFontAt(victims[i]))
                {
                    released++;
                }
            }

            if (released > 0)
            {
                Log.LogInfo("[Cache] Released " + released + " superseded font asset(s); " +
                    LoadedFonts.Count + " kept (limit " + limit + " per file).");
            }

            ProfileLimitBlocked.Clear();
        }

        /// <summary>
        /// Destroys one cached asset after re-checking that nothing references it. Returns false when it
        /// must stay, in which case the entry keeps its place in the cache.
        /// </summary>
        private static bool DestroyCachedFontAt(int index)
        {
            if (index < 0 || index >= LoadedFonts.Count)
            {
                return false;
            }

            TMP_FontAsset asset = LoadedFonts[index];
            if (asset == null)
            {
                RemoveCachedFontEntry(index);
                return true;
            }

            Font unityFont = index < LoadedUnityFonts.Count ? LoadedUnityFonts[index] : null;
            if (asset == PrimaryFontAsset || IsFontReferenced(asset, unityFont))
            {
                return false;
            }

            string name = asset.name;
            int samplingSize = index < LoadedFontSizes.Count ? LoadedFontSizes[index] : 0;

            // Drop every fallback reference first, then the textures, the material, the asset and
            // its source font.
            RemoveFontFromFallbacks(asset);
            RemoveCachedFontEntry(index);

            try
            {
                DestroyFontAssetObjects(asset, unityFont);

                Log.LogInfo("Released cached font asset '" + name + "' (sampling " + samplingSize + ").");
                return true;
            }
            catch (Exception ex)
            {
                Log.LogWarning("Releasing cached font asset '" + name + "' failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>True when a live text, input field, fallback table or snapshot still uses the asset.</summary>
        private static bool IsFontReferenced(TMP_FontAsset asset, Font unityFont = null)
        {
            if (asset == null)
            {
                return false;
            }

            TMP_Text[] texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text text = texts[i];
                if (text == null)
                {
                    continue;
                }

                if (text.font == asset || text.fontSharedMaterial == asset.material)
                {
                    return true;
                }

                // A game font may not show up in Resources.FindObjectsOfTypeAll (it can be loaded
                // from a bundle), so its fallback table is invisible to the TMP_FontAsset scan
                // below. The live text still holds that font: check the table directly, otherwise
                // TrimFontProfiles would destroy a profile the settings panel is still using.
                List<TMP_FontAsset> liveFallbacks = text.font != null
                    ? text.font.fallbackFontAssetTable
                    : null;
                if (liveFallbacks != null && liveFallbacks.Contains(asset))
                {
                    return true;
                }

                // A laid-out text can still hold glyphs from the asset after the fallback hook was
                // removed. Destroying the atlas then blanks the row until something forces a
                // re-layout, which is exactly what the settings panel showed after a weight switch.
                TMP_TextInfo info = text.textInfo;
                if (info != null && info.characterInfo != null)
                {
                    int count = Mathf.Min(info.characterCount, info.characterInfo.Length);
                    for (int c = 0; c < count; c++)
                    {
                        if (info.characterInfo[c].fontAsset == asset)
                        {
                            return true;
                        }
                    }
                }
            }

            TMP_InputField[] inputFields = Resources.FindObjectsOfTypeAll<TMP_InputField>();
            for (int i = 0; i < inputFields.Length; i++)
            {
                TMP_InputField inputField = inputFields[i];
                if (inputField != null && inputField.fontAsset == asset)
                {
                    return true;
                }
            }

            // Legacy labels carry the Unity Font behind the TMP asset, not the asset itself.
            if (unityFont != null)
            {
                UnityEngine.UI.Text[] legacyTexts = Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>();
                for (int i = 0; i < legacyTexts.Length; i++)
                {
                    UnityEngine.UI.Text legacy = legacyTexts[i];
                    if (legacy != null && legacy.font == unityFont)
                    {
                        return true;
                    }
                }
            }

            List<TMP_FontAsset> globalFallbacks = TMP_Settings.fallbackFontAssets;
            if (globalFallbacks != null && globalFallbacks.Contains(asset))
            {
                return true;
            }

            TMP_FontAsset[] assets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            for (int i = 0; i < assets.Length; i++)
            {
                TMP_FontAsset other = assets[i];
                if (other != null && other != asset && other.fallbackFontAssetTable != null &&
                    other.fallbackFontAssetTable.Contains(asset))
                {
                    return true;
                }
            }

            foreach (OriginalTextState state in OriginalTmpStates.Values)
            {
                if (state.Font == asset)
                {
                    return true;
                }
            }

            foreach (CjkOverrideState state in CjkOverrides.Values)
            {
                if (state.Profile == asset ||
                    (state.AppliedMaterial != null && state.AppliedMaterial == asset.material))
                {
                    return true;
                }
            }

            foreach (TMP_FontAsset inputOriginal in OriginalInputFonts.Values)
            {
                if (inputOriginal == asset)
                {
                    return true;
                }
            }

            return false;
        }

        private static void RemoveFontFromFallbacks(TMP_FontAsset asset)
        {
            List<TMP_FontAsset> globalFallbacks = TMP_Settings.fallbackFontAssets;
            if (globalFallbacks != null)
            {
                globalFallbacks.Remove(asset);
            }

            TMP_FontAsset[] assets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            for (int i = 0; i < assets.Length; i++)
            {
                TMP_FontAsset other = assets[i];
                if (other != null && other != asset && other.fallbackFontAssetTable != null)
                {
                    other.fallbackFontAssetTable.Remove(asset);
                }
            }

            // The same live-font gap as in IsFontReferenced: a font loaded from a bundle may not be
            // in Resources, but its fallback table still holds the asset being released.
            TMP_Text[] texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            for (int i = 0; i < texts.Length; i++)
            {
                TMP_Text text = texts[i];
                if (text != null && text.font != null && text.font.fallbackFontAssetTable != null)
                {
                    text.font.fallbackFontAssetTable.Remove(asset);
                }
            }
        }

        /// <summary>Drops one entry from the four parallel lists and forgets the asset.</summary>
        private static void RemoveCachedFontEntry(int index)
        {
            if (index < 0 || index >= LoadedFonts.Count)
            {
                return;
            }

            TMP_FontAsset removed = LoadedFonts[index];
            KnownFonts.Remove(removed);
            NativeMetricsAligned.Remove(removed);
            OriginalFaceInfo.Remove(removed);
            LoadedFonts.RemoveAt(index);

            if (index < LoadedUnityFonts.Count)
            {
                KnownUnityFonts.Remove(LoadedUnityFonts[index]);
                LoadedUnityFonts.RemoveAt(index);
            }

            if (index < LoadedFontPaths.Count)
            {
                LoadedFontPaths.RemoveAt(index);
            }

            if (index < LoadedFontSizes.Count)
            {
                LoadedFontSizes.RemoveAt(index);
            }

            if (index < LoadedFontStamps.Count)
            {
                LoadedFontStamps.RemoveAt(index);
            }
        }

        private static void ReleaseGlobalFallbacks()
        {
            List<TMP_FontAsset> globalFallbacks = TMP_Settings.fallbackFontAssets;
            if (globalFallbacks == null)
            {
                return;
            }

            for (int i = globalFallbacks.Count - 1; i >= 0; i--)
            {
                if (KnownFonts.Contains(globalFallbacks[i]))
                {
                    globalFallbacks.RemoveAt(i);
                }
            }
        }

        /// <summary>Removes our fonts from every fallback table, so disabling leaves no trace.</summary>
        private static void ReleaseExternalFallbacks()
        {
            ReleaseGlobalFallbacks();

            try
            {
                TMP_FontAsset[] assets = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                for (int i = 0; i < assets.Length; i++)
                {
                    TMP_FontAsset asset = assets[i];
                    if (asset == null || asset.fallbackFontAssetTable == null)
                    {
                        continue;
                    }

                    List<TMP_FontAsset> table = asset.fallbackFontAssetTable;
                    for (int j = table.Count - 1; j >= 0; j--)
                    {
                        if (KnownFonts.Contains(table[j]))
                        {
                            table.RemoveAt(j);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("Fallback cleanup failed: " + ex.Message);
            }

            // Keep the hook bookkeeping in sync: the tables above already lost our fonts, so a later
            // UnhookFontFallbacks must not treat these fonts as still hooked (it would restore a null
            // table that was not null to begin with).
            foreach (TMP_FontAsset font in HookedFonts)
            {
                if (font == null)
                {
                    continue;
                }

                List<TMP_FontAsset> table = font.fallbackFontAssetTable;
                if (table != null && table.Count == 0 && HookedFontsWithoutTable.Contains(font))
                {
                    font.fallbackFontAssetTable = null;
                }
            }

            HookedFonts.Clear();
            HookedFontsWithoutTable.Clear();
        }

        private static string DescribePrimaryFont()
        {
            return PrimaryFontAsset != null ? PrimaryFontAsset.name : "<none>";
        }

        private static void ResetSeenState()
        {
            SeenTexts.Clear();
            SeenLegacyTexts.Clear();
            SeenInputFields.Clear();
            DecisionLogCache.Clear();
            OriginalAssetLookup.Clear();
            CjkScanCache.Clear();
            TextPipelineHooks.ClearQueue();
        }

        private static TMP_FontAsset GetPrimaryFontAsset()
        {
            // Retrying a failing selection for every text would mean one directory scan per text, so a
            // failed resolve waits a few seconds before the periodic scans try again. An explicit
            // selection or rebuild calls ResolvePrimaryFont directly and is not delayed.
            if (PrimaryFontAsset == null && LoadedFonts.Count > 0 &&
                Time.unscaledTime >= s_nextPrimaryResolveTime)
            {
                ResolvePrimaryFont();
                if (PrimaryFontAsset == null)
                {
                    s_nextPrimaryResolveTime = Time.unscaledTime + 5f;
                }
            }

            return PrimaryFontAsset;
        }
    }
}
