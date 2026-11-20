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
// only runs for the font assets this plugin created, and it refuses every non-CJK character.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;

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
        private static bool providersChanged;

        /// <summary>True after the "no native coverage" warning was written, so it is logged once.</summary>
        private static bool reportedEmptyProviders;

        /// <summary>Code points already reported as mapped to an empty glyph, so each is logged once.</summary>
        private static readonly HashSet<uint> LoggedBlankGlyphs = new HashSet<uint>();

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

                // The prefix is the guard itself; the postfix is a diagnostic. Patch them separately so
                // a binding problem in the diagnostic cannot leave the plugin without its guard.
                Harmony harmony = new Harmony("fenchen.cu-font-loader.nativeglyph");
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(NativeGlyphGuard), nameof(BeforeAddCharacter)));
                s_harmony = harmony;

                try
                {
                    harmony.Patch(target, postfix: new HarmonyMethod(typeof(NativeGlyphGuard),
                        nameof(AfterAddCharacter)));
                }
                catch (Exception ex)
                {
                    LogWarning("Empty-glyph diagnostics not installed: " + ex.Message);
                }

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
        /// in the game's own font. An exception fails open (the glyph is added) so a guard bug can
        /// never leave text blank; the CJK classification itself is strict.
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

                if (CjkText.IsCjkGlyph(unicode))
                {
                    return true;
                }

                // Non-CJK stays native even when no game font can draw it: a missing glyph is
                // preferable to the plugin font owning a character it does not classify as CJK.
                // This also covers TMP_Settings' global fallback list, which bypasses the chain
                // configured on the text's own font.
                character = null;
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// Reports a CJK character that the plugin font maps to an empty outline. TMP counts such a
        /// character as found, so it renders blank instead of falling back to a game font; that is a
        /// font-quality problem the plugin cannot fix from here, and it is worth finding during
        /// translation QA. Logged once per code point, and only with the detailed switches on.
        /// </summary>
        private static void AfterAddCharacter(TMP_FontAsset __instance, uint unicode, bool __result,
            ref TMP_Character character)
        {
            try
            {
                if (!__result || character == null || __instance == null ||
                    !FontLoaderPlugin.IsKnownFont(__instance) || !CjkText.IsCjk(unicode) ||
                    (unicode <= 0xFFFF && char.IsWhiteSpace((char)unicode)))
                {
                    return;
                }

                Glyph glyph = character.glyph;
                if (glyph == null || (glyph.glyphRect.width > 0f && glyph.glyphRect.height > 0f))
                {
                    return;
                }

                bool detailed = (FontLoaderPlugin.CompatibilityLog != null &&
                                 FontLoaderPlugin.CompatibilityLog.Value) ||
                                (FontLoaderPlugin.LogTextDetails != null &&
                                 FontLoaderPlugin.LogTextDetails.Value);
                if (!detailed || LoggedBlankGlyphs.Count >= 200 || !LoggedBlankGlyphs.Add(unicode))
                {
                    return;
                }

                LogWarning("[Native] '" + __instance.name + "' maps U+" + unicode.ToString("X4") +
                    " to an empty glyph; it renders blank instead of falling back to a game font.");
            }
            catch (Exception)
            {
                // Diagnostics must never disturb TMP's glyph path.
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
                providersChanged = true;

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

                if (CjkText.IsCjkGlyph(entry.unicode))
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

        /// <summary>True once after the reachable native fonts changed; the caller can then run
        /// PurgeRecoverable instead of scanning every plugin glyph on every scan pass.</summary>
        internal static bool TakeProvidersChanged()
        {
            bool changed = providersChanged;
            providersChanged = false;
            return changed;
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

                if (CjkText.IsCjkGlyph(entry.unicode) || !AnyNativeProviderHasGlyph(entry.unicode))
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
    /// Character classification for "CJK is ours, everything else stays native": Chinese and Japanese
    /// ideographs, Japanese kana, Korean Hangul, and the punctuation and fullwidth forms that belong to
    /// those scripts. Shared punctuation (curly quotes, ellipsis, middle dot, dashes, reference mark)
    /// is no longer treated as CJK: it stays on the game font, and a legacy pure-CJK label may not
    /// contain it. Other general punctuation, symbols and emoji stay on the game font.
    /// </summary>
    internal static class CjkText
    {
        internal static bool IsCjk(uint codePoint)
        {
            return (codePoint >= 0x1100 && codePoint <= 0x11FF) ||   // Hangul Jamo
                   (codePoint >= 0x2E80 && codePoint <= 0x2FFF) ||   // radicals, Kangxi, IDS
                   (codePoint >= 0x3000 && codePoint <= 0x303F) ||   // CJK symbols and punctuation
                   (codePoint >= 0x3040 && codePoint <= 0x30FF) ||   // hiragana, katakana
                   (codePoint >= 0x3100 && codePoint <= 0x31FF) ||   // bopomofo, kanbun, Hangul compat Jamo
                   (codePoint >= 0x3200 && codePoint <= 0x33FF) ||   // enclosed CJK, compatibility
                   (codePoint >= 0x3400 && codePoint <= 0x4DBF) ||   // extension A
                   (codePoint >= 0x4E00 && codePoint <= 0x9FFF) ||   // unified ideographs
                   (codePoint >= 0xA960 && codePoint <= 0xA97F) ||   // Hangul Jamo Extended-A
                   (codePoint >= 0xAC00 && codePoint <= 0xD7A3) ||   // Hangul syllables
                   (codePoint >= 0xD7B0 && codePoint <= 0xD7FF) ||   // Hangul Jamo Extended-B
                   (codePoint >= 0xF900 && codePoint <= 0xFAFF) ||   // compatibility ideographs
                   (codePoint >= 0xFE10 && codePoint <= 0xFE1F) ||   // vertical forms
                   (codePoint >= 0xFE30 && codePoint <= 0xFE4F) ||   // compatibility forms
                   (codePoint >= 0xFE50 && codePoint <= 0xFE6F) ||   // small form variants
                   (codePoint >= 0xFF00 && codePoint <= 0xFFEF) ||   // halfwidth and fullwidth forms
                   (codePoint >= 0x1F200 && codePoint <= 0x1F2FF) || // enclosed ideographic supplement
                   (codePoint >= 0x20000 && codePoint <= 0x3FFFF);   // extensions B-G
        }

        /// <summary>
        /// Disabled: shared punctuation (middle dot, curly quotes, ellipsis, dashes, reference mark) is
        /// no longer treated as CJK. Kept as a no-op so the call sites stay in one place and the list
        /// below can be restored if the old behaviour is wanted again.
        /// </summary>
        internal static bool IsCjkPunctuation(uint codePoint)
        {
            return false;

            /*
            return codePoint == 0x00B7 ||   // middle dot
                   codePoint == 0x2018 ||   // left single quotation mark
                   codePoint == 0x2019 ||   // right single quotation mark
                   codePoint == 0x201C ||   // left double quotation mark
                   codePoint == 0x201D ||   // right double quotation mark
                   codePoint == 0x2025 ||   // two dot leader
                   codePoint == 0x2026 ||   // ellipsis
                   codePoint == 0x2027 ||   // hyphenation point
                   codePoint == 0x203B;     // reference mark
            */
        }

        internal static bool IsCjkGlyph(uint codePoint)
        {
            // Shared punctuation is disabled; only characters inside the CJK ranges count.
            return IsCjk(codePoint);
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

                // Shared punctuation is disabled, so a label containing quotes, ellipsis or a dash is
                // no longer eligible for whole-string replacement on legacy UI.Text.
                /*
                if (IsCjkPunctuation(character))
                {
                    // Shared punctuation is allowed around a CJK label, but does not make a
                    // punctuation-only string count as CJK.
                    continue;
                }
                */

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
                if (character < 0x1100)
                {
                    // ASCII, Latin, Greek, Cyrillic and everything below the first Hangul block is
                    // never CJK, which keeps the common case to one comparison per character.
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
