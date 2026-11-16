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
using UnityEngine.TextCore.LowLevel;

namespace CuFontLoader
{
    [BepInPlugin("fenchen.cu-font-loader", "CU Font Loader", "1.5.1")]
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

        private static float s_nextScanTime;

        /// <summary>Monotonic counter used as the LRU stamp of a cached font asset.</summary>
        private static int s_fontUseCounter;

        /// <summary>Earliest time the primary font may be re-resolved after a failed attempt.</summary>
        private static float s_nextPrimaryResolveTime;

        /// <summary>Throttled reporting of scan-pass failures (see LogPassFailure).</summary>
        private static int s_passFailureCount;
        private static string s_passFailureSignature;
        private static float s_nextFailureLogTime;

        /// <summary>Modes exposed in the in-game panel; legacy values are migrated on load.</summary>
        internal static readonly FontReplaceMode[] PanelModes =
        {
            FontReplaceMode.FallbackOnly,
            FontReplaceMode.Safe,
            FontReplaceMode.Normal,
            FontReplaceMode.Aggressive
        };

        private static readonly List<string> LoadedFontPaths = new List<string>();

        /// <summary>Sampling size each LoadedFonts entry was built at; parallel to LoadedFontPaths.</summary>
        private static readonly List<int> LoadedFontSizes = new List<int>();

        /// <summary>LRU stamp of each LoadedFonts entry; parallel to LoadedFontPaths.</summary>
        private static readonly List<int> LoadedFontStamps = new List<int>();
        private static readonly HashSet<TMP_FontAsset> KnownFonts = new HashSet<TMP_FontAsset>();

        /// <summary>
        /// Every Unity Font this plugin ever created. Unlike LoadedUnityFonts it is never cleared, so a
        /// text carrying an older plugin font is still recognised as ours after a font switch or rebuild.
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
            public Material Material;
        }

        public enum FontReplaceMode
        {
            FallbackOnly,
            ReplaceOnce,
            Persistent,
            Safe,
            Normal,
            Aggressive
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
                "字体替换总开关。关闭后不替换任何文本，并还原本插件已替换的字体与材质；重新打开立即生效。"
            );

            ReplaceAllText = Config.Bind(
                "General",
                "ReplaceAllText",
                false,
                "已废弃。这是旧版本的开关，现在不再产生任何作用，保留只是为了不破坏旧配置文件。请使用 ReplaceMode。"
            );

            ReplaceMode = Config.Bind(
                "General",
                "ReplaceMode",
                FontReplaceMode.Normal,
                "FallbackOnly = 只把外部字体加入 TMP fallback，不主动替换游戏字体（最安全）。 " +
                "Safe = 只替换普通文本，跳过明显使用 Sprite、特殊字体或自定义材质的文本。 " +
                "Normal = 替换大多数文本，同时保留其他 Mod 的自定义 UI 和材质。 " +
                "Aggressive = 接近旧版 Persistent 行为，反复强制替换所有文本。 " +
                "旧版 ReplaceOnce / Persistent 会在首次启动时自动迁移为 Normal / Aggressive。"
            );

            ScanIntervalSeconds = Config.Bind(
                "General",
                "ScanIntervalSeconds",
                1.0f,
                "Safe / Normal / Aggressive 模式下，扫描新 TMP 文本的间隔秒数。不建议低于 0.25。"
            );

            PrimaryFont = Config.Bind(
                "General",
                "PrimaryFont",
                "",
                "已废弃。该键不再影响字体选择，保留只是为了不破坏旧配置文件。" +
                "字体选择请使用 FontFile（字体文件）或游戏内面板。"
            );

            FontFile = Config.Bind(
                "General",
                "FontFile",
                "",
                "“字体文件”选中的本地字体（文件名，含扩展名，匹配 fonts 文件夹，不区分大小写）。" +
                "“系统字体优先”关闭时生效；留空则不替换任何文本，选中后立即应用。" +
                "文件不存在时会自动清空；文件存在但加载失败时保留选择，等下次选中或重建字体资产时重试。"
            );

            SystemFont = Config.Bind(
                "General",
                "SystemFont",
                "",
                "当前选择的系统字体（文件名，不含扩展名），由游戏内面板写入。" +
                "只在“系统字体优先”开启时生效，其余情况下即使有值也不使用。"
            );

            SystemFontPriority = Config.Bind(
                "General",
                "SystemFontPriority",
                false,
                "系统字体优先。开启时若已选择系统字体，则系统字体覆盖 fonts 文件夹中的字体文件；关闭时字体文件优先。"
            );

            AtlasSize = Config.Bind(
                "Font",
                "AtlasSize",
                4096,
                "外部 TMP 动态字体图集大小。中文字符较多，较大的图集可以减少回退到游戏原字体的情况。"
            );

            SamplingPointSize = Config.Bind(
                "Font",
                "SamplingPointSize",
                36,
                "创建 TMP 字体资产时使用的采样字号。"
            );

            AtlasPadding = Config.Bind(
                "Font",
                "AtlasPadding",
                5,
                "创建 TMP 字体资产时使用的字形间距。"
            );

            MaxCachedSamplingSizes = Config.Bind(
                "Font",
                "MaxCachedSamplingSizes",
                3,
                "每个字体文件最多缓存多少个采样字号（最近使用的优先保留）。0 = 不限制，等同于不释放旧图集。" +
                "超出时会在一次完整重新应用之后销毁最久未用的那套资产（含图集纹理与字体数据），" +
                "再次切回该字号会重新解析文件。"
            );

            PreserveCustomMaterials = Config.Bind(
                "Compatibility",
                "PreserveCustomMaterials",
                true,
                "保护其他 Mod 的自定义材质。开启时，Safe / Normal 模式会整体跳过使用非默认材质（描边、辉光、自定义 Shader 等）的文本。" +
                "Aggressive 模式始终恢复旧版强制行为；关闭后，Safe / Normal 也会替换这些文本的字体和材质。"
            );

            ExcludedFontNames = Config.Bind(
                "Compatibility",
                "ExcludedFontNames",
                "",
                "不替换使用这些字体（FontAsset 名称）的文本。逗号分隔，不区分大小写，名称包含关键字即命中。"
            );

            ExcludedMaterialNames = Config.Bind(
                "Compatibility",
                "ExcludedMaterialNames",
                "",
                "不替换使用这些材质（材质名称）的文本。逗号分隔，不区分大小写，名称包含关键字即命中。"
            );

            ExcludedObjectNames = Config.Bind(
                "Compatibility",
                "ExcludedObjectNames",
                "",
                "不替换这些 GameObject 名称的 TMP / 旧版文本。逗号分隔，不区分大小写，名称包含关键字即命中。"
            );

            ExcludedFontNames.SettingChanged += OnExclusionSettingChanged;
            ExcludedMaterialNames.SettingChanged += OnExclusionSettingChanged;
            ExcludedObjectNames.SettingChanged += OnExclusionSettingChanged;
            RefreshExclusionCache();

            LogTextDetails = Config.Bind(
                "Debug",
                "LogTextDetails",
                false,
                "调试用。开启后会在日志中输出 TMP 文本对象名称、激活状态、字体、材质和文本片段。"
            );

            MaxLoggedTexts = Config.Bind(
                "Debug",
                "MaxLoggedTexts",
                80,
                "LogTextDetails 开启时，每次字体扫描最多输出多少个 TMP 文本对象。"
            );

            CompatibilityLog = Config.Bind(
                "Debug",
                "CompatibilityLog",
                false,
                "兼容性调试日志。开启后，文本被替换或跳过时会输出一行决策记录（场景、对象名、字体、材质、原因）。" +
                "同一对象的同一决策只记录一次，不会持续刷屏。排查其他 Mod UI 被误伤时开启。"
            );

            MaxCompatibilityLogs = Config.Bind(
                "Debug",
                "MaxCompatibilityLogs",
                200,
                "CompatibilityLog 开启时，本次游戏会话最多输出多少条决策记录，防止日志过大。"
            );

            MigrateLegacyReplaceMode();
            replacementSuspended = !Enabled.Value;
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

            // The primary font is resolved here rather than gating on LoadedFonts.Count: with fonts loaded
            // on demand, a selection whose file was locked during startup still gets retried (the lookups
            // are throttled in GetPrimaryFontAsset), and an empty selection costs one null check.
            if (!IsReplacementEnabled() || GetPrimaryFontAsset() == null ||
                GetEffectiveMode() == FontReplaceMode.FallbackOnly)
            {
                return;
            }

            float interval = Mathf.Max(0.25f, ScanIntervalSeconds.Value);
            if (Time.unscaledTime < s_nextScanTime)
            {
                return;
            }

            s_nextScanTime = Time.unscaledTime + interval;
            ApplyToLoadedText(false);
            ApplyToLoadedLegacyText(false);
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

        private static FontReplaceMode GetEffectiveMode()
        {
            switch (ReplaceMode.Value)
            {
                case FontReplaceMode.ReplaceOnce:
                    return FontReplaceMode.Normal;
                case FontReplaceMode.Persistent:
                    return FontReplaceMode.Aggressive;
                default:
                    return ReplaceMode.Value;
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

        private static TMP_FontAsset LoadFont(string fontPath)
        {
            try
            {
                float startedAt = Time.realtimeSinceStartup;
                Log.LogInfo("Loading: " + Path.GetFileName(fontPath));
                Font dynamicFont = new Font(fontPath);
                int atlasSize = Mathf.Clamp(AtlasSize.Value, 512, 8192);
                int samplingPointSize = CurrentSamplingSize();
                int atlasPadding = Mathf.Clamp(AtlasPadding.Value, 1, 16);
                TMP_FontAsset fontAsset = TMP_FontAsset.CreateFontAsset(
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

            // Each stage has its own exception boundary: one bad object must not abort the rest of the
            // pass, and failures are logged with a throttle instead of once per scan.
            int chainedFallbacks = 0;
            try
            {
                chainedFallbacks = RebuildFallbackChain();
            }
            catch (Exception ex)
            {
                LogPassFailure("fallback chain", ex);
            }

            TMP_Text[] allTexts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            int replaced = 0;
            int skipped = 0;
            int menuTexts = 0;
            int menuOurs = 0;

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

            try
            {
                foreach (TMP_Text text in allTexts)
                {
                    if (text == null)
                    {
                        continue;
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
                    if (!enforceRepeatedly && !firstSeen)
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

            if (LogTextDetails.Value)
            {
                LogTextSnapshot(allTexts);
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

            int patched = 0;
            FontReplaceMode mode = GetEffectiveMode();
            bool enforceRepeatedly = mode == FontReplaceMode.Aggressive;

            TMP_InputField[] inputFields = Resources.FindObjectsOfTypeAll<TMP_InputField>();
            foreach (TMP_InputField inputField in inputFields)
            {
                if (inputField == null)
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

                if ((ExcludedObjectPatterns.Count > 0 &&
                        MatchesExclusion(text.gameObject.name, ExcludedObjectPatterns)) ||
                    (ExcludedFontPatterns.Count > 0 &&
                        MatchesExclusion(text.font.name, ExcludedFontPatterns)))
                {
                    LogLegacySkipped(text, "Excluded");
                    RestoreLegacyFontIfOurs(text);
                    continue;
                }

                bool firstSeen = !SeenLegacyTexts.Contains(text);
                if (!enforceRepeatedly && !firstSeen)
                {
                    continue;
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
        /// lacks. The game's own font assets are the only fallback, which is what makes Chinese render
        /// in the vanilla font when the selected font has no CJK glyphs. The primary is also kept in
        /// the global list, which is what FallbackOnly mode relies on. Returns how many entries changed.
        /// </summary>
        private static int RebuildFallbackChain()
        {
            int changed = 0;

            List<TMP_FontAsset> globalFallbacks = TMP_Settings.fallbackFontAssets;
            if (globalFallbacks != null)
            {
                for (int i = globalFallbacks.Count - 1; i >= 0; i--)
                {
                    if (KnownFonts.Contains(globalFallbacks[i]))
                    {
                        globalFallbacks.RemoveAt(i);
                        changed++;
                    }
                }

                if (PrimaryFontAsset != null && !globalFallbacks.Contains(PrimaryFontAsset))
                {
                    globalFallbacks.Add(PrimaryFontAsset);
                    changed++;
                }
            }

            List<TMP_FontAsset> originals = CollectOriginalFonts();
            foreach (TMP_FontAsset font in KnownFonts)
            {
                if (font == null)
                {
                    continue;
                }

                List<TMP_FontAsset> table = font.fallbackFontAssetTable;
                if (table == null)
                {
                    if (originals.Count == 0)
                    {
                        continue;
                    }

                    table = new List<TMP_FontAsset>();
                    font.fallbackFontAssetTable = table;
                }
                else if (SameFallbackChain(table, originals))
                {
                    continue;
                }

                // Drop every plugin font and stale copies of the originals, then append in a fixed order.
                int before = table.Count;
                table.RemoveAll(entry => entry == null || KnownFonts.Contains(entry) || originals.Contains(entry));
                table.AddRange(originals);
                if (table.Count != before)
                {
                    changed++;
                }
            }

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

        /// <summary>The game's own font assets, taken from the snapshots and sorted for a stable chain.</summary>
        private static List<TMP_FontAsset> CollectOriginalFonts()
        {
            List<TMP_FontAsset> originals = new List<TMP_FontAsset>();
            foreach (OriginalTextState state in OriginalTmpStates.Values)
            {
                TMP_FontAsset original = ResolveFontAsset(state.FontName, state.Font, OriginalAssetLookup);
                if (original != null && !KnownFonts.Contains(original) && !originals.Contains(original))
                {
                    originals.Add(original);
                }
            }

            originals.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return originals;
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
                OriginalTextState original = default(OriginalTextState);
                original.Font = currentFont;
                original.FontName = currentFont.name;
                original.Material = currentMaterial;
                OriginalTmpStates[text] = original;
            }
            else if (!OriginalTmpStates.ContainsKey(text))
            {
                // First sighting already on our font (a clone of a replaced prefab): remember the game
                // default, so a restore has a target instead of keeping our font.
                TMP_FontAsset gameDefault = TMP_Settings.defaultFontAsset;
                if (gameDefault != null)
                {
                    OriginalTextState fallback = default(OriginalTextState);
                    fallback.Font = gameDefault;
                    fallback.FontName = gameDefault.name;
                    fallback.Material = gameDefault.material;
                    OriginalTmpStates[text] = fallback;
                }
            }

            FontReplaceMode mode = GetEffectiveMode();
            bool replaceMaterial =
                currentMaterial == null ||
                IsOurMaterial(currentMaterial, currentFont) ||
                !PreserveCustomMaterials.Value ||
                mode == FontReplaceMode.Aggressive;

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
        /// Picks the primary font. With SystemFontPriority on, only the configured system font is
        /// used; with it off, only the configured local font file is used. Nothing selected means
        /// nothing is applied: the plugin stays idle instead of silently picking the first file. A
        /// selection whose file is gone is cleared; a file that exists but fails to load is kept,
        /// because that failure is usually temporary (file locked, download in progress).
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
        /// </summary>
        private static void ClearMissingFontSelection(string wanted, bool systemPreferred)
        {
            ReleaseExternalFallbacks();

            if (systemPreferred)
            {
                SystemFont.Value = string.Empty;
                SaveConfig();
                Log.LogWarning("System font '" + wanted + "' was not found; SystemFont cleared.");
                return;
            }

            FontFile.Value = string.Empty;
            SaveConfig();
            Log.LogWarning("Font file '" + wanted + "' was not found in " + FontsDir +
                "; FontFile cleared.");
        }

        private static void SetPrimaryFont(int index)
        {
            PrimaryFontAsset = LoadedFonts[index];
            PrimaryUnityFont = index < LoadedUnityFonts.Count ? LoadedUnityFonts[index] : null;
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
        /// Clears the local font choice (the panel's "not selected" entry). Re-applies immediately, which
        /// means reverting every replaced text to the game's own font when nothing else is selected.
        /// Returns true when nothing is left to apply, so the caller can rebuild its UI from prefabs.
        /// </summary>
        internal static bool ClearPluginFontSelection()
        {
            if (!string.IsNullOrEmpty(FontFile.Value))
            {
                FontFile.Value = string.Empty;
                SaveConfig();
            }

            ResolvePrimaryFont();
            if (PrimaryFontAsset == null)
            {
                // Nothing selected: our fonts must not stay behind as fallbacks either.
                ReleaseExternalFallbacks();
            }

            RefreshAllTexts();
            Log.LogInfo("Font file selection cleared (" + DescribePrimaryFont() + ").");
            return PrimaryFontAsset == null;
        }

        /// <summary>True when the asset was created by this plugin, including pre-rebuild assets.</summary>
        private static bool IsKnownFont(TMP_FontAsset font)
        {
            return font != null && KnownFonts.Contains(font);
        }

        /// <summary>
        /// Returns the original asset, or looks it up by name when it has been unloaded since the
        /// snapshot was taken. Returns null when it is gone for good. Results are memoized because a
        /// single restore pass may resolve the same name for many texts.
        /// </summary>
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
            // RefreshAllTexts reverts the screen itself when the new mode is FallbackOnly.
            ResolvePrimaryFont();
            RefreshAllTexts();
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
            if (OriginalTmpStates.Count == 0 && OriginalLegacyFonts.Count == 0 && OriginalInputFonts.Count == 0)
            {
                return;
            }

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
                        state.Font = text.font;
                        state.FontName = text.font.name;
                        state.Material = text.fontSharedMaterial;
                        OriginalTmpStates[text] = state;
                    }

                    continue;
                }

                TMP_FontAsset original = ResolveFontAsset(state.FontName, state.Font, resolved);
                if (original != null && KnownFonts.Contains(original))
                {
                    // Poisoned snapshot (one of our fonts): revert to the game default instead.
                    original = TMP_Settings.defaultFontAsset;
                }

                if (original == null)
                {
                    unresolved++;
                    continue;
                }

                text.font = original;
                if (state.Material != null)
                {
                    text.fontSharedMaterial = state.Material;
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

            // Input fields are found by scanning, not by the snapshot keys: a field that was already
            // carrying one of our fonts when it was first seen has no snapshot of its own.
            int inputRestored = 0;
            TMP_InputField[] inputFields = Resources.FindObjectsOfTypeAll<TMP_InputField>();
            for (int i = 0; i < inputFields.Length; i++)
            {
                TMP_InputField inputField = inputFields[i];
                if (inputField == null || !IsKnownFont(inputField.fontAsset))
                {
                    continue;
                }

                TMP_FontAsset original;
                if (!OriginalInputFonts.TryGetValue(inputField, out original) || original == null)
                {
                    // No usable snapshot: fall back to the game's default font instead of leaving ours.
                    original = TMP_Settings.defaultFontAsset;
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

            Log.LogInfo("Original fonts restored: " + restored + " TMP text(s), " + legacyRestored +
                " legacy text(s), " + inputRestored + " input field(s)" +
                (unresolved > 0
                    ? "; " + unresolved + " entry(ies) could not be resolved to a game font and were left as they are"
                    : string.Empty) + ".");
        }

        /// <summary>
        /// Re-evaluates every loaded text, e.g. after a mode or font change. With no font left to apply
        /// (selected file deleted, nothing loaded) the game's fonts are restored instead of leaving the
        /// last replacement on screen.
        /// </summary>
        internal static void RefreshAllTexts()
        {
            ResetSeenState();
            if (!IsReplacementEnabled())
            {
                TrimFontCache();
                return;
            }

            if (GetPrimaryFontAsset() == null)
            {
                RestoreOriginalFonts();
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
        /// Reverts one text to its snapshotted font when it still carries ours but no longer qualifies
        /// (stricter mode, new exclusion, FallbackOnly). The snapshot is kept so it can be re-applied.
        /// The cache memoizes name→asset lookups across one pass.
        /// </summary>
        private static void RestoreTextFontIfOurs(TMP_Text text, Dictionary<string, TMP_FontAsset> cache)
        {
            if (text == null || !IsKnownFont(text.font))
            {
                return;
            }

            OriginalTextState state;
            if (!OriginalTmpStates.TryGetValue(text, out state))
            {
                return;
            }

            TMP_FontAsset original = ResolveFontAsset(state.FontName, state.Font, cache);
            if (original != null && KnownFonts.Contains(original))
            {
                // Poisoned snapshot (one of our fonts): use the game default instead.
                original = TMP_Settings.defaultFontAsset;
            }

            if (original == null)
            {
                return;
            }

            text.font = original;
            if (state.Material != null)
            {
                text.fontSharedMaterial = state.Material;
            }

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
            if (OriginalLegacyFonts.TryGetValue(text, out original) && original != null)
            {
                text.font = original;
            }

            SeenLegacyTexts.Remove(text);
        }

        /// <summary>Same as RestoreTextFontIfOurs for input fields, with the game default as fallback.</summary>
        private static void RestoreInputFontIfOurs(TMP_InputField inputField)
        {
            if (inputField == null || !IsKnownFont(inputField.fontAsset))
            {
                return;
            }

            TMP_FontAsset original;
            if (!OriginalInputFonts.TryGetValue(inputField, out original) || original == null)
            {
                original = TMP_Settings.defaultFontAsset;
            }

            if (original != null)
            {
                inputField.fontAsset = original;
            }

            SeenInputFields.Remove(inputField);
        }

        /// <summary>
        /// Called after the fonts folder was rescanned. Clears a selection whose file disappeared and
        /// always re-resolves and re-applies: removing every file must revert the texts to the game's
        /// fonts, and that has to cover all scenes and tabs, not only what is on screen. Returns true
        /// when a previously applied font went away, so the caller can rebuild its UI from prefabs.
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
            RefreshAllTexts();

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

            if (EnsureFontAsset(path) < 0)
            {
                return false;
            }

            string fileName = Path.GetFileName(path);
            FontFile.Value = fileName;
            SaveConfig();
            ResolvePrimaryFont();
            RefreshAllTexts();
            Log.LogInfo("Selected font file: " + fileName +
                (SystemFontPriority.Value
                    ? " (recorded; SystemFontPriority is on, keeping " + DescribePrimaryFont() + ")"
                    : " (" + DescribePrimaryFont() + ")"));
            return true;
        }

        /// <summary>
        /// Records the system font selection. It is used only while SystemFontPriority is on; with the
        /// switch off the local font file stays in charge.
        /// </summary>
        internal static bool SelectSystemFont(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            string name = Path.GetFileNameWithoutExtension(path);
            SystemFont.Value = name;
            SaveConfig();

            if (SystemFontPriority.Value)
            {
                FailedFontPaths.Remove(path);

                if (EnsureFontAsset(path) < 0)
                {
                    return false;
                }

                ResolvePrimaryFont();
                RefreshAllTexts();
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
            string[] labels;
            List<string> paths = GetWeightCandidates(out labels);
            if (index < 0 || index >= paths.Count)
            {
                return false;
            }

            return SystemFontPriority.Value ? SelectSystemFont(paths[index]) : SelectPluginFont(paths[index]);
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
        /// ordered light to heavy. Empty when nothing is selected or the family has one member only.
        /// </summary>
        private static List<string> GetWeightCandidates(out string[] labels)
        {
            labels = new string[0];

            bool system = SystemFontPriority.Value;
            string current = system ? SystemFont.Value : FontFile.Value;
            if (string.IsNullOrEmpty(current))
            {
                return new List<string>();
            }

            List<string> source = system ? ScanSystemFonts() : ScanFontFiles(FontsDir);
            string family = null;
            for (int i = 0; i < source.Count; i++)
            {
                if (!NameMatches(source[i], current))
                {
                    continue;
                }

                string weight;
                ExtractWeight(Path.GetFileNameWithoutExtension(source[i]), out weight, out family);
                break;
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
                    string weight;
                    string fileFamily;
                    ExtractWeight(Path.GetFileNameWithoutExtension(source[i]), out weight, out fileFamily);

                    if (!string.Equals(fileFamily, family, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(weight, WeightOrder[w], StringComparison.OrdinalIgnoreCase))
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
                RefreshAllTexts();
            }
            else
            {
                RestoreOriginalFonts();
                ReleaseExternalFallbacks();
                ResetSeenState();
            }

            Log.LogInfo("Font replacement " + (value ? "enabled" : "disabled") +
                " (settings panel); cfg=" + Enabled.Value + ", suspended=" + replacementSuspended);
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
            RefreshAllTexts();
            Log.LogInfo("SystemFontPriority = " + value + " (" + DescribePrimaryFont() + ")");
            return true;
        }

        /// <summary>Rebuilds (or reuses) the font in effect at the new sampling point size and re-applies.</summary>
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
            RefreshAllTexts();
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

                    if (victim < 0 || IsFontReferenced(LoadedFonts[victim]))
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

            if (asset == PrimaryFontAsset || IsFontReferenced(asset))
            {
                return false;
            }

            Font unityFont = index < LoadedUnityFonts.Count ? LoadedUnityFonts[index] : null;
            string name = asset.name;
            int samplingSize = index < LoadedFontSizes.Count ? LoadedFontSizes[index] : 0;

            // Drop every fallback reference first, then the textures, the asset and its source font.
            RemoveFontFromFallbacks(asset);
            RemoveCachedFontEntry(index);

            try
            {
                Texture2D[] atlasTextures = asset.atlasTextures;
                if (atlasTextures != null)
                {
                    for (int i = 0; i < atlasTextures.Length; i++)
                    {
                        if (atlasTextures[i] != null)
                        {
                            Destroy(atlasTextures[i]);
                        }
                    }
                }

                Destroy(asset);
                if (unityFont != null)
                {
                    Destroy(unityFont);
                }

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
        private static bool IsFontReferenced(TMP_FontAsset asset)
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
        }

        /// <summary>Drops one entry from the four parallel lists and forgets the asset.</summary>
        private static void RemoveCachedFontEntry(int index)
        {
            if (index < 0 || index >= LoadedFonts.Count)
            {
                return;
            }

            if (index < LoadedFonts.Count)
            {
                KnownFonts.Remove(LoadedFonts[index]);
                LoadedFonts.RemoveAt(index);
            }

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
