// cu-font-loader - new-text discovery.
// Licensed under MIT License
// https://github.com/FenChen0211/cu-font-loader
//
// A newly enabled text used to wait for the next scheduled scan (up to ScanIntervalSeconds) before it
// was considered. This covers both modes: PreserveNonCjk only has to hook the text font's fallback
// chain once, while replacement modes decide and apply the font. The OnEnable postfix queues the text
// and the queue is flushed every frame, so it is handled on the next frame instead.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;

namespace CuFontLoader
{
    internal static class TextPipelineHooks
    {
        private static Harmony s_harmony;
        private static bool discoveryInstalled;

        /// <summary>Texts enabled since the last flush; a set so a pooled text is queued once.</summary>
        private static readonly HashSet<TMP_Text> DiscoveryQueue = new HashSet<TMP_Text>();

        /// <summary>Reused flush buffer: the queue is cleared before the texts are processed.</summary>
        private static readonly List<TMP_Text> DiscoveryBuffer = new List<TMP_Text>();

        internal static bool DiscoveryInstalled
        {
            get { return discoveryInstalled; }
        }

        internal static void Install()
        {
            if (s_harmony != null)
            {
                return;
            }

            try
            {
                Harmony harmony = new Harmony("fenchen.cu-font-loader.pipeline");
                discoveryInstalled = InstallDiscovery(harmony);
                if (discoveryInstalled)
                {
                    s_harmony = harmony;
                }
            }
            catch (Exception ex)
            {
                LogWarning("Text pipeline hooks not installed: " + ex.Message);
            }
        }

        /// <summary>Hooks the enable event of both TMP renderers, so new texts are seen next frame.</summary>
        private static bool InstallDiscovery(Harmony harmony)
        {
            bool ugui = PatchOnEnable(harmony, typeof(TextMeshProUGUI));
            bool world = PatchOnEnable(harmony, typeof(TextMeshPro));
            return ugui || world;
        }

        private static bool PatchOnEnable(Harmony harmony, Type rendererType)
        {
            try
            {
                MethodInfo target = AccessTools.Method(rendererType, "OnEnable", Type.EmptyTypes);
                if (target == null)
                {
                    LogWarning(rendererType.Name + ".OnEnable not found; new texts are discovered by " +
                        "the scheduled scan only.");
                    return false;
                }

                harmony.Patch(target,
                    postfix: new HarmonyMethod(typeof(TextPipelineHooks), nameof(AfterTextEnabled)));
                return true;
            }
            catch (Exception ex)
            {
                LogWarning(rendererType.Name + ".OnEnable hook not installed: " + ex.Message);
                return false;
            }
        }

        private static void AfterTextEnabled(TMP_Text __instance)
        {
            try
            {
                if (__instance == null || !FontLoaderPlugin.IsReplacementEnabled())
                {
                    return;
                }

                DiscoveryQueue.Add(__instance);
            }
            catch (Exception)
            {
                // A queued text is only an optimisation; never let it break the enable path.
            }
        }

        /// <summary>Applies the normal decision to every text that became active since the last frame.</summary>
        internal static void Flush()
        {
            if (DiscoveryQueue.Count == 0)
            {
                return;
            }

            DiscoveryBuffer.Clear();
            DiscoveryBuffer.AddRange(DiscoveryQueue);
            DiscoveryQueue.Clear();

            for (int i = 0; i < DiscoveryBuffer.Count; i++)
            {
                TMP_Text text = DiscoveryBuffer[i];
                if (text == null)
                {
                    continue;
                }

                FontLoaderPlugin.ApplySingleText(text);
            }

            DiscoveryBuffer.Clear();
        }

        internal static void ClearQueue()
        {
            DiscoveryQueue.Clear();
            DiscoveryBuffer.Clear();
        }

        private static void LogWarning(string message)
        {
            if (FontLoaderPlugin.Log != null)
            {
                FontLoaderPlugin.Log.LogWarning(message);
            }
        }
    }
}

