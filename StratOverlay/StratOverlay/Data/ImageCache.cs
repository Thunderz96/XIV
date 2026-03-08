// =============================================================================
// ImageCache.cs — Download, Disk Cache, and Texture Management
// =============================================================================
//
// ROOT CAUSE FIX (v2):
//   wtfdig.info serves images as WebP regardless of the .png extension in the URL.
//   Dalamud's GetFromFile() only handles PNG/JPG/TEX — it silently returns null
//   for WebP. Fix: load raw bytes from disk and pass them to
//   ITextureProvider.CreateFromImageAsync(), which handles WebP, PNG, JPG, etc.
//
// THREADING MODEL:
//   - Downloads run on background Tasks (never on game thread)
//   - Texture creation (CreateFromImageAsync) also runs on background Tasks
//   - GetTexture() is called every draw frame on the UI thread; it just does
//     dict lookups and picks up completed textures from readyTextures queue
//   - No blocking I/O or async work ever happens on the draw thread
//
// SPEED:
//   - On startup, ScanCacheDirectory() reads .key sidecar files and kicks off
//     background texture loads for everything already on disk — so cached
//     images from previous sessions are GPU-ready before you enter combat
//   - PreWarm downloads all images in parallel (Task.WhenAll), then immediately
//     kicks off background texture loading for each one
//   - GetTexture() itself is essentially free: two dict lookups + optional
//     dequeue from a ConcurrentQueue
// =============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

using StratOverlay.Data;

namespace StratOverlay;

public class ImageCache : IDisposable
{
    // ---- Dependencies injected from Plugin.cs ----
    private readonly ITextureProvider TextureProvider;
    private readonly IPluginLog       Log;

    // ---- One shared HTTP client for all downloads ----
    // HttpClient must be long-lived to avoid socket exhaustion.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    // ---- Where cached files live on disk ----
    private readonly string CacheDir;

    // ---- resolvedPaths: cache-key → absolute file path on disk ----
    // Set by background download/scan tasks. Read by draw thread + texture loader.
    // ConcurrentDictionary = safe for concurrent read AND write from any thread.
    private readonly ConcurrentDictionary<string, string> resolvedPaths = new();

    // ---- texturePending: file paths currently being loaded into GPU memory ----
    // We add a path here before starting CreateFromImageAsync so we never
    // start two loads for the same file.
    private readonly HashSet<string> texturePending = new();

    // ---- readyTextures: completed texture loads waiting to be picked up ----
    // Background tasks enqueue here; GetTexture() dequeues on the draw thread.
    // ConcurrentQueue = lock-free, safe across threads.
    private readonly ConcurrentQueue<(string filePath, IDalamudTextureWrap tex)> readyTextures = new();

    // ---- textures: the hot lookup table used every draw frame ----
    // Only accessed on the draw thread after being populated from readyTextures.
    private readonly Dictionary<string, IDalamudTextureWrap> textures = new();

    // =========================================================================
    // CONSTRUCTOR / DISPOSE
    // =========================================================================

    public ImageCache(ITextureProvider textureProvider, IPluginLog log, string pluginConfigDir)
    {
        TextureProvider = textureProvider;
        Log             = log;
        CacheDir        = Path.Combine(pluginConfigDir, "cache");
        Directory.CreateDirectory(CacheDir);

        // Kick off background texture loading for everything already on disk.
        // This means images from previous sessions are GPU-ready before combat.
        ScanAndPreloadCacheDirectory();

        Log.Info($"[ImageCache] Ready. Cache dir: {CacheDir}");
    }

    public void Dispose()
    {
        // Drain any textures that finished loading after we started disposing
        while (readyTextures.TryDequeue(out var item))
            item.tex.Dispose();

        foreach (var tex in textures.Values)
            tex.Dispose();

        textures.Clear();
        resolvedPaths.Clear();
        texturePending.Clear();
        Log.Info("[ImageCache] Disposed.");
    }

    // =========================================================================
    // STARTUP: scan cache dir and pre-load all known textures in background
    // =========================================================================

    private void ScanAndPreloadCacheDirectory()
    {
        _ = Task.Run(() =>
        {
            try
            {
                // Each cached image has a .key sidecar file containing the original
                // cache key (e.g. "wtfdig::74/m9s/limit-cut::all").
                // We read the key, find the matching image file, register the path,
                // then kick off a background texture load.
                foreach (var keyFile in Directory.EnumerateFiles(CacheDir, "*.key"))
                {
                    var cacheKey = File.ReadAllText(keyFile).Trim();
                    var stem     = Path.GetFileNameWithoutExtension(keyFile);

                    // Find the image file with the same name stem (any image extension)
                    var imageFile = Directory.EnumerateFiles(CacheDir, stem + ".*")
                        .FirstOrDefault(f => !f.EndsWith(".key") && !f.EndsWith(".tmp"));

                    if (imageFile == null || !File.Exists(imageFile)) continue;

                    resolvedPaths[cacheKey] = imageFile;
                    StartTextureLoad(imageFile); // async, fire-and-forget
                }

                Log.Info($"[ImageCache] Startup scan complete: {resolvedPaths.Count} cached images found.");
            }
            catch (Exception ex)
            {
                Log.Warning($"[ImageCache] Startup scan error: {ex.Message}");
            }
        });
    }

    // =========================================================================
    // PRE-WARM: download all images for a timeline in parallel
    // =========================================================================

    /// <summary>
    /// Downloads all images for a timeline simultaneously in background tasks.
    /// Already-cached images skip straight to texture loading.
    /// Call this at zone entry or when manually loading a timeline.
    /// Safe to call multiple times — duplicate work is skipped.
    /// </summary>
    public void PreWarm(StratTimeline timeline)
    {
        _ = Task.Run(async () =>
        {
            var downloadTasks = new List<Task>();

            foreach (var entry in timeline.Entries)
            foreach (var (_, image) in entry.Images)
            {
                if (string.IsNullOrWhiteSpace(image.Path)) continue;

                var key = GetCacheKey(image);

                // Already downloaded and registered
                if (resolvedPaths.ContainsKey(key))
                {
                    // Make sure texture loading was kicked off
                    if (resolvedPaths.TryGetValue(key, out var fp))
                        StartTextureLoad(fp);
                    continue;
                }

                // Local file — just register, no download needed
                if (image.Source == ImageSource.LocalFile)
                {
                    if (File.Exists(image.Path))
                    {
                        resolvedPaths[key] = image.Path;
                        StartTextureLoad(image.Path);
                    }
                    continue;
                }

                // Already on disk from a previous session (no .key file yet)
                var cachedPath = GetCachedFilePath(key, image);
                if (File.Exists(cachedPath))
                {
                    resolvedPaths[key] = cachedPath;
                    StartTextureLoad(cachedPath);
                    continue;
                }

                // Need to download — capture loop variables for the lambda
                var capturedImage = image;
                var capturedKey   = key;
                var capturedPath  = cachedPath;

                downloadTasks.Add(Task.Run(async () =>
                {
                    bool ok = await DownloadAsync(capturedImage, capturedPath);
                    if (ok)
                    {
                        resolvedPaths[capturedKey] = capturedPath;
                        StartTextureLoad(capturedPath);
                    }
                    else
                    {
                        capturedImage.LoadFailed = true;
                    }
                }));
            }

            if (downloadTasks.Count > 0)
            {
                Log.Info($"[ImageCache] Pre-warming {downloadTasks.Count} new images for \"{timeline.Name}\"...");
                await Task.WhenAll(downloadTasks);
                Log.Info($"[ImageCache] Pre-warm done for \"{timeline.Name}\".");
            }
            else
            {
                Log.Info($"[ImageCache] Pre-warm for \"{timeline.Name}\": all images already cached.");
            }
        });
    }

    // =========================================================================
    // FORCE REFRESH: re-download and reload one image
    // =========================================================================

    public void ForceRefresh(StratImage image)
    {
        var key        = GetCacheKey(image);
        var cachedPath = GetCachedFilePath(key, image);

        // Evict old texture — safe here because this is called from the UI thread
        if (textures.TryGetValue(cachedPath, out var oldTex))
        {
            oldTex.Dispose();
            textures.Remove(cachedPath);
        }

        resolvedPaths.TryRemove(key, out _);
        texturePending.Remove(cachedPath);
        image.LoadFailed = false;

        _ = Task.Run(async () =>
        {
            bool ok = await DownloadAsync(image, cachedPath);
            if (ok)
            {
                resolvedPaths[key] = cachedPath;
                StartTextureLoad(cachedPath);
            }
            else
            {
                image.LoadFailed = true;
            }
        });
    }

    // =========================================================================
    // GET TEXTURE: called every draw frame — must be very fast
    // =========================================================================

    /// <summary>
    /// Returns a GPU texture ready to draw, or null if not ready yet.
    ///
    /// This method does three things:
    ///   1. Drain the readyTextures queue (moves finished loads into the hot dict)
    ///   2. Check the hot dict (fast path, O(1))
    ///   3. Return null if download/load isn't done yet
    ///
    /// Must be called from the Framework/UI thread.
    /// </summary>
    public IDalamudTextureWrap? GetTexture(StratImage image)
    {
        if (image.LoadFailed) return null;

        // Step 1: pick up any textures that finished loading since last frame
        // This is very cheap — ConcurrentQueue.TryDequeue is nearly free when empty
        while (readyTextures.TryDequeue(out var ready))
            textures[ready.filePath] = ready.tex;

        var key = GetCacheKey(image);
        if (!resolvedPaths.TryGetValue(key, out var filePath)) return null;

        // Step 2: hot path — texture already loaded
        if (textures.TryGetValue(filePath, out var existing)) return existing;

        // Step 3: file exists but texture load hasn't finished yet (or hasn't started)
        // Kick off the load if it somehow wasn't started already
        StartTextureLoad(filePath);
        return null;
    }

    /// <summary>True if the file is on disk and registered (ready to texture-load).</summary>
    public bool IsReady(StratImage image)
        => !image.LoadFailed && resolvedPaths.ContainsKey(GetCacheKey(image));

    // =========================================================================
    // DIAGNOSTICS
    // =========================================================================

    /// <summary>
    /// Downloads a URL and returns a human-readable status string.
    /// Used from the Debug tab "Test URL" button.
    /// </summary>
    public async Task<string> TestDownloadAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 StratOverlay/1.0");
            using var res = await Http.SendAsync(req);
            var ct    = res.Content.Headers.ContentType?.MediaType ?? "?";
            var bytes = await res.Content.ReadAsByteArrayAsync();
            return res.IsSuccessStatusCode
                ? $"OK {(int)res.StatusCode} — {ct} — {bytes.Length:N0} bytes"
                : $"FAIL {(int)res.StatusCode} {res.ReasonPhrase}";
        }
        catch (Exception ex)
        {
            return $"EXCEPTION: {ex.Message}";
        }
    }

    // =========================================================================
    // PRIVATE: background texture loading
    // =========================================================================

    /// <summary>
    /// Starts a background Task that reads the file from disk and calls
    /// ITextureProvider.CreateFromImageAsync (supports PNG, JPG, WebP, etc.).
    /// The completed texture is placed in readyTextures for GetTexture() to pick up.
    ///
    /// Safe to call from any thread.
    /// Guards against double-loading with texturePending.
    /// </summary>
    private void StartTextureLoad(string filePath)
    {
        // Guard: already loading or already loaded
        lock (texturePending)
        {
            if (texturePending.Contains(filePath)) return;
            if (textures.ContainsKey(filePath))    return;
            texturePending.Add(filePath);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    Log.Warning($"[ImageCache] File missing for texture load: {filePath}");
                    return;
                }

                // Read raw bytes — works for ANY format (PNG, JPG, WebP, etc.)
                var bytes = await File.ReadAllBytesAsync(filePath);

                // CreateFromImageAsync decodes the image bytes and uploads to GPU.
                // This is the correct API for format-agnostic loading in Dalamud.
                var tex = await TextureProvider.CreateFromImageAsync(bytes);

                if (tex != null)
                {
                    readyTextures.Enqueue((filePath, tex));
                    Log.Debug($"[ImageCache] Texture ready: {Path.GetFileName(filePath)}");
                }
                else
                {
                    Log.Warning($"[ImageCache] CreateFromImageAsync returned null for: {filePath}");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[ImageCache] Texture load error for {filePath}: {ex.Message}");
            }
        });
    }

    // =========================================================================
    // PRIVATE: download helpers
    // =========================================================================

    private async Task<bool> DownloadAsync(StratImage image, string destPath)
    {
        var url = GetRemoteUrl(image);
        Log.Debug($"[ImageCache] Downloading: {url}");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 StratOverlay/1.0");

            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode)
            {
                Log.Warning($"[ImageCache] HTTP {(int)res.StatusCode} for {url}");
                return false;
            }

            // Write to .tmp first, then rename — prevents corrupt files on crash
            var tmpPath = destPath + ".tmp";
            await using (var fs = File.Create(tmpPath))
                await res.Content.CopyToAsync(fs);

            File.Move(tmpPath, destPath, overwrite: true);

            // Sidecar .key file lets ScanAndPreloadCacheDirectory re-register
            // this image on the next plugin load without re-downloading.
            await File.WriteAllTextAsync(
                Path.ChangeExtension(destPath, ".key"),
                GetCacheKey(image));

            Log.Info($"[ImageCache] Downloaded: {Path.GetFileName(destPath)} ← {url}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning($"[ImageCache] Download failed for {url}: {ex.Message}");
            return false;
        }
    }

    private static string GetCacheKey(StratImage image)
    {
        // For WtfDig, Path already contains the full fightId/filename,
        // so it's already unique. WtfDigVariant is kept for labelling only.
        if (image.Source == ImageSource.WtfDig)
            return $"wtfdig::{image.Path}";
        return image.Path;
    }

    private string GetCachedFilePath(string key, StratImage image)
    {
        // Hash the key to get a safe, deterministic filename with no special chars
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var hex  = Convert.ToHexString(hash).ToLowerInvariant();
        // Always save as .bin — actual format detected by image bytes, not extension
        return Path.Combine(CacheDir, hex + ".bin");
    }

    private static string GetRemoteUrl(StratImage image)
    {
        if (image.Source == ImageSource.WtfDig)
        {
            // image.Path stores the full relative path including filename,
            // e.g. "74/m12s/p1-toxic-act1-dps-zoomed.webp"
            // The manifest builder writes the complete path so no assembly needed.
            return $"https://wtfdig.info/{image.Path}";
        }
        return image.Path;
    }
}
