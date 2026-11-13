using System;
using System.Collections.Generic;
using System.IO;

namespace Epic_Games_Account_Switcher_Globals
{
    /// <summary>
    /// Recognises throw-away browser cache inside Epic's embedded Chromium profiles (launcher "WebCaches", the overlay
    /// "BrowserCache" and the EOS "UI Helper"). That cache is often 90% of a saved profile and is rebuilt automatically, so profiles and backups skip it.
    /// Sign-in state (Cookies, Local Storage, IndexedDB, Session Storage) is never matched and is always kept.
    /// </summary>
    public static class CacheFilter
    {
        private static readonly HashSet<string> BrowserRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WebCaches", "BrowserCache", "UI Helper"
        };

        private static readonly HashSet<string> DisposableFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "GraphiteDawnCache",
            "GrShaderCache", "ShaderCache", "CacheStorage", "ScriptCache", "blob_storage", "Crashpad",
            "component_crx_cache", "CertificateRevocation", "OnDeviceHeadSuggestModel", "OptimizationHints",
            "optimization_guide_model_store", "hyphen-data", "ZxcvbnData", "Crowd Deny", "SafetyTips",
            "Subresource Filter", "PKIMetadata", "AutofillStates", "segmentation_platform", "BrowserMetrics"
        };

        /// <summary>True when <paramref name="path"/> (file or folder, absolute) is disposable browser cache.</summary>
        public static bool IsDisposable(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var parts = path.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            var insideBrowser = false;
            for (var i = 0; i < parts.Length; i++)
            {
                if (!insideBrowser)
                {
                    insideBrowser = BrowserRoots.Contains(parts[i]);
                    continue;
                }
                if (DisposableFolders.Contains(parts[i])) return true;
                if (i == parts.Length - 1 && parts[i].EndsWith(".pma", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Deletes disposable cache folders below <paramref name="root"/>. Returns the bytes freed.</summary>
        public static long PruneDisposable(string root)
        {
            long freed = 0;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return 0;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                string[] children;
                try { children = Directory.GetDirectories(dir); } catch { continue; }
                foreach (var child in children)
                {
                    if (!IsDisposable(child)) { pending.Push(child); continue; }
                    try
                    {
                        long size = 0;
                        foreach (var f in Directory.EnumerateFiles(child, "*", SearchOption.AllDirectories))
                            try { size += new FileInfo(f).Length; } catch { }
                        Globals.ForceDeleteDirectory(child);
                        if (!Directory.Exists(child)) freed += size;
                    }
                    catch { }
                }
                string[] files;
                try { files = Directory.GetFiles(dir); } catch { continue; }
                foreach (var file in files)
                {
                    if (!IsDisposable(file)) continue;
                    try { var size = new FileInfo(file).Length; File.Delete(file); freed += size; } catch { }
                }
            }
            return freed;
        }
    }
}
