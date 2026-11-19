// cu-font-loader - in-game options in the game's Language tab.
// Licensed under MIT License
// https://github.com/FenChen0211/cu-font-loader
//
// CUCoreLib is an optional runtime dependency: it is resolved by reflection only, never at
// build time. The cfg file stays the single source of truth; the panel is a front end for it.
//
// Degradation:
//   CUCoreLib missing or incompatible -> no panel, cfg only (unchanged behaviour).
//   Option registration rejected      -> that option is absent, the others stay usable.
//   Invalid value from the panel      -> ignored, current state is kept.
//   Settings being loaded             -> panel callbacks are dropped so cfg wins.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace CuFontLoader
{
    /// <summary>
    /// Reflection bridge to CUCoreLib's mod option API (CUCoreLib.Data.ModOptionDefinition and
    /// CUCoreLib.Registries.ModOptionsRegistry). All calls fail closed: a missing assembly, a
    /// renamed method or a rejected option only disables the panel, never the plugin.
    /// </summary>
    internal static class CoreLibOptions
    {
        private static bool resolved;
        private static bool available;
        private static MethodInfo registerMethod;
        private static MethodInfo boolFactory;
        private static MethodInfo dropdownFactory;
        private static MethodInfo localeRegister;
        private static int localeOtherCategory;
        private static Type choiceType;

        internal static bool IsAvailable
        {
            get
            {
                Resolve();
                return available;
            }
        }

        private static void Resolve()
        {
            if (resolved)
            {
                return;
            }

            resolved = true;

            try
            {
                Type registry = FindType("CUCoreLib.Registries.ModOptionsRegistry");
                Type definition = FindType("CUCoreLib.Data.ModOptionDefinition");
                Type choice = FindType("CUCoreLib.Data.ModDropdownChoice");
                if (registry == null || definition == null || choice == null)
                {
                    return;
                }

                registerMethod = registry.GetMethod("Register", new[] { definition });
                boolFactory = definition.GetMethod("Bool", new[]
                {
                    typeof(string), typeof(string), typeof(string), typeof(Setting.SettingCategory),
                    typeof(bool), typeof(Action<bool>)
                });
                dropdownFactory = definition.GetMethod("Dropdown", new[]
                {
                    typeof(string), typeof(string), typeof(string), typeof(Setting.SettingCategory),
                    typeof(int), choice.MakeArrayType(), typeof(Action<int>)
                });
                choiceType = choice;

                available = registerMethod != null && boolFactory != null && dropdownFactory != null;
                if (!available)
                {
                    Log("CUCoreLib found but its option API does not match; in-game panel disabled.");
                    return;
                }

                ResolveLocale(FindType("CUCoreLib.Registries.LocaleRegistry"));
            }
            catch (Exception ex)
            {
                available = false;
                Log("CUCoreLib option probe failed: " + ex.Message);
            }
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = assemblies[i].GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        /// <summary>Resolves a game type by name for hooks that must not bind at compile time.</summary>
        internal static Type FindTypeOrNull(string fullName)
        {
            return FindType(fullName);
        }

        /// <summary>Locale support is optional: without it new dropdown entries show their raw key.</summary>
        private static void ResolveLocale(Type localeRegistry)
        {
            if (localeRegistry == null)
            {
                return;
            }

            try
            {
                Type categoryType = localeRegistry.GetNestedType("LocaleCategory", BindingFlags.Public);
                MethodInfo register = localeRegistry.GetMethod("Register",
                    new[] { typeof(int), typeof(string), typeof(string) });
                if (categoryType == null || register == null)
                {
                    return;
                }

                object other = Enum.Parse(categoryType, "Other");
                localeRegister = register;
                localeOtherCategory = Convert.ToInt32(other);
            }
            catch (Exception)
            {
                localeRegister = null;
            }
        }

        /// <summary>Registers the display text of one dropdown choice ("gameset" + option id + key).</summary>
        internal static bool RegisterChoiceLabel(string optionId, string key, string label)
        {
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            return RegisterLocaleText("gameset" + optionId + key, label);
        }

        /// <summary>
        /// Registers (or replaces) an option's label and description. CUCoreLib reads the label from
        /// "gameset" + id and the tooltip text from "gameset" + id + "dsc" when it spawns a row, so
        /// re-registering is how the panel follows a language switch without a restart.
        /// </summary>
        internal static bool RegisterOptionText(string optionId, string label, string description)
        {
            bool labeled = RegisterLocaleText("gameset" + optionId, label);
            if (!string.IsNullOrEmpty(description))
            {
                labeled |= RegisterLocaleText("gameset" + optionId + "dsc", description);
            }

            return labeled;
        }

        private static bool RegisterLocaleText(string key, string text)
        {
            if (localeRegister == null || text == null)
            {
                return false;
            }

            try
            {
                localeRegister.Invoke(null, new object[] { localeOtherCategory, key, text });
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static bool RegisterBool(string id, string label, string description, bool value,
            Action<bool> apply)
        {
            if (!IsAvailable)
            {
                return false;
            }

            try
            {
                object definition = boolFactory.Invoke(null, new object[]
                {
                    id, label, description, Setting.SettingCategory.Language, value, apply
                });

                return RegisterDefinition(definition, id);
            }
            catch (Exception ex)
            {
                LogFailure("bool", id, ex);
                return false;
            }
        }

        internal static bool RegisterDropdown(string id, string label, string description, int index,
            string[] keys, string[] labels, Action<int> apply)
        {
            if (!IsAvailable || choiceType == null || keys == null || labels == null || keys.Length == 0 ||
                keys.Length != labels.Length)
            {
                return false;
            }

            try
            {
                Array choices = Array.CreateInstance(choiceType, keys.Length);
                for (int i = 0; i < keys.Length; i++)
                {
                    choices.SetValue(Activator.CreateInstance(choiceType, new object[] { keys[i], labels[i] }), i);
                }

                object definition = dropdownFactory.Invoke(null, new object[]
                {
                    id, label, description, Setting.SettingCategory.Language, index, choices, apply
                });

                return RegisterDefinition(definition, id);
            }
            catch (Exception ex)
            {
                LogFailure("dropdown", id, ex);
                return false;
            }
        }

        private static bool RegisterDefinition(object definition, string id)
        {
            if (definition == null)
            {
                return false;
            }

            object result = registerMethod.Invoke(null, new[] { definition });
            bool accepted = result is bool && (bool)result;
            if (!accepted)
            {
                Log("CUCoreLib rejected option '" + id + "'.");
            }

            return accepted;
        }

        private static void LogFailure(string kind, string id, Exception ex)
        {
            Exception inner = ex;
            if (ex is TargetInvocationException && ex.InnerException != null)
            {
                inner = ex.InnerException;
            }

            Log("CUCoreLib " + kind + " option '" + id + "' failed: " + inner.Message);
        }

        private static void Log(string message)
        {
            if (FontLoaderPlugin.Log != null)
            {
                FontLoaderPlugin.Log.LogWarning(message);
            }
        }
    }

    /// <summary>
    /// Decides which language the panel speaks. It follows the game's own choice the same way
    /// AutoTranslate.Together does: PlayerPrefs "locale" first, then Locale.currentLangName, then the
    /// system language. Only Chinese (simplified or traditional) gets the Chinese strings; every other
    /// language falls back to English.
    /// </summary>
    internal static class PanelLocale
    {
        private static bool cached;
        private static bool chinese;
        private static float nextProbeAt;

        internal static bool IsChinese
        {
            get
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (cached && now < nextProbeAt)
                {
                    return chinese;
                }

                nextProbeAt = now + 1f;
                chinese = Resolve();
                cached = true;
                return chinese;
            }
        }

        private static bool Resolve()
        {
            string raw = null;
            try
            {
                // The game stores its language choice here.
                raw = UnityEngine.PlayerPrefs.GetString("locale", null);
            }
            catch (Exception)
            {
                // PlayerPrefs unavailable: fall through to the game locale and the system language.
            }

            if (string.IsNullOrEmpty(raw))
            {
                raw = ReadGameLocaleName();
            }

            if (!string.IsNullOrEmpty(raw))
            {
                string lower = raw.Trim().ToLowerInvariant().Replace('_', '-');
                return lower.StartsWith("zh") || lower.Contains("chinese") || lower.Contains("中文");
            }

            try
            {
                UnityEngine.SystemLanguage language = UnityEngine.Application.systemLanguage;
                return language == UnityEngine.SystemLanguage.Chinese ||
                       language == UnityEngine.SystemLanguage.ChineseSimplified ||
                       language == UnityEngine.SystemLanguage.ChineseTraditional;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Locale.currentLangName, read by reflection because the game type may change.</summary>
        private static string ReadGameLocaleName()
        {
            try
            {
                Type locale = CoreLibOptions.FindTypeOrNull("Locale");
                if (locale == null)
                {
                    return null;
                }

                const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo property = locale.GetProperty("currentLangName", flags);
                if (property != null)
                {
                    return property.GetValue(null, null) as string;
                }

                FieldInfo field = locale.GetField("currentLangName", flags);
                if (field != null)
                {
                    return field.GetValue(null) as string;
                }
            }
            catch (Exception)
            {
            }

            return null;
        }
    }

    /// <summary>
    /// Registers the plugin options in the game's Language settings tab and keeps the panel in
    /// sync with the cfg file. Every action delegates to FontLoaderPlugin, which owns the font
    /// state and its own fallbacks; nothing here mutates game state directly.
    /// </summary>
    internal static class FontSettingsPanel
    {
        private const string OptionRoot = "fenchen.cu-font-loader.";
        private const string EnabledOption = OptionRoot + "PanelEnabled";
        private const string ModeOption = OptionRoot + "PanelMode";
        private const string SamplingSizeOption = OptionRoot + "PanelSamplingSize";
        private const string PluginFontOption = OptionRoot + "PanelPluginFont";
        private const string SystemFontOption = OptionRoot + "PanelSystemFont";
        private const string WeightOption = OptionRoot + "PanelFontWeight";
        private const string PriorityOption = OptionRoot + "PanelPriority";
        private const string PreserveNonCjkOption = OptionRoot + "PanelPreserveNonCjk";
        private const string CjkOverrideOption = OptionRoot + "PanelCjkOverride";
        private const string AlignNativeMetricsOption = OptionRoot + "PanelAlignNativeMetrics";

        /// <summary>Rows this panel registers; only used for the registration summary line.</summary>
        private const int OptionCount = 10;

        /// <summary>Bilingual UI text; PanelLocale picks the half matching the game language.</summary>
        private struct LocText
        {
            internal readonly string Zh;
            internal readonly string En;

            internal LocText(string zh, string en)
            {
                Zh = zh;
                En = en;
            }

            internal string Value
            {
                get { return PanelLocale.IsChinese ? Zh : En; }
            }
        }

        private static readonly LocText EnabledLabel = new LocText("启用字体替换", "Enable font replacement");
        private static readonly LocText EnabledDesc = new LocText(
            "总开关。关闭后不替换任何文本，并还原本插件替换过的字体、材质以及加入 TMP fallback 链的插件字体。",
            "Master switch. When off, no text is replaced; this plugin reverts the fonts and materials it changed " +
            "and removes its font from the TMP fallback chains it extended.");

        private static readonly LocText ModeLabel = new LocText("替换模式", "Replace mode");
        private static readonly LocText ModeDesc = new LocText(
            "选择文本替换方式，推荐优先使用 Normal；替换后行高或基线变化明显时改用 ScaleAware。" +
            "FallbackOnly 只把插件字体加入 TMP 回退链；Safe 跳过含 Sprite 或受保护材质的文本；" +
            "Aggressive 每轮强制替换；Consistent 在字体被游戏改回后重新应用。" +
            "开启“仅替换中日韩字形”后，除 FallbackOnly 外各模式行为相同，只把插件字体作为 CJK 字形回退。" +
            "切换后立即生效。",
            "Choose how text is replaced; Normal suits most setups, and ScaleAware replaces a noticeable shift " +
            "in line height or baseline. FallbackOnly only adds the plugin font to TMP's fallback chain; Safe " +
            "skips text with sprites or protected materials; Aggressive forces replacement every pass; " +
            "Consistent re-applies after the game resets the font. With \"CJK glyphs only\" on, every mode " +
            "except FallbackOnly behaves the same and only hooks the plugin font as a CJK fallback. Applies " +
            "immediately.");

        private static readonly LocText SamplingLabel = new LocText("采样字号", "Sampling size");
        private static readonly LocText SamplingDesc = new LocText(
            "创建字体图集时使用的采样字号，默认 {0} px，不改变界面文字的实际大小。" +
            "修改后重建当前字体的图集并重新应用；切回用过的档位会复用已建好的图集。",
            "Sampling point size used when the font atlas is built (default {0} px); it does not change the " +
            "on-screen text size. Changing it rebuilds the atlas of the font in effect and re-applies; a size " +
            "that was used before reuses its cached atlas.");

        private static readonly LocText FontFileLabel = new LocText("字体文件", "Font file");
        private static readonly LocText FontFileDesc = new LocText(
            "把 .ttf / .otf 字体文件放入 {0}，重新打开本下拉即会重新扫描；选中后立即应用。" +
            "“系统字体优先”关闭时由它接管，留空且关闭时不替换任何文本。",
            "Put .ttf / .otf font files into {0}, then reopen this dropdown to rescan. The selected font is " +
            "applied immediately; it is the source while \"System font priority\" is off, and leaving it " +
            "empty with the priority off applies no font.");

        private static readonly LocText SystemFontLabel = new LocText("系统字体", "System font");
        private static readonly LocText SystemFontDesc = new LocText(
            "Windows 已安装字体，仅在“系统字体优先”开启时作为字体来源；关闭时即使有值也不生效。" +
            "加载失败时保持当前字体不变。选择“（未选择）”清除该系统字体。",
            "Installed Windows fonts, used as the font source only while \"System font priority\" is on " +
            "(the value is ignored when it is off); a failed load keeps the current font. \"(not selected)\" " +
            "clears the choice.");

        private static readonly LocText WeightLabel = new LocText("字重选择", "Font weight");
        private static readonly LocText WeightDesc = new LocText(
            "选择当前生效字体的字重。可用项跟随当前字体来源；切换后立即重新应用。",
            "Weight of the font currently in effect. The choices follow the font source in use; " +
            "switching re-applies immediately.");

        private static readonly LocText PriorityLabel = new LocText("系统字体优先", "System font priority");
        private static readonly LocText PriorityDesc = new LocText(
            "决定字体来源：开启时用系统字体，关闭时用字体文件；所选来源未选择时不替换任何文本，由游戏原字体渲染。",
            "Selects the font source: the system font when on, the font file when off. With the selected " +
            "source empty nothing is replaced and the game's own fonts render the text.");

        private static readonly LocText PreserveNonCjkLabel = new LocText(
            "仅替换中日韩字形", "CJK glyphs only");
        private static readonly LocText PreserveNonCjkDesc = new LocText(
            "推荐开启。插件字体只补中日韩字形（中文、日文假名、韩文谚文），数字、英文、符号、材质保持原生。" +
            "此功能会导致英文字体失效，只需要英文的用户请不要开启；韩文需要所选字体本身带谚文字形。",
            "Recommended and on by default. The plugin font only supplies CJK glyphs (Chinese, Japanese kana, " +
            "Korean Hangul); digits, Latin, symbols and materials stay native. Because English text keeps the " +
            "game font, English-only users should leave this off. Korean needs a font that contains Hangul.");

        private static readonly LocText CjkOverrideLabel = new LocText(
            "中日韩字体替换白名单", "CJK font replacement whitelist");
        private static readonly LocText CjkOverrideDesc = new LocText(
            "仅“仅替换中日韩字形”开启时有效。白名单中的游戏字体（默认 ScienceUnifont、ConsoleFont）" +
            "即使自带 CJK 字形，也优先由插件字体替换 CJK 文字；数字、英文和符号仍保持原字体。" +
            "关闭该模式后，这些字体按普通文本处理，替换模式下会被整体替换。默认开启，切换立即生效。",
            "Only applies while \"CJK glyphs only\" is on. For game fonts in the whitelist (ScienceUnifont " +
            "and ConsoleFont by default), the plugin font takes over CJK text even if the source font already " +
            "contains CJK glyphs; digits, Latin and symbols stay on the source font. With the mode off, these " +
            "fonts are treated like any other text and replaced wholesale in replacement modes. On by default; " +
            "applies immediately.");

        private static readonly LocText AlignNativeMetricsLabel = new LocText(
            "行距对齐", "Align line metrics");
        private static readonly LocText AlignNativeMetricsDesc = new LocText(
            "仅“仅替换中日韩字形”开启时有效。让插件 CJK 字形的行高和基线贴近界面主要使用的游戏字体，" +
            "避免混排行距被撑高。只调整插件字体本身，不改文本字体或材质；切换立即生效。",
            "Only applies while \"CJK glyphs only\" is on. Aligns the plugin font's line height and baseline " +
            "with the game font used by most CJK text on screen, so mixed lines are not stretched. Only the " +
            "plugin font's metrics change; text fonts and materials are untouched. Applies immediately.");

        private static readonly LocText NotSelectedLabel = new LocText("（未选择）", "(not selected)");
        private static readonly LocText FontsEmptyLabel = new LocText("（fonts 文件夹为空）", "(fonts folder is empty)");
        private static readonly LocText NoSystemFontLabel = new LocText("（未找到系统字体）", "(no system font found)");
        private static readonly LocText VariableFontHint = new LocText(
            "（可变字体，仅默认字重）", " (variable font, default weight only)");

        /// <summary>Mode choice labels in the same order as FontLoaderPlugin.PanelModes.</summary>
        private static readonly LocText[] ModeLabels =
        {
            new LocText("FallbackOnly（仅回退）", "FallbackOnly (fallback only)"),
            new LocText("Safe（保守）", "Safe (conservative)"),
            new LocText("Normal（常规，推荐）", "Normal (default, recommended)"),
            new LocText("ScaleAware（跟随原字体）", "ScaleAware (match source metrics)"),
            new LocText("Consistent（自动重试）", "Consistent (retry when reset)"),
            new LocText("Aggressive（强制）", "Aggressive (forced)")
        };

        /// <summary>
        /// Sampling-size description with the effective default filled in, so the text cannot drift from
        /// the cfg default.
        /// </summary>
        private static string SamplingDescText()
        {
            return string.Format(SamplingDesc.Value, DefaultSamplingSize());
        }

        private static int DefaultSamplingSize()
        {
            try
            {
                if (FontLoaderPlugin.SamplingPointSize != null)
                {
                    return Convert.ToInt32(FontLoaderPlugin.SamplingPointSize.DefaultValue);
                }
            }
            catch (Exception)
            {
            }

            return 36;
        }

        /// <summary>Font-file description with the fonts folder filled in, relative to the game root.</summary>
        private static string FontFileDescText()
        {
            try
            {
                return string.Format(FontFileDesc.Value, FontsDirDisplay());
            }
            catch (FormatException)
            {
                return FontFileDesc.Value;
            }
        }

        /// <summary>
        /// The fonts folder as the player sees it: relative to the game root when possible, otherwise
        /// the absolute path.
        /// </summary>
        private static string FontsDirDisplay()
        {
            string dir = FontLoaderPlugin.FontsDir;
            if (string.IsNullOrEmpty(dir))
            {
                return "BepInEx\\plugins\\cu-font-loader\\fonts";
            }

            try
            {
                string root = BepInEx.Paths.GameRootPath;
                if (!string.IsNullOrEmpty(root) && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    string relative = dir.Substring(root.Length).TrimStart('\\', '/');
                    if (relative.Length > 0)
                    {
                        return relative;
                    }
                }
            }
            catch (Exception)
            {
            }

            return dir;
        }

        /// <summary>Sampling point sizes offered in the panel; glyphs are rasterised at this size.</summary>
        private static readonly int[] SamplingSizes = { 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72, 96 };

        private static string[] pluginFontPaths = new string[0];
        private static string[] pluginFontKeys = new string[0];
        private static string[] pluginFontLabels = new string[0];
        private static string[] systemFontKeys = new string[0];
        private static string[] systemFontLabels = new string[0];
        private static string[] weightKeys = new string[0];
        private static string[] weightLabels = new string[0];
        private static string[] systemFontPaths = new string[0];

        private static bool initialized;
        private static bool ready;
        private static bool loadingSettings;
        private static bool pushingToPanel;
        private static bool tabRefreshPending;
        private static bool dropdownHookInstalled;

        internal static bool DropdownHookInstalled
        {
            get { return dropdownHookInstalled; }
        }

        internal static void Initialize()
        {
            if (initialized)
            {
                return;
            }

            initialized = true;

            try
            {
                if (!CoreLibOptions.IsAvailable)
                {
                    FontLoaderPlugin.Log.LogInfo(
                        "CUCoreLib not found - in-game panel disabled, the cfg file drives all options.");
                    return;
                }

                InstallLoadGuard();

                int registered = 0;
                if (RegisterEnabledOption())
                {
                    registered++;
                }

                if (RegisterModeOption())
                {
                    registered++;
                }

                if (RegisterSamplingSizeOption())
                {
                    registered++;
                }

                if (RegisterPluginFontOption())
                {
                    registered++;
                }

                if (RegisterSystemFontOption())
                {
                    registered++;
                }

                if (RegisterPriorityOption())
                {
                    registered++;
                }

                if (RegisterWeightOption())
                {
                    registered++;
                }

                if (RegisterPreserveNonCjkOption())
                {
                    registered++;
                }

                if (RegisterCjkOverrideOption())
                {
                    registered++;
                }

                if (RegisterAlignNativeMetricsOption())
                {
                    registered++;
                }

                ready = registered > 0;
                FontLoaderPlugin.Log.LogInfo(
                    "In-game panel: " + registered + "/" + OptionCount +
                    " option(s) registered in the game's Language tab.");
            }
            catch (Exception ex)
            {
                ready = false;
                FontLoaderPlugin.Log.LogError("In-game panel registration failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Drops panel callbacks while the game loads settings.json, then restores the cfg values.
        /// Without the guard the game would push its own saved values into the cfg file.
        /// </summary>
        private static void InstallLoadGuard()
        {
            Harmony harmony = new Harmony("fenchen.cu-font-loader.panel");

            try
            {
                harmony.CreateClassProcessor(typeof(SettingsLoadGuardPatch)).Patch();
            }
            catch (Exception ex)
            {
                FontLoaderPlugin.Log.LogWarning(
                    "Settings load guard not installed, panel values may be overwritten on load: " + ex.Message);
            }

            InstallMenuSyncHooks(harmony);
        }

        /// <summary>
        /// Settings are otherwise synced only on Settings.LoadSettings. The settings menu can also be
        /// built without that call (first open), which would leave the rows showing registration-time
        /// values instead of the cfg ones. Pushing before the rows spawn and after the menu starts
        /// keeps the panel honest in every path.
        ///
        /// SettingsMenu is patched through reflection: the game type may not be public.
        /// </summary>
        private static void InstallMenuSyncHooks(Harmony harmony)
        {
            try
            {
                Type settingsMenu = CoreLibOptions.FindTypeOrNull("SettingsMenu");
                if (settingsMenu == null)
                {
                    FontLoaderPlugin.Log.LogWarning("SettingsMenu not found; panel values may stay stale.");
                    return;
                }

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                MethodInfo start = settingsMenu.GetMethod("Start", flags, null, Type.EmptyTypes, null);
                if (start != null)
                {
                    harmony.Patch(start, postfix: new HarmonyMethod(typeof(SettingsMenuHooks),
                        nameof(SettingsMenuHooks.AfterMenuStart)));
                }

                MethodInfo selectTab = settingsMenu.GetMethod("SelectTab", flags, null,
                    new[] { typeof(Setting.SettingCategory) }, null);
                if (selectTab != null)
                {
                    harmony.Patch(selectTab, prefix: new HarmonyMethod(typeof(SettingsMenuHooks),
                        nameof(SettingsMenuHooks.BeforeTabSpawn)));
                }

                if (start == null && selectTab == null)
                {
                    FontLoaderPlugin.Log.LogWarning("SettingsMenu hooks not installed; no matching methods.");
                }
            }
            catch (Exception ex)
            {
                FontLoaderPlugin.Log.LogWarning("SettingsMenu hooks not installed: " + ex.Message);
            }

            InstallFontDropdownHook(harmony);
        }

        /// <summary>
        /// The fonts folder is rescanned when the font dropdown is opened rather than on every settings
        /// visit. If the hook cannot be installed, the Language tab keeps the old eager scan as fallback.
        /// </summary>
        private static void InstallFontDropdownHook(Harmony harmony)
        {
            try
            {
                MethodInfo show = typeof(TMP_Dropdown).GetMethod("Show",
                    BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (show == null)
                {
                    FontLoaderPlugin.Log.LogInfo("TMP_Dropdown.Show not found; font list is scanned on tab select.");
                    return;
                }

                harmony.Patch(show, prefix: new HarmonyMethod(typeof(SettingsMenuHooks),
                    nameof(SettingsMenuHooks.BeforeDropdownShow)));
                dropdownHookInstalled = true;
            }
            catch (Exception ex)
            {
                FontLoaderPlugin.Log.LogWarning("Font dropdown hook not installed: " + ex.Message);
            }
        }

        private static bool RegisterEnabledOption()
        {
            return CoreLibOptions.RegisterBool(
                EnabledOption,
                EnabledLabel.Value,
                EnabledDesc.Value,
                FontLoaderPlugin.Enabled.Value,
                OnEnabledChanged);
        }

        private static bool RegisterModeOption()
        {
            FontLoaderPlugin.FontReplaceMode[] modes = FontLoaderPlugin.PanelModes;
            string[] keys = new string[modes.Length];
            string[] labels = new string[modes.Length];
            for (int i = 0; i < modes.Length; i++)
            {
                keys[i] = i.ToString();
                labels[i] = i < ModeLabels.Length ? ModeLabels[i].Value : modes[i].ToString();
            }

            return CoreLibOptions.RegisterDropdown(
                ModeOption,
                ModeLabel.Value,
                ModeDesc.Value,
                FontLoaderPlugin.GetPanelModeIndex(),
                keys,
                labels,
                OnModeChanged);
        }

        private static bool RegisterSamplingSizeOption()
        {
            string[] keys = new string[SamplingSizes.Length];
            string[] labels = new string[SamplingSizes.Length];
            for (int i = 0; i < SamplingSizes.Length; i++)
            {
                keys[i] = i.ToString();
                labels[i] = SamplingSizes[i] + " px";
            }

            return CoreLibOptions.RegisterDropdown(
                SamplingSizeOption,
                SamplingLabel.Value,
                SamplingDescText(),
                IndexOfNearestSize(FontLoaderPlugin.SamplingPointSize.Value),
                keys,
                labels,
                OnSamplingSizeChanged);
        }

        /// <summary>
        /// Always registered, even with an empty fonts folder: the control must stay visible in the
        /// panel. An empty list yields a single placeholder choice that does nothing when picked.
        /// The list is rescanned when the dropdown is opened, see OnFontDropdownShown.
        /// </summary>
        private static bool RegisterPluginFontOption()
        {
            List<string> paths = FontLoaderPlugin.ScanFontFiles(FontLoaderPlugin.FontsDir);

            string[] keys;
            string[] labels;
            BuildFontChoices(paths, FontsEmptyLabel.Value, true, out keys, out labels, out pluginFontPaths);
            pluginFontKeys = keys;
            pluginFontLabels = labels;

            return CoreLibOptions.RegisterDropdown(
                PluginFontOption,
                FontFileLabel.Value,
                FontFileDescText(),
                PluginFontChoiceIndex(),
                keys,
                labels,
                OnPluginFontChanged);
        }

        /// <summary>
        /// Always registered. The first choice is "not selected", so the selection can be cleared;
        /// when no system font file is found the placeholder says so instead.
        /// </summary>
        private static bool RegisterSystemFontOption()
        {
            List<string> paths = FontLoaderPlugin.ScanSystemFonts();

            string[] keys;
            string[] labels;
            string[] uniquePaths;
            BuildFontChoices(paths, NoSystemFontLabel.Value, true, out keys, out labels, out uniquePaths);
            systemFontPaths = uniquePaths;
            systemFontKeys = keys;
            systemFontLabels = labels;

            return CoreLibOptions.RegisterDropdown(
                SystemFontOption,
                SystemFontLabel.Value,
                SystemFontDesc.Value,
                Math.Max(0, SystemFontChoiceIndex(null)),
                keys,
                labels,
                OnSystemFontChanged);
        }

        /// <summary>
        /// Lists the weights of the font that is currently effective, so the choices follow both the
        /// selected font and the SystemFontPriority switch. Falls back to a single placeholder entry.
        /// </summary>
        private static bool RegisterWeightOption()
        {
            int selected;
            string[] labels = FontLoaderPlugin.GetFontWeightChoices(out selected);
            RefreshWeightChoices(labels, selected);

            return CoreLibOptions.RegisterDropdown(
                WeightOption,
                WeightLabel.Value,
                WeightDesc.Value,
                Math.Max(0, selected),
                weightKeys,
                weightLabels,
                OnWeightChanged);
        }

        private static bool RegisterPriorityOption()
        {
            return CoreLibOptions.RegisterBool(
                PriorityOption,
                PriorityLabel.Value,
                PriorityDesc.Value,
                FontLoaderPlugin.SystemFontPriority.Value,
                OnPriorityChanged);
        }

        private static bool RegisterPreserveNonCjkOption()
        {
            return CoreLibOptions.RegisterBool(
                PreserveNonCjkOption,
                PreserveNonCjkLabel.Value,
                PreserveNonCjkDesc.Value,
                FontLoaderPlugin.PreserveNonCjk.Value,
                OnPreserveNonCjkChanged);
        }

        private static bool RegisterCjkOverrideOption()
        {
            return CoreLibOptions.RegisterBool(
                CjkOverrideOption,
                CjkOverrideLabel.Value,
                CjkOverrideDesc.Value,
                FontLoaderPlugin.CjkOverrideEnabled.Value,
                OnCjkOverrideChanged);
        }

        private static bool RegisterAlignNativeMetricsOption()
        {
            return CoreLibOptions.RegisterBool(
                AlignNativeMetricsOption,
                AlignNativeMetricsLabel.Value,
                AlignNativeMetricsDesc.Value,
                FontLoaderPlugin.AlignNativeMetrics.Value,
                OnAlignNativeMetricsChanged);
        }

        private static void OnWeightChanged(int index)
        {
            if (IgnoreWhileLoading("FontWeight index=" + index))
            {
                return;
            }

            string selectedPath;
            if (!FontLoaderPlugin.SelectFontWeight(index, out selectedPath))
            {
                FontLoaderPlugin.Log.LogWarning("Ignored font weight index " + index + " from the panel.");
                return;
            }

            FontLoaderPlugin.Log.LogInfo("[Panel] weight index " + index + " -> " +
                (selectedPath ?? "<null>") + ".");

            // Refresh the row behind the source the weight was picked from: the selected weight may be
            // a file that was not present when the panel was first built. The visible rows are updated
            // in place; rebuilding the whole tab here made the panel lose its font text.
            if (FontLoaderPlugin.SystemFontPriority.Value)
            {
                RefreshSystemFontChoices(selectedPath);
            }
            else
            {
                RefreshVisibleFontFileDropdown();
            }

            PushConfigToPanel();
            RefreshVisibleWeightDropdown();
        }

        private static void OnEnabledChanged(bool value)
        {
            if (IgnoreWhileLoading("Enabled=" + value))
            {
                return;
            }

            FontLoaderPlugin.ApplyPanelEnabled(value);
            PushConfigToPanel();
            RequestTabRefresh();
        }

        private static void OnModeChanged(int index)
        {
            if (IgnoreWhileLoading("ReplaceMode index=" + index))
            {
                return;
            }

            if (!FontLoaderPlugin.ApplyPanelMode(index))
            {
                FontLoaderPlugin.Log.LogWarning("Ignored ReplaceMode index " + index + " from the panel.");
                return;
            }

            PushConfigToPanel();
            RequestTabRefresh();
        }

        private static void OnSamplingSizeChanged(int index)
        {
            if (IgnoreWhileLoading("SamplingSize index=" + index))
            {
                return;
            }

            if (index < 0 || index >= SamplingSizes.Length)
            {
                FontLoaderPlugin.Log.LogWarning("Ignored sampling size index " + index + " from the panel.");
                return;
            }

            FontLoaderPlugin.ApplyPanelSamplingSize(SamplingSizes[index]);
            PushConfigToPanel();
        }

        private static void OnPluginFontChanged(int index)
        {
            if (IgnoreWhileLoading("FontFile index=" + index))
            {
                return;
            }

            // Index 0 is the "not selected" entry: clearing reverts the texts to the game's fonts, and
            // the tab is rebuilt so no row keeps the font it was spawned with.
            if (index < 0)
            {
                FontLoaderPlugin.Log.LogWarning("Ignored font file index " + index + " from the panel.");
                return;
            }

            if (index == 0)
            {
                FontLoaderPlugin.ClearPluginFontSelection();
                RequestTabRefresh();
                PushConfigToPanel();
                return;
            }

            int fileIndex = index - 1;
            if (pluginFontPaths == null || fileIndex < 0 || fileIndex >= pluginFontPaths.Length)
            {
                FontLoaderPlugin.Log.LogWarning("Ignored font file index " + index + " from the panel.");
                return;
            }

            if (!FontLoaderPlugin.SelectPluginFont(pluginFontPaths[fileIndex]))
            {
                FontLoaderPlugin.Log.LogWarning(
                    "Font selection failed, keeping the current font: " + pluginFontPaths[fileIndex]);
            }

            PushConfigToPanel();

            // A refused pick leaves the visible caption on the file the user clicked; put it back on
            // the cfg value. Cheap: the choice list is known, nothing is rescanned.
            RefreshVisibleFontFileDropdown();
            RefreshVisibleWeightDropdown();
        }

        private static void OnSystemFontChanged(int index)
        {
            if (IgnoreWhileLoading("SystemFont index=" + index))
            {
                return;
            }

            // Index 0 is the "not selected" entry: clearing removes the system font, so nothing is
            // replaced while its priority is on.
            if (index < 0)
            {
                FontLoaderPlugin.Log.LogWarning("Ignored system font index " + index + " from the panel.");
                return;
            }

            if (index == 0)
            {
                if (FontLoaderPlugin.ClearSystemFontSelection())
                {
                    RequestTabRefresh();
                }

                PushConfigToPanel();
                RefreshVisibleWeightDropdown();
                return;
            }

            int fileIndex = index - 1;
            if (systemFontPaths == null || fileIndex < 0 || fileIndex >= systemFontPaths.Length)
            {
                FontLoaderPlugin.Log.LogWarning("Ignored system font index " + index + " from the panel.");
                return;
            }

            if (!FontLoaderPlugin.SelectSystemFont(systemFontPaths[fileIndex]))
            {
                FontLoaderPlugin.Log.LogWarning(
                    "System font selection failed, keeping the current font: " + systemFontPaths[fileIndex]);
                PushConfigToPanel();

                // The pick was refused: put the visible caption back on the cfg value.
                UpdateVisibleSystemFontDropdown(SystemFontChoiceIndex(null));
                RefreshVisibleWeightDropdown();
                return;
            }

            FontLoaderPlugin.Log.LogInfo("[Panel] system font index " + index + " -> " +
                systemFontPaths[fileIndex] + " (priority=" + FontLoaderPlugin.SystemFontPriority.Value + ").");

            PushConfigToPanel();
            RefreshVisibleWeightDropdown();
        }

        private static void OnPriorityChanged(bool value)
        {
            if (IgnoreWhileLoading("SystemFontPriority=" + value))
            {
                return;
            }

            FontLoaderPlugin.ApplyPanelSystemFontPriority(value);
            RefreshSystemFontChoices();
            PushConfigToPanel();
            RefreshVisibleWeightDropdown();
        }

        private static void OnPreserveNonCjkChanged(bool value)
        {
            if (IgnoreWhileLoading("PreserveNonCjk=" + value))
            {
                return;
            }

            FontLoaderPlugin.ApplyPanelPreserveNonCjk(value);
            PushConfigToPanel();
        }

        private static void OnCjkOverrideChanged(bool value)
        {
            if (IgnoreWhileLoading("CjkOverrideEnabled=" + value))
            {
                return;
            }

            FontLoaderPlugin.ApplyPanelCjkOverride(value);
            PushConfigToPanel();
        }

        private static void OnAlignNativeMetricsChanged(bool value)
        {
            if (IgnoreWhileLoading("AlignNativeMetrics=" + value))
            {
                return;
            }

            FontLoaderPlugin.ApplyPanelAlignNativeMetrics(value);
            PushConfigToPanel();
        }

        /// <summary>
        /// Rebuilds the visible settings tab through CUCoreLib, so its rows are re-created and pick up
        /// the current font state instead of keeping the one they were spawned with. Deferred by one
        /// frame: rebuilding from inside a row callback would destroy the row that is still executing.
        /// When a font is selected, the fresh rows are re-fonted in the same frame so no flash appears.
        /// </summary>
        internal static void Tick()
        {
            if (!tabRefreshPending)
            {
                return;
            }

            tabRefreshPending = false;
            RefreshVisibleTab();

            if (FontLoaderPlugin.HasPrimaryFont())
            {
                try
                {
                    FontLoaderPlugin.RefreshAllTexts();
                }
                catch (Exception ex)
                {
                    FontLoaderPlugin.Log.LogWarning("Panel re-apply failed: " + ex.Message);
                }
            }
        }
        private static void RequestTabRefresh()
        {
            tabRefreshPending = true;
        }

        internal static void RefreshVisibleTab()
        {
            try
            {
                Type extender = CoreLibOptions.FindTypeOrNull("CUCoreLib.Helpers.SettingsMenuCategoryExtender");
                MethodInfo refresh = extender == null
                    ? null
                    : extender.GetMethod("RefreshLiveMenu", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (refresh != null)
                {
                    refresh.Invoke(null, null);
                    return;
                }

                FontLoaderPlugin.Log.LogInfo("[Panel] tab refresh helper unavailable; rows keep their current font.");
            }
            catch (Exception ex)
            {
                FontLoaderPlugin.Log.LogWarning("[Panel] tab refresh failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Drops panel changes while settings.json is being applied, so the cfg file stays
        /// authoritative, and while pushing our own values so a programmatic write cannot feed back.
        /// Logged because a swallowed click otherwise looks like "the toggle did nothing".
        /// </summary>
        private static bool IgnoreWhileLoading(string what)
        {
            if (pushingToPanel)
            {
                return true;
            }

            if (!loadingSettings)
            {
                return false;
            }

            FontLoaderPlugin.Log.LogInfo("[Panel] ignored " + what + " while settings are loading.");
            return true;
        }

        /// <summary>
        /// Runs a programmatic panel write without feeding it back through our own row callbacks. A
        /// corrected index would otherwise re-run the whole select path (load, re-apply, refresh rows)
        /// from inside itself.
        /// </summary>
        private static void SuppressCallbacks(Action write)
        {
            if (pushingToPanel)
            {
                write();
                return;
            }

            pushingToPanel = true;
            try
            {
                write();
            }
            finally
            {
                pushingToPanel = false;
            }
        }

        /// <summary>
        /// Rebuilds the weight choices from values the caller already obtained (one directory scan per
        /// panel sync). Silent when nothing changed; updates the live setting otherwise.
        /// </summary>
        private static void RefreshWeightChoices(string[] labels, int selectedIndex)
        {
            try
            {
                labels = WeightLabelValues(labels);
                string[] keys;
                if (labels.Length == 0)
                {
                    // Same wording as the font-file placeholder: nothing is selected, so nothing to pick.
                    keys = new[] { EmptyChoiceKey };
                    labels = new[] { NotSelectedLabel.Value };
                }
                else
                {
                    keys = new string[labels.Length];
                    for (int i = 0; i < labels.Length; i++)
                    {
                        keys[i] = i.ToString();
                    }
                }

                if (SameStrings(keys, weightKeys) && SameStrings(labels, weightLabels))
                {
                    return;
                }

                weightKeys = keys;
                weightLabels = labels;

                for (int i = 0; i < keys.Length; i++)
                {
                    CoreLibOptions.RegisterChoiceLabel(WeightOption, keys[i], labels[i]);
                }

                SettingDropdown dropdown = FindSetting(WeightOption) as SettingDropdown;
                if (dropdown != null && SetDropdownChoices(dropdown, keys))
                {
                    int value = selectedIndex >= 0 && selectedIndex < keys.Length ? selectedIndex : 0;
                    SuppressCallbacks(delegate { dropdown.value = value; });
                }
            }
            catch (Exception ex)
            {
                FontLoaderPlugin.Log.LogWarning("Weight list refresh failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Weight labels for the font in effect. A variable font can only render its default instance
        /// under Unity 2022, so the lone entry says so instead of looking like a detection bug.
        /// </summary>
        private static string[] WeightLabelValues(string[] labels)
        {
            if (labels == null || labels.Length == 0)
            {
                return new string[0];
            }

            if (labels.Length == 1 && FontLoaderPlugin.ActiveFontIsVariable())
            {
                labels[0] = labels[0] + VariableFontHint.Value;
            }

            return labels;
        }

        private static bool SameStrings(string[] first, string[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
            {
                return false;
            }

            for (int i = 0; i < first.Length; i++)
            {
                if (!string.Equals(first[i], second[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Re-registers every option label, description and choice text in the current game language.
        /// CUCoreLib resolves those keys when it spawns a row, so a language switch is picked up the next
        /// time the settings menu opens, without a restart.
        /// </summary>
        internal static void RefreshPanelTexts()
        {
            if (!ready)
            {
                return;
            }

            try
            {
                CoreLibOptions.RegisterOptionText(EnabledOption, EnabledLabel.Value, EnabledDesc.Value);
                CoreLibOptions.RegisterOptionText(ModeOption, ModeLabel.Value, ModeDesc.Value);
                CoreLibOptions.RegisterOptionText(SamplingSizeOption, SamplingLabel.Value, SamplingDescText());
                CoreLibOptions.RegisterOptionText(PluginFontOption, FontFileLabel.Value, FontFileDescText());
                CoreLibOptions.RegisterOptionText(SystemFontOption, SystemFontLabel.Value, SystemFontDesc.Value);
                CoreLibOptions.RegisterOptionText(WeightOption, WeightLabel.Value, WeightDesc.Value);
                CoreLibOptions.RegisterOptionText(PriorityOption, PriorityLabel.Value, PriorityDesc.Value);
                CoreLibOptions.RegisterOptionText(PreserveNonCjkOption, PreserveNonCjkLabel.Value,
                    PreserveNonCjkDesc.Value);
                CoreLibOptions.RegisterOptionText(CjkOverrideOption, CjkOverrideLabel.Value,
                    CjkOverrideDesc.Value);
                CoreLibOptions.RegisterOptionText(AlignNativeMetricsOption, AlignNativeMetricsLabel.Value,
                    AlignNativeMetricsDesc.Value);

                FontLoaderPlugin.FontReplaceMode[] modes = FontLoaderPlugin.PanelModes;
                for (int i = 0; i < modes.Length && i < ModeLabels.Length; i++)
                {
                    CoreLibOptions.RegisterChoiceLabel(ModeOption, i.ToString(), ModeLabels[i].Value);
                }

                CoreLibOptions.RegisterChoiceLabel(PluginFontOption, EmptyChoiceKey, FontListEmptyText());
                CoreLibOptions.RegisterChoiceLabel(SystemFontOption, EmptyChoiceKey, SystemFontEmptyText());
                CoreLibOptions.RegisterChoiceLabel(WeightOption, EmptyChoiceKey, NotSelectedLabel.Value);
            }
            catch (Exception ex)
            {
                FontLoaderPlugin.Log.LogWarning("Panel text refresh failed: " + ex.Message);
            }
        }

        /// <summary>Writes the cfg-derived values into the live panel settings.</summary>
        internal static void PushConfigToPanel()
        {
            if (!ready || pushingToPanel)
            {
                return;
            }

            pushingToPanel = true;
            try
            {
                // One directory scan per sync: the labels and the current index share it.
                int weightSelection;
                string[] weightChoices = FontLoaderPlugin.GetFontWeightChoices(out weightSelection);
                RefreshWeightChoices(weightChoices, weightSelection);

                SetBoolValue(EnabledOption, FontLoaderPlugin.IsReplacementEnabled());
                SetDropdownValue(ModeOption, FontLoaderPlugin.GetPanelModeIndex(),
                    FontLoaderPlugin.PanelModes.Length);
                SetDropdownValue(SamplingSizeOption, IndexOfNearestSize(FontLoaderPlugin.SamplingPointSize.Value),
                    SamplingSizes.Length);
                SetDropdownValue(PluginFontOption, PluginFontChoiceIndex(), pluginFontPaths.Length + 1);
                int systemIndex = SystemFontChoiceIndex(null);
                if (systemIndex < 0)
                {
                    // Do not rebuild dropdown rows from here. PushConfigToPanel also runs while
                    // the tab is spawning; touching a half-built row is what made the settings
                    // panel lose its font text. The system-font row rescans when it is opened or
                    // when a weight/priority change explicitly refreshes it.
                    FontLoaderPlugin.Log.LogDebug("[Panel] system font '" + FontLoaderPlugin.SystemFont.Value +
                        "' is not in the cached list; it will be rescanned when the row is opened.");
                }

                SetDropdownValue(SystemFontOption, systemIndex, systemFontPaths.Length + 1);
                SetDropdownValue(WeightOption, weightSelection, weightLabels.Length);
                SetBoolValue(PriorityOption, FontLoaderPlugin.SystemFontPriority.Value);
                SetBoolValue(PreserveNonCjkOption, FontLoaderPlugin.PreserveNonCjk.Value);
                SetBoolValue(CjkOverrideOption, FontLoaderPlugin.CjkOverrideEnabled.Value);
                SetBoolValue(AlignNativeMetricsOption, FontLoaderPlugin.AlignNativeMetrics.Value);
            }
            catch (Exception ex)
            {
                FontLoaderPlugin.Log.LogWarning("Panel sync failed: " + ex.Message);
            }
            finally
            {
                pushingToPanel = false;
            }
        }

        /// <summary>
        /// Builds the choice list. CUCoreLib requires at least one choice, so an empty list becomes a
        /// single placeholder entry that keeps the control visible. Choice keys are the file names:
        /// they are unique, readable as a fallback label, and stable across rescans. UniquePaths is the
        /// exact path list behind the file choices, so index-based lookups stay aligned.
        ///
        /// WithEmptyChoice prepends a "not selected" entry; both font dropdowns use it, so an empty
        /// selection shows as unselected instead of looking like the first file was picked.
        /// </summary>
        private const string EmptyChoiceKey = "none";

        private static void BuildFontChoices(List<string> paths, string emptyLabel, bool withEmptyChoice,
            out string[] keys, out string[] labels, out string[] uniquePaths)
        {
            List<string> uniqueKeys = new List<string>();
            List<string> uniqueLabels = new List<string>();
            List<string> unique = new List<string>();

            if (paths != null)
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < paths.Count; i++)
                {
                    string fileName = Path.GetFileName(paths[i]);
                    if (string.IsNullOrEmpty(fileName) || !seen.Add(fileName))
                    {
                        continue;
                    }

                    uniqueKeys.Add(fileName);
                    uniqueLabels.Add(fileName);
                    unique.Add(paths[i]);
                }
            }

            uniquePaths = unique.ToArray();

            if (withEmptyChoice)
            {
                uniqueKeys.Insert(0, EmptyChoiceKey);
                string noneLabel = NotSelectedLabel.Value;
                uniqueLabels.Insert(0, unique.Count == 0 ? noneLabel + emptyLabel : noneLabel);
            }
            else if (uniqueKeys.Count == 0)
            {
                uniqueKeys.Add(EmptyChoiceKey);
                uniqueLabels.Add(emptyLabel);
            }

            keys = uniqueKeys.ToArray();
            labels = uniqueLabels.ToArray();
        }

        /// <summary>
        /// Rescans the fonts folder so files added or removed at runtime show up without a restart.
        /// Runs when the font dropdown is opened (see OnFontDropdownShown), or on the Language tab as a
        /// fallback when the dropdown hook is unavailable. Silent unless the list actually changed.
        /// </summary>
        internal static void RefreshLocalFontChoices()
        {
            if (!ready)
            {
                return;
            }

            try
            {
                List<string> paths = FontLoaderPlugin.ScanFontFiles(FontLoaderPlugin.FontsDir);
                string[] keys;
                string[] labels;
                string[] uniquePaths;
                BuildFontChoices(paths, FontsEmptyLabel.Value, true, out keys, out labels, out uniquePaths);
                if (SameStrings(keys, pluginFontKeys))
                {
                    return;
                }

                pluginFontPaths = uniquePaths;
                pluginFontKeys = keys;
                pluginFontLabels = labels;

                for (int i = 0; i < keys.Length; i++)
                {
                    CoreLibOptions.RegisterChoiceLabel(PluginFontOption, keys[i], labels[i]);
                }

                // A file that vanished while it was the active selection must not keep being used.
                if (FontLoaderPlugin.RevalidateFontSelection())
                {
                    // No font left to apply: rebuild the tab so every row is re-created by the game.
                    RequestTabRefresh();
                }

                SettingDropdown dropdown = FindSetting(PluginFontOption) as SettingDropdown;
                if (dropdown != null && SetDropdownChoices(dropdown, keys))
                {
                    int value = PluginFontChoiceIndex();
                    SuppressCallbacks(delegate { dropdown.value = value; });
                }

                FontLoaderPlugin.Log.LogDebug(
                    "Font list rescanned: " + pluginFontPaths.Length + " file(s) in " + FontLoaderPlugin.FontsDir + ".");
            }
            catch (Exception ex)
            {
                FontLoaderPlugin.Log.LogWarning("Font list rescan failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Rescans the system font list and rebuilds the dropdown choices. Used after a weight change
        /// (the new weight may not be in the list captured at registration) and before the system
        /// font dropdown is shown. Passing the selected path keeps the choice exact when two fonts
        /// share a base name.
        /// </summary>
        private static void RefreshSystemFontChoices(string selectedPath = null)
        {
            if (!ready)
            {
                return;
            }

            try
            {
                List<string> paths = FontLoaderPlugin.ScanSystemFonts();
                string[] keys;
                string[] labels;
                string[] uniquePaths;
                BuildFontChoices(paths, NoSystemFontLabel.Value, true, out keys, out labels, out uniquePaths);

                bool changed = !SameStrings(keys, systemFontKeys) || !SameStrings(labels, systemFontLabels);
                systemFontPaths = uniquePaths;
                systemFontKeys = keys;
                systemFontLabels = labels;

                if (changed)
                {
                    for (int i = 0; i < keys.Length; i++)
                    {
                        CoreLibOptions.RegisterChoiceLabel(SystemFontOption, keys[i], labels[i]);
                    }

                    SettingDropdown data = FindSetting(SystemFontOption) as SettingDropdown;
                    if (data != null)
                    {
                        SetDropdownChoices(data, keys);
                    }
                }

                int index = SystemFontChoiceIndex(selectedPath);
                SuppressCallbacks(delegate
                {
                    SetDropdownValue(SystemFontOption, index, systemFontPaths.Length + 1);
                });
                UpdateVisibleSystemFontDropdown(index, changed);
                FontLoaderPlugin.Log.LogDebug("[Panel] system list refreshed: " + systemFontPaths.Length +
                    " file(s), changed=" + changed + ", selected=" + index +
                    " (" + (FontLoaderPlugin.SystemFont.Value ?? "<null>") + ").");
            }
            catch (Exception ex)
            {
                FontLoaderPlugin.Log.LogWarning("System font list refresh failed: " + ex.Message);
            }
        }

        private static int IndexOfFontPath(string path, string[] paths)
        {
            if (string.IsNullOrEmpty(path) || paths == null)
            {
                return -1;
            }

            for (int i = 0; i < paths.Length; i++)
            {
                if (string.Equals(paths[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Called before a dropdown is shown. Only our own rows are handled: the font-file list is
        /// rescanned and the weight list is recomputed, so no scan happens on unrelated rows.
        /// </summary>
        internal static void OnFontDropdownShown(TMP_Dropdown dropdown)
        {
            if (!ready || dropdown == null)
            {
                return;
            }

            if (IsRowWithLabel(dropdown.transform, SystemFontLabel))
            {
                RefreshSystemFontChoices();
                return;
            }

            if (IsRowWithLabel(dropdown.transform, FontFileLabel))
            {
                RefreshLocalFontChoices();
                RebuildDropdownOptions(dropdown, pluginFontKeys, pluginFontLabels, PluginFontChoiceIndex(),
                    FontListEmptyText());
                return;
            }

            if (IsRowWithLabel(dropdown.transform, WeightLabel))
            {
                int selected;
                string[] labels = FontLoaderPlugin.GetFontWeightChoices(out selected);
                RefreshWeightChoices(labels, selected);
                RebuildDropdownOptions(dropdown, weightKeys, weightLabels, selected, NotSelectedLabel.Value);
            }
        }

        private static TMP_Dropdown FindDropdownWithLabel(LocText label)
        {
            TMP_Dropdown[] dropdowns = Resources.FindObjectsOfTypeAll<TMP_Dropdown>();
            for (int i = 0; i < dropdowns.Length; i++)
            {
                TMP_Dropdown dropdown = dropdowns[i];
                if (dropdown != null && dropdown.gameObject.activeInHierarchy &&
                    IsRowWithLabel(dropdown.transform, label))
                {
                    return dropdown;
                }
            }

            return null;
        }

        /// <summary>
        /// Rebuilds the visible weight row after the effective font source changed (system font,
        /// priority, local font). The SettingDropdown data alone does not update an existing row.
        /// </summary>
        private static void RefreshVisibleWeightDropdown()
        {
            int selected;
            string[] labels = FontLoaderPlugin.GetFontWeightChoices(out selected);
            FontLoaderPlugin.Log.LogDebug("[Panel] weight choices: " + labels.Length +
                ", selected=" + selected + ", system=" + FontLoaderPlugin.SystemFontPriority.Value + ".");
            string[] beforeKeys = weightKeys;
            string[] beforeLabels = weightLabels;

            RefreshWeightChoices(labels, selected);
            SuppressCallbacks(delegate { SetDropdownValue(WeightOption, selected, weightLabels.Length); });

            bool choicesChanged = !SameStrings(weightKeys, beforeKeys) ||
                                  !SameStrings(weightLabels, beforeLabels);
            TMP_Dropdown dropdown = FindDropdownWithLabel(WeightLabel);
            if (dropdown == null)
            {
                return;
            }

            int visibleSelection = selected >= 0 ? selected : 0;
            if (!choicesChanged && dropdown.options.Count == weightKeys.Length)
            {
                dropdown.SetValueWithoutNotify(visibleSelection);
                dropdown.RefreshShownValue();
                return;
            }

            RebuildDropdownOptions(dropdown, weightKeys, weightLabels, visibleSelection, NotSelectedLabel.Value);
        }

        private static void RefreshVisibleFontFileDropdown()
        {
            TMP_Dropdown dropdown = FindDropdownWithLabel(FontFileLabel);
            if (dropdown == null)
            {
                return;
            }

            int selected = PluginFontChoiceIndex();
            if (dropdown.options.Count == pluginFontKeys.Length)
            {
                dropdown.SetValueWithoutNotify(selected);
                dropdown.RefreshShownValue();
                return;
            }

            RebuildDropdownOptions(dropdown, pluginFontKeys, pluginFontLabels, selected, FontListEmptyText());
        }

        private static void UpdateVisibleSystemFontDropdown(int selectedIndex, bool choicesChanged = false)
        {
            if (selectedIndex < 0)
            {
                return;
            }

            TMP_Dropdown dropdown = FindDropdownWithLabel(SystemFontLabel);
            if (dropdown == null)
            {
                return;
            }

            // Most weight changes leave the system font list untouched. Clearing and re-adding the
            // options just to move the selection makes the caption blank for a frame, so only
            // rebuild the option list when the choices changed; otherwise set the value silently.
            if (!choicesChanged && dropdown.options.Count == systemFontKeys.Length)
            {
                dropdown.SetValueWithoutNotify(selectedIndex);
                dropdown.RefreshShownValue();
                return;
            }

            RebuildDropdownOptions(dropdown, systemFontKeys, systemFontLabels, selectedIndex,
                SystemFontEmptyText());
        }

        /// <summary>Placeholder text of the font-file dropdown in the current language.</summary>
        private static string FontListEmptyText()
        {
            string none = NotSelectedLabel.Value;
            return pluginFontPaths.Length == 0 ? none + FontsEmptyLabel.Value : none;
        }

        /// <summary>Placeholder text of the system-font dropdown in the current language.</summary>
        private static string SystemFontEmptyText()
        {
            string none = NotSelectedLabel.Value;
            return systemFontPaths.Length == 0 ? none + NoSystemFontLabel.Value : none;
        }

        /// <summary>
        /// Recognises a row by its label, in either language, so identification keeps working right
        /// after a language switch. Only direct children of the dropdown's ancestors are inspected, so
        /// a sibling row's label can never match.
        /// </summary>
        private static bool IsRowWithLabel(UnityEngine.Transform start, LocText label)
        {
            UnityEngine.Transform current = start;
            for (int depth = 0; current != null && depth < 3; depth++)
            {
                int count = current.childCount;
                for (int i = 0; i < count; i++)
                {
                    TMP_Text text = current.GetChild(i).GetComponent<TMP_Text>();
                    if (text == null)
                    {
                        continue;
                    }

                    string value = text.text;
                    if (value == null)
                    {
                        continue;
                    }

                    value = value.Trim();
                    if (string.Equals(value, label.Zh, StringComparison.Ordinal) ||
                        string.Equals(value, label.En, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                current = current.parent;
            }

            return false;
        }

        /// <summary>
        /// Rebuilds the open dropdown from the given choices. The placeholder choice always uses the
        /// freshly resolved text, so a language switch cannot leave the old one behind.
        /// </summary>
        private static void RebuildDropdownOptions(TMP_Dropdown dropdown, string[] keys, string[] labels,
            int selectedIndex, string emptyText)
        {
            if (keys == null || labels == null || keys.Length == 0)
            {
                return;
            }

            try
            {
                List<TMP_Dropdown.OptionData> options = new List<TMP_Dropdown.OptionData>(keys.Length);
                for (int i = 0; i < keys.Length; i++)
                {
                    string text = keys[i] == EmptyChoiceKey && !string.IsNullOrEmpty(emptyText)
                        ? emptyText
                        : labels[i];
                    options.Add(new TMP_Dropdown.OptionData(text));
                }

                dropdown.ClearOptions();
                dropdown.AddOptions(options);

                dropdown.SetValueWithoutNotify(selectedIndex >= 0 && selectedIndex < keys.Length
                    ? selectedIndex
                    : 0);
                dropdown.RefreshShownValue();
            }
            catch (Exception ex)
            {
                FontLoaderPlugin.Log.LogWarning("Dropdown rebuild failed: " + ex.Message);
            }
        }

        /// <summary>
        /// SettingDropdown.choices is not guaranteed to be public, so it is assigned by reflection.
        /// Returns false when the member cannot be written, in which case the list stays as it is.
        /// </summary>
        private static bool SetDropdownChoices(SettingDropdown dropdown, string[] keys)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            FieldInfo field = typeof(SettingDropdown).GetField("choices", flags);
            if (field != null && field.FieldType == typeof(string[]))
            {
                field.SetValue(dropdown, keys);
                return true;
            }

            PropertyInfo property = typeof(SettingDropdown).GetProperty("choices", flags);
            if (property != null && property.CanWrite && property.PropertyType == typeof(string[]))
            {
                property.SetValue(dropdown, keys, null);
                return true;
            }

            return false;
        }

        /// <summary>Dropdown index of the current selection: 0 is "not selected", files start at 1.</summary>
        private static int PluginFontChoiceIndex()
        {
            if (string.IsNullOrEmpty(FontLoaderPlugin.FontFile.Value))
            {
                return 0;
            }

            int index = IndexOfFontFile(FontLoaderPlugin.FontFile.Value, pluginFontPaths);
            return index < 0 ? 0 : index + 1;
        }

        /// <summary>
        /// Dropdown index of the system font selection: 0 is "not selected", files start at 1, and -1
        /// means the selection is not part of the cached list (the row must keep its value).
        /// SelectedPath, when given, wins over the name in the cfg because two files can share a name.
        /// </summary>
        private static int SystemFontChoiceIndex(string selectedPath)
        {
            if (!string.IsNullOrEmpty(selectedPath))
            {
                int byPath = IndexOfFontPath(selectedPath, systemFontPaths);
                return byPath < 0 ? -1 : byPath + 1;
            }

            if (string.IsNullOrEmpty(FontLoaderPlugin.SystemFont.Value))
            {
                return 0;
            }

            int byName = IndexOfFontName(FontLoaderPlugin.SystemFont.Value, systemFontPaths);
            return byName < 0 ? -1 : byName + 1;
        }

        /// <summary>Index of the local font whose file name (with extension) matches, or -1.</summary>
        private static int IndexOfFontFile(string fileName, string[] paths)
        {
            if (string.IsNullOrEmpty(fileName) || paths == null)
            {
                return -1;
            }

            for (int i = 0; i < paths.Length; i++)
            {
                if (string.Equals(Path.GetFileName(paths[i]), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>Index of the font whose file name matches, or -1 when it is not in the list.</summary>
        private static int IndexOfFontName(string name, string[] paths)
        {
            if (string.IsNullOrEmpty(name) || paths == null)
            {
                return -1;
            }

            for (int i = 0; i < paths.Length; i++)
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(paths[i]), name,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int IndexOfNearestSize(int size)
        {
            int best = 0;
            int bestDelta = int.MaxValue;
            for (int i = 0; i < SamplingSizes.Length; i++)
            {
                int delta = Math.Abs(SamplingSizes[i] - size);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = i;
                }
            }

            return best;
        }

        internal static void BeginSettingsLoad()
        {
            loadingSettings = true;
        }

        internal static void EndSettingsLoad()
        {
            loadingSettings = false;
            PushConfigToPanel();
        }

        internal static void AbortSettingsLoad()
        {
            loadingSettings = false;
        }

        private static void SetBoolValue(string id, bool value)
        {
            SettingBool toggle = FindSetting(id) as SettingBool;
            if (toggle != null)
            {
                toggle.value = value;
            }
        }

        private static void SetDropdownValue(string id, int value, int count)
        {
            if (value < 0)
            {
                // The current selection is not part of this list; leave the last selection alone.
                return;
            }

            SettingDropdown dropdown = FindSetting(id) as SettingDropdown;
            if (dropdown == null || count <= 0)
            {
                return;
            }

            if (value >= count)
            {
                value = 0;
            }

            dropdown.value = value;
        }

        private static Setting FindSetting(string id)
        {
            List<Setting> settings = Settings.settings;
            if (settings == null)
            {
                return null;
            }

            for (int i = 0; i < settings.Count; i++)
            {
                Setting setting = settings[i];
                if (setting != null && string.Equals(setting.name, id, StringComparison.Ordinal))
                {
                    return setting;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Marks the settings load window; the original exception is returned unchanged.
    /// </summary>
    [HarmonyPatch(typeof(Settings), "LoadSettings")]
    internal static class SettingsLoadGuardPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            FontSettingsPanel.BeginSettingsLoad();
        }

        [HarmonyPostfix]
        private static void Postfix()
        {
            FontSettingsPanel.EndSettingsLoad();
        }

        [HarmonyFinalizer]
        private static Exception Finalizer(Exception __exception)
        {
            FontSettingsPanel.AbortSettingsLoad();
            return __exception;
        }
    }

    /// <summary>
    /// Targets installed by reflection in FontSettingsPanel.InstallMenuSyncHooks. They resync the panel
    /// from cfg, refresh the labels for the current game language, and rescan the fonts folder on demand.
    /// </summary>
    internal static class SettingsMenuHooks
    {
        internal static void AfterMenuStart()
        {
            FontSettingsPanel.RefreshPanelTexts();
            FontSettingsPanel.PushConfigToPanel();
        }

        internal static void BeforeTabSpawn()
        {
            FontSettingsPanel.RefreshPanelTexts();

            if (!FontSettingsPanel.DropdownHookInstalled)
            {
                // Fallback only: the dropdown hook is the normal (and silent) trigger.
                FontSettingsPanel.RefreshLocalFontChoices();
            }

            FontSettingsPanel.PushConfigToPanel();
        }

        internal static void BeforeDropdownShow(TMP_Dropdown __instance)
        {
            FontSettingsPanel.OnFontDropdownShown(__instance);
        }
    }
}
