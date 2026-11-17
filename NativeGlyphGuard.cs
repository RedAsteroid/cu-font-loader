// cu-font-loader - CJK-only glyph ownership for the replacement font.
// Licensed under MIT License
// https://github.com/FenChen0211/cu-font-loader
//
// TMP resolves every character by asking the text's font asset first: a dynamic font asset adds the
// glyph to its own atlas on demand, and only when that fails does TMP walk the fallback chain
// (verified against the game's Unity.TextMeshPro: GetCharacterFromFontAsset_Internal looks in the
// character table, then calls TryAddCharacterInternal when the asset is Dynamic, then recurses into
// fallbackFontAssetTable). Keeping digits, Latin letters and symbols on the game's font therefore
// means answering "not mine" at that add step, which is what this guard hooks. It is a prefix, it
// fails open, and it only ever runs for the font assets this plugin created.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace CuFontLoader
{
    /// <summary>
    /// The only place where a character is taken away from the replacement font. Everything else
    /// (mode, exclusions, materials) decides whether a text is re-fonted at all; this decides which
    /// glyphs that font may own.
    /// </summary>
    internal static class NativeGlyphGuard
    {
        private static bool installAttempted;
        private static Harmony s_harmony;

        /// <summary>Assets already stripped for the current activation; see PurgeAll.</summary>
        private static readonly HashSet<TMP_FontAsset> Purified = new HashSet<TMP_FontAsset>();

        /// <summary>
        /// Flat list of the native fonts TMP can reach after the plugin font: our fallback chain, the
        /// global fallback list and the game default, each expanded through its own fallback table.
        /// TMP searches those chains recursively, so a flat list is what makes the per-glyph check both
        /// correct and allocation-free.
        /// </summary>
        private static readonly List<TMP_FontAsset> NativeProviders = new List<TMP_FontAsset>();
        private static readonly List<TMP_FontAsset> ProviderQueue = new List<TMP_FontAsset>();
        private static readonly HashSet<TMP_FontAsset> ProviderSeen = new HashSet<TMP_FontAsset>();
        private static readonly List<TMP_FontAsset> PreviousProviders = new List<TMP_FontAsset>();

        /// <summary>Codes already reported once when the plugin font had to take them.</summary>
        private static readonly HashSet<uint> LoggedTakenGlyphs = new HashSet<uint>();

        /// <summary>True after the "no native coverage" warning was written, so it is logged once.</summary>
        private static bool reportedEmptyProviders;

        internal static bool Installed
        {
            get { return s_harmony != null; }
        }

        /// <summary>True when the option is on and the glyph guard is the active strategy.</summary>
        internal static bool IsActive
        {
            get { return s_harmony != null && FontLoaderPlugin.IsPreserveNonCjkEnabled; }
        }

        /// <summary>
        /// Patches the one entry point every TMP glyph add goes through. Returns false when the hook
        /// could not be installed; PreserveNonCjk still works through fallback priority, only without
        /// the guard that keeps glyphs the game fonts do not cover out of the plugin font.
        /// </summary>
        internal static bool Install()
        {
            if (installAttempted)
            {
                return Installed;
            }

            installAttempted = true;

            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(TMP_FontAsset),
                    "TryAddCharacterInternal",
                    new[] { typeof(uint), typeof(TMP_Character).MakeByRefType() });
                if (target == null)
                {
                    LogInfo("TMP_FontAsset.TryAddCharacterInternal not found; the glyph guard is " +
                        "disabled. PreserveNonCjk keeps working through fallback priority, but a glyph " +
                        "the game fonts lack may be added to the plugin font.");
                    return false;
                }

                Harmony harmony = new Harmony("fenchen.cu-font-loader.nativeglyph");
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(NativeGlyphGuard), nameof(BeforeAddCharacter)));
                s_harmony = harmony;
                return true;
            }
            catch (Exception ex)
            {
                LogWarning("Native glyph guard not installed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Returning false skips the dynamic glyph add, so TMP keeps looking and finds the character
        /// in the game's own font. Every check fails open: a digit drawn in the replacement font is
        /// far better than a missing glyph box.
        /// </summary>
        private static bool BeforeAddCharacter(TMP_FontAsset __instance, uint unicode,
            ref TMP_Character character)
        {
            try
            {
                if (s_harmony == null || !IsActive || __instance == null)
                {
                    return true;
                }

                if (!FontLoaderPlugin.IsKnownFont(__instance))
                {
                    return true;
                }

                if (CjkText.IsCjk(unicode))
                {
                    return true;
                }

                if (AnyNativeProviderHasGlyph(unicode))
                {
                    // TMP will find the character in one of the native fonts, so keep it out of ours.
                    // Mirrors what GetTextElement does: own chain, global list, game default - and each
                    // of those fonts' own fallback tables.
                    character = null;
                    return false;
                }

                LogTakenGlyph(__instance, unicode);
                return true;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// Flattens every font TMP may search after the plugin font. Called once per pass with the
        /// current fallback chain, so the per-glyph check only walks a small flat list.
        /// </summary>
        internal static void RefreshNativeProviders(List<TMP_FontAsset> chain)
        {
            NativeProviders.Clear();
            ProviderQueue.Clear();
            ProviderSeen.Clear();

            AddProviderRoots(chain);

            // The tables actually configured on our fonts are what TMP will walk, so include them even
            // when this pass could not resolve any original font.
            foreach (TMP_FontAsset font in FontLoaderPlugin.KnownFontAssets)
            {
                if (font != null)
                {
                    AddProviderRoots(font.fallbackFontAssetTable);
                }
            }

            try
            {
                AddProviderRoots(TMP_Settings.fallbackFontAssets);
                AddProviderRoot(TMP_Settings.defaultFontAsset);
            }
            catch (Exception)
            {
                // TMP settings not readable yet; the chain roots still cover the common case.
            }

            for (int i = 0; i < ProviderQueue.Count; i++)
            {
                TMP_FontAsset font = ProviderQueue[i];
                NativeProviders.Add(font);

                List<TMP_FontAsset> table = font.fallbackFontAssetTable;
                if (table == null)
                {
                    continue;
                }

                for (int j = 0; j < table.Count; j++)
                {
                    AddProviderRoot(table[j]);
                }
            }

            // A newly reachable font can take glyphs back that the plugin font had to add earlier.
            if (!SameProviders(PreviousProviders, NativeProviders))
            {
                PreviousProviders.Clear();
                PreviousProviders.AddRange(NativeProviders);
                Purified.Clear();

                if (NativeProviders.Count == 0)
                {
                    if (!reportedEmptyProviders)
                    {
                        reportedEmptyProviders = true;
                        LogWarning("[Native] no native font is reachable yet; the plugin font will take " +
                            "every glyph until the fallback chain is built.");
                    }
                }
                else
                {
                    reportedEmptyProviders = false;
                    LogInfo("[Native] native font coverage: " + DescribeProviders());
                }
            }
        }

        private static string DescribeProviders()
        {
            string text = string.Empty;
            int limit = Mathf.Min(NativeProviders.Count, 8);
            for (int i = 0; i < limit; i++)
            {
                if (i > 0)
                {
                    text += ", ";
                }

                text += NativeProviders[i].name;
            }

            return NativeProviders.Count > limit
                ? text + ", ... (" + NativeProviders.Count + ")"
                : text;
        }

        private static void AddProviderRoots(List<TMP_FontAsset> fonts)
        {
            if (fonts == null)
            {
                return;
            }

            for (int i = 0; i < fonts.Count; i++)
            {
                AddProviderRoot(fonts[i]);
            }
        }

        private static void AddProviderRoot(TMP_FontAsset font)
        {
            if (font == null || FontLoaderPlugin.IsKnownFont(font) || !ProviderSeen.Add(font))
            {
                return;
            }

            ProviderQueue.Add(font);
        }

        private static bool SameProviders(List<TMP_FontAsset> first, List<TMP_FontAsset> second)
        {
            if (first.Count != second.Count)
            {
                return false;
            }

            for (int i = 0; i < first.Count; i++)
            {
                if (first[i] != second[i])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// True when any reachable native font can render this character. Diagnostics use it to tell a
        /// glyph the plugin font had to take (no game font has it) apart from one it should have left to
        /// a game font.
        /// </summary>
        internal static bool HasNativeGlyph(uint unicode)
        {
            return AnyNativeProviderHasGlyph(unicode);
        }

        private static bool AnyNativeProviderHasGlyph(uint unicode)
        {
            for (int i = 0; i < NativeProviders.Count; i++)
            {
                TMP_FontAsset font = NativeProviders[i];
                if (font == null)
                {
                    // Unloaded since the list was built; the next pass rebuilds it.
                    continue;
                }

                if (HasGlyph(font, unicode))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reports once (with CompatibilityLog on) which characters only the plugin font could render,
        /// so a glyph that is really missing from the game can be told apart from a routing mistake.
        /// </summary>
        private static void LogTakenGlyph(TMP_FontAsset asset, uint unicode)
        {
            bool detailed = (FontLoaderPlugin.CompatibilityLog != null &&
                             FontLoaderPlugin.CompatibilityLog.Value) ||
                            (FontLoaderPlugin.LogTextDetails != null &&
                             FontLoaderPlugin.LogTextDetails.Value);
            if (!detailed || !LoggedTakenGlyphs.Add(unicode))
            {
                return;
            }

            LogInfo("[Native] '" + asset.name + "' takes U+" + unicode.ToString("X4") +
                " because no game font can render it.");
        }

        /// <summary>
        /// Static assets answer from their character table. A dynamic asset is asked through TMP's
        /// own resolver, which adds the glyph the same way it would for native text; that keeps the
        /// answer honest for fonts that only populate their atlas on demand.
        /// </summary>
        private static bool HasGlyph(TMP_FontAsset font, uint unicode)
        {
            Dictionary<uint, TMP_Character> lookup = font.characterLookupTable;
            if (lookup != null && lookup.ContainsKey(unicode))
            {
                return true;
            }

            if (font.atlasPopulationMode != AtlasPopulationMode.Dynamic)
            {
                return false;
            }

            bool alternativeTypeface;
            return TMP_FontAssetUtilities.GetCharacterFromFontAsset(
                unicode, font, false, FontStyles.Normal, FontWeight.Regular,
                out alternativeTypeface) != null;
        }

        /// <summary>
        /// Drops the non-CJK character entries so the font stops owning those glyphs. The atlas and
        /// its glyphs are kept on purpose: the guard blocks new adds, and turning the option back off
        /// restores the entries from the glyphs that are still there, without re-rasterising anything.
        /// Returns how many entries were removed.
        /// </summary>
        internal static int Purge(TMP_FontAsset asset)
        {
            if (asset == null)
            {
                return 0;
            }

            // The lookup getter also materialises the character table when the asset was never read;
            // reading it first makes the table access below safe for assets created by TMP itself.
            Dictionary<uint, TMP_Character> lookup = asset.characterLookupTable;
            List<TMP_Character> table = asset.characterTable;
            if (table == null || lookup == null)
            {
                return 0;
            }

            List<TMP_Character> kept = null;
            int removed = 0;
            uint firstRemoved = 0;
            for (int i = 0; i < table.Count; i++)
            {
                TMP_Character entry = table[i];
                if (entry == null)
                {
                    continue;
                }

                if (CjkText.IsCjk(entry.unicode))
                {
                    if (kept != null)
                    {
                        kept.Add(entry);
                    }

                    continue;
                }

                if (kept == null)
                {
                    kept = new List<TMP_Character>(table.Count);
                    for (int j = 0; j < i; j++)
                    {
                        kept.Add(table[j]);
                    }
                }

                if (removed == 0)
                {
                    firstRemoved = entry.unicode;
                }

                lookup.Remove(entry.unicode);
                removed++;
            }

            if (removed > 0)
            {
                table.Clear();
                table.AddRange(kept);

                // The lookup table is what TMP actually asks, so verify it really lost the entry. A
                // future TMP that hands out copies instead of the live tables is repaired through the
                // supported API, at the price of rebuilding this asset's glyphs once.
                Dictionary<uint, TMP_Character> verify = asset.characterLookupTable;
                if (verify != null && verify.ContainsKey(firstRemoved))
                {
                    LogWarning("TMP character tables are not writable in this version; rebuilding '" +
                        asset.name + "' instead of stripping it.");
                    asset.ClearFontAssetData(false);
                }
            }

            return removed;
        }

        /// <summary>Strips one asset the first time it is seen after the option was switched on.</summary>
        internal static int EnsurePure(TMP_FontAsset asset)
        {
            if (asset == null || !IsActive || !Purified.Add(asset))
            {
                return 0;
            }

            return Purge(asset);
        }

        /// <summary>
        /// Strips every plugin asset that still owns non-CJK glyphs, at most once per activation.
        /// Called once per pass while the option is on so a font built (or reused) later is covered
        /// without another code path.
        /// </summary>
        internal static int PurgeAll()
        {
            int removed = 0;
            foreach (TMP_FontAsset font in FontLoaderPlugin.KnownFontAssets)
            {
                if (font == null || !Purified.Add(font))
                {
                    continue;
                }

                removed += Purge(font);
            }

            return removed;
        }

        /// <summary>
        /// Hands back only the glyphs the native fonts can render now. Unlike PurgeAll this is safe to
        /// run on every pass: a character no game font provides stays in the plugin font (no boxes),
        /// while one that became reachable - for example because a font of the new scene was snapshotted
        /// after the text was first laid out - is returned to the game font.
        /// </summary>
        internal static int PurgeRecoverable()
        {
            int removed = 0;
            foreach (TMP_FontAsset font in FontLoaderPlugin.KnownFontAssets)
            {
                if (font != null)
                {
                    removed += PurgeRecoverable(font);
                }
            }

            return removed;
        }

        private static int PurgeRecoverable(TMP_FontAsset asset)
        {
            List<TMP_Character> table = asset.characterTable;
            Dictionary<uint, TMP_Character> lookup = asset.characterLookupTable;
            if (table == null || lookup == null)
            {
                return 0;
            }

            List<TMP_Character> kept = null;
            int removed = 0;
            for (int i = 0; i < table.Count; i++)
            {
                TMP_Character entry = table[i];
                if (entry == null)
                {
                    continue;
                }

                if (CjkText.IsCjk(entry.unicode) || !AnyNativeProviderHasGlyph(entry.unicode))
                {
                    if (kept != null)
                    {
                        kept.Add(entry);
                    }

                    continue;
                }

                if (kept == null)
                {
                    kept = new List<TMP_Character>(table.Count);
                    for (int j = 0; j < i; j++)
                    {
                        kept.Add(table[j]);
                    }
                }

                lookup.Remove(entry.unicode);
                removed++;
            }

            if (removed > 0)
            {
                table.Clear();
                table.AddRange(kept);
            }

            return removed;
        }

        /// <summary>
        /// Forces the next PurgeAll to look again. Used when the option is switched on and whenever
        /// the fallback chain changes, so a glyph that leaked in earlier is handed back.
        /// </summary>
        internal static void InvalidatePurity()
        {
            Purified.Clear();
        }

        private static void LogInfo(string message)
        {
            if (FontLoaderPlugin.Log != null)
            {
                FontLoaderPlugin.Log.LogInfo(message);
            }
        }

        private static void LogWarning(string message)
        {
            if (FontLoaderPlugin.Log != null)
            {
                FontLoaderPlugin.Log.LogWarning(message);
            }
        }
    }

    /// <summary>
    /// Character classification for "CJK is ours, everything else stays native". The ranges are
    /// deliberately the single place to edit when that rule needs adjusting: fullwidth forms are
    /// treated as CJK because they belong to Chinese typography, while general punctuation,
    /// symbols and emoji stay on the game font.
    /// </summary>
    internal static class CjkText
    {
        internal static bool IsCjk(uint codePoint)
        {
            return (codePoint >= 0x2E80 && codePoint <= 0x2FFF) ||   // radicals, Kangxi, IDS
                   (codePoint >= 0x3000 && codePoint <= 0x303F) ||   // CJK symbols and punctuation
                   (codePoint >= 0x3040 && codePoint <= 0x30FF) ||   // hiragana, katakana
                   (codePoint >= 0x3100 && codePoint <= 0x31FF) ||   // bopomofo, kanbun, strokes
                   (codePoint >= 0x3200 && codePoint <= 0x33FF) ||   // enclosed CJK, compatibility
                   (codePoint >= 0x3400 && codePoint <= 0x4DBF) ||   // extension A
                   (codePoint >= 0x4E00 && codePoint <= 0x9FFF) ||   // unified ideographs
                   (codePoint >= 0xF900 && codePoint <= 0xFAFF) ||   // compatibility ideographs
                   (codePoint >= 0xFE10 && codePoint <= 0xFE1F) ||   // vertical forms
                   (codePoint >= 0xFE30 && codePoint <= 0xFE4F) ||   // compatibility forms
                   (codePoint >= 0xFF00 && codePoint <= 0xFFEF) ||   // halfwidth and fullwidth forms
                   (codePoint >= 0x1F200 && codePoint <= 0x1F2FF) || // enclosed ideographic supplement
                   (codePoint >= 0x20000 && codePoint <= 0x3FFFF);   // extensions B-G
        }

        /// <summary>
        /// True when the text is safe for whole-string replacement on engines without per-glyph
        /// fallback (legacy UI.Text): at least one CJK character and nothing else besides whitespace.
        /// </summary>
        internal static bool IsPureCjk(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            bool hasCjk = false;
            for (int i = 0; i < text.Length; i++)
            {
                char character = text[i];
                if (char.IsWhiteSpace(character))
                {
                    continue;
                }

                if (IsCjk(character))
                {
                    hasCjk = true;
                    continue;
                }

                if (char.IsHighSurrogate(character) && i + 1 < text.Length &&
                    char.IsLowSurrogate(text[i + 1]))
                {
                    if (!IsCjk((uint)char.ConvertToUtf32(character, text[i + 1])))
                    {
                        return false;
                    }

                    hasCjk = true;
                    i++;
                    continue;
                }

                return false;
            }

            return hasCjk;
        }

        /// <summary>True when the string contains at least one CJK character anywhere.</summary>
        internal static bool ContainsCjk(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            for (int i = 0; i < text.Length; i++)
            {
                char character = text[i];
                if (character < 0x2E80)
                {
                    // ASCII, Latin, Greek, Cyrillic, general punctuation: never CJK.
                    continue;
                }

                if (IsCjk(character))
                {
                    return true;
                }

                if (char.IsHighSurrogate(character) && i + 1 < text.Length &&
                    char.IsLowSurrogate(text[i + 1]))
                {
                    if (IsCjk((uint)char.ConvertToUtf32(character, text[i + 1])))
                    {
                        return true;
                    }

                    i++;
                }
            }

            return false;
        }
    }
}
