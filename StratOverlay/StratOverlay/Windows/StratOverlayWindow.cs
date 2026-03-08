// =============================================================================
// StratOverlayWindow.cs — On-Screen Image Display
// =============================================================================
//
// This window covers the full viewport and draws strat images on screen.
// It is always click-through EXCEPT when edit mode is enabled (which is
// toggled from the Settings tab of the main window, not here).
//
// EDIT MODE:
//   Toggled via StratOverlayWindow.EditMode property (set by MainWindow).
//   When on: overlay receives mouse input, drag to reposition, scroll to resize.
//   When off: fully transparent to all mouse/keyboard input.
//
// DISPLAY MODEL:
//   OnStratTriggered fires → ActiveDisplay added to _activeDisplays list.
//   Draw() stacks them vertically from the configured anchor/position.
//   Expired entries are removed each frame.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace StratOverlay.Windows;

// ---- One active image being displayed on screen ----
internal class ActiveDisplay
{
    public required Data.StratEntry  Entry         { get; init; }
    public required Data.StratImage  ResolvedImage { get; init; }
    public required float            FightTime     { get; init; }
    public readonly Stopwatch Age = Stopwatch.StartNew();
}

public class StratOverlayWindow : Window, IDisposable
{
    private readonly Plugin Plugin;

    private readonly List<ActiveDisplay> _activeDisplays = new();

    // ---- Edit-mode state (toggled from MainWindow Settings tab) ----
    // When true: overlay receives mouse input for drag/resize.
    // When false: fully click-through.
    public  bool    EditMode    { get; set; } = false;
    private bool    _dragging   = false;
    private Vector2 _dragOffset = Vector2.Zero;

    // =========================================================================
    // CONSTRUCTOR / DISPOSE
    // =========================================================================

    public StratOverlayWindow(Plugin plugin)
        : base("##StratOverlayWindow",
               ImGuiWindowFlags.NoDecoration        |
               ImGuiWindowFlags.NoBackground        |
               ImGuiWindowFlags.NoNav               |
               ImGuiWindowFlags.NoFocusOnAppearing  |
               ImGuiWindowFlags.NoBringToFrontOnFocus |
               ImGuiWindowFlags.NoSavedSettings     |
               ImGuiWindowFlags.NoScrollbar         |
               ImGuiWindowFlags.NoScrollWithMouse)
    {
        Plugin = plugin;
        IsOpen = true;
        SizeCondition     = ImGuiCond.Always;
        PositionCondition = ImGuiCond.Always;

        Plugin.Engine.OnStratTriggered += OnStratTriggered;
        Plugin.Engine.OnStratExpired   += OnStratExpired;
        Plugin.Engine.OnCombatEnded    += OnCombatEnded;
        Plugin.Engine.OnVariantChanged += OnVariantChanged;
    }

    public void Dispose()
    {
        Plugin.Engine.OnStratTriggered -= OnStratTriggered;
        Plugin.Engine.OnStratExpired   -= OnStratExpired;
        Plugin.Engine.OnCombatEnded    -= OnCombatEnded;
        Plugin.Engine.OnVariantChanged -= OnVariantChanged;
    }

    // =========================================================================
    // PRE-DRAW — pin to full viewport
    // =========================================================================
    public override void PreDraw()
    {
        // In edit mode we need to receive mouse input, so drop NoInputs
        // Outside edit mode, the overlay is fully click-through
        Flags = ImGuiWindowFlags.NoDecoration        |
                ImGuiWindowFlags.NoBackground        |
                ImGuiWindowFlags.NoNav               |
                ImGuiWindowFlags.NoFocusOnAppearing  |
                ImGuiWindowFlags.NoBringToFrontOnFocus |
                ImGuiWindowFlags.NoSavedSettings     |
                ImGuiWindowFlags.NoScrollbar         |
                ImGuiWindowFlags.NoScrollWithMouse;

        if (!EditMode)
            Flags |= ImGuiWindowFlags.NoInputs;

        var vp = ImGui.GetMainViewport();
        Position = vp.Pos;
        Size     = vp.Size;
    }

    // =========================================================================
    // DRAW
    // =========================================================================
    public override void Draw()
    {
        var cfg = Plugin.Configuration;
        var dl  = ImGui.GetWindowDrawList();
        var vp  = ImGui.GetMainViewport();

        // ---- Expire old entries ----
        _activeDisplays.RemoveAll(d =>
            d.Entry.DisplayDuration > 0f &&
            d.Age.Elapsed.TotalSeconds > d.Entry.DisplayDuration);

        // ---- Nothing to draw if no active strats and not in edit mode preview ----
        if (_activeDisplays.Count == 0 && !EditMode) return;

        // ---- Calculate top-left of the panel stack ----
        float imgWidth = cfg.OverlayImageWidth;
        Vector2 origin = CalcOrigin(vp, cfg, imgWidth);

        // ---- Handle drag-to-reposition in edit mode ----
        if (EditMode)
            HandleDrag(cfg, origin, imgWidth);

        // ---- In edit mode with nothing active, show a placeholder preview ----
        if (EditMode && _activeDisplays.Count == 0)
        {
            DrawEditPreview(dl, origin, imgWidth, cfg);
            return;
        }

        // ---- Draw each active display stacked vertically ----
        float cursor = 0f;
        bool  stackUp = !cfg.OverlayFreePosition && cfg.OverlayAnchor >= 2;

        foreach (var display in _activeDisplays)
        {
            float alpha  = CalcAlpha(display, cfg.OverlayFadeDuration);
            float height = DrawDisplay(dl, display, origin, cursor, imgWidth, alpha, cfg);
            cursor += stackUp ? -(height + 8f) : (height + 8f);
        }
    }

    // =========================================================================
    // DRAG AND RESIZE LOGIC
    // =========================================================================

    private void HandleDrag(Configuration cfg, Vector2 origin, float imgWidth)
    {
        // Estimate panel height for hit-test (use 16:9 aspect ratio if no image)
        float estHeight = imgWidth * (9f / 16f) + 30f;
        Vector2 panelTL = origin;
        Vector2 panelBR = origin + new Vector2(imgWidth, estHeight);

        var mouse    = ImGui.GetMousePos();
        bool inPanel = mouse.X >= panelTL.X && mouse.X <= panelBR.X &&
                       mouse.Y >= panelTL.Y && mouse.Y <= panelBR.Y;

        // ---- Scroll to resize ----
        if (inPanel)
        {
            float scroll = ImGui.GetIO().MouseWheel;
            if (Math.Abs(scroll) > 0.01f)
            {
                cfg.OverlayImageWidth = Math.Clamp(cfg.OverlayImageWidth + scroll * 10f, 100f, 1200f);
                cfg.Save();
            }
        }

        // ---- Drag to reposition ----
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && inPanel)
        {
            _dragging   = true;
            _dragOffset = mouse - panelTL;
        }

        if (_dragging)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var newTL = mouse - _dragOffset;
                cfg.OverlayFreePosition = true;
                cfg.OverlayFreeX        = newTL.X;
                cfg.OverlayFreeY        = newTL.Y;
                // Don't save every frame — save on release
            }
            else
            {
                // Mouse released
                _dragging = false;
                cfg.Save();
            }
        }
    }

    // =========================================================================
    // EDIT MODE PREVIEW (shown when no active strats)
    // =========================================================================

    private static void DrawEditPreview(ImDrawListPtr dl, Vector2 origin, float imgWidth, Configuration cfg)
    {
        float imgH = imgWidth * (9f / 16f);
        Vector2 tl = origin;
        Vector2 br = origin + new Vector2(imgWidth, imgH + 26f);

        // Dashed orange border to show the panel area
        uint borderCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.6f, 0.1f, 0.9f));
        uint bgCol     = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.45f));
        dl.AddRectFilled(tl, br, bgCol, 6f);
        dl.AddRect(tl, br, borderCol, 6f, ImDrawFlags.None, 2f);

        // Centered label
        string label = "[ Strat Image Preview ]";
        var    ts    = ImGui.CalcTextSize(label);
        uint   tc    = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.7f, 0.2f, 1f));
        dl.AddText(tl + new Vector2((imgWidth - ts.X) * 0.5f, imgH * 0.5f - 7f), tc, label);

        string sizeLabel = $"{(int)imgWidth} px wide";
        var    sl        = ImGui.CalcTextSize(sizeLabel);
        uint   sc        = ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.8f, 0.8f, 0.8f));
        dl.AddText(tl + new Vector2((imgWidth - sl.X) * 0.5f, imgH * 0.5f + 10f), sc, sizeLabel);

        // Resize handle grip in bottom-right corner
        uint gripCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.5f, 0.1f, 0.7f));
        Vector2 gripTL = br - new Vector2(20f, 20f);
        dl.AddRectFilled(gripTL, br, gripCol, 3f);
        dl.AddText(gripTL + new Vector2(3f, 3f),
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f)), "↔");
    }

    // =========================================================================
    // DRAW ONE ACTIVE DISPLAY
    // =========================================================================

    private float DrawDisplay(
        ImDrawListPtr dl, ActiveDisplay display, Vector2 origin,
        float cursorOffset, float imgWidth, float alpha, Configuration cfg)
    {
        var   tex      = Plugin.ImageCache.GetTexture(display.ResolvedImage);
        bool  stackUp  = !cfg.OverlayFreePosition && cfg.OverlayAnchor >= 2;
        float imgH     = tex != null
            ? imgWidth * ((float)tex.Height / Math.Max(tex.Width, 1))
            : imgWidth * (9f / 16f);
        float captionH = cfg.OverlayShowCaption ? 22f : 0f;
        float totalH   = imgH + captionH + 8f;
        float panelY   = stackUp
            ? origin.Y + cursorOffset - totalH
            : origin.Y + cursorOffset;

        Vector2 panelTL = new(origin.X, panelY);
        Vector2 panelBR = new(origin.X + imgWidth, panelY + totalH);
        Vector2 imgTL   = panelTL;
        Vector2 imgBR   = new(panelTL.X + imgWidth, panelTL.Y + imgH);

        // Background
        dl.AddRectFilled(panelTL, panelBR, ColorWithAlpha(0x000000, cfg.OverlayBgAlpha * alpha), 4f);

        // Image or placeholder
        if (tex != null)
            dl.AddImage(tex.Handle, imgTL, imgBR, Vector2.Zero, Vector2.One,
                        ColorWithAlpha(0xFFFFFF, alpha));
        else
        {
            dl.AddRectFilled(imgTL, imgBR, ColorWithAlpha(0x222222, 0.7f * alpha), 4f);
            dl.AddText(new Vector2(imgTL.X + 8f, imgTL.Y + imgH * 0.5f - 7f),
                       ColorWithAlpha(0x888888, alpha), "Loading image...");
        }

        // Caption
        if (cfg.OverlayShowCaption && !string.IsNullOrWhiteSpace(display.Entry.Label))
            dl.AddText(new Vector2(panelTL.X + 6f, imgBR.Y + 4f),
                       ColorWithAlpha(0xFFFFFF, alpha), display.Entry.Label);

        // Edit mode: draw a subtle drag-handle bar at the top of each panel
        if (EditMode)
        {
            uint handleCol = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.5f, 0.1f, 0.5f));
            dl.AddRectFilled(panelTL, new Vector2(panelBR.X, panelTL.Y + 6f), handleCol, 4f);
        }

        return totalH;
    }

    // =========================================================================
    // HELPERS
    // =========================================================================

    /// <summary>Computes the top-left origin where image stacking begins.</summary>
    private static Vector2 CalcOrigin(ImGuiViewportPtr vp, Configuration cfg, float imgWidth)
    {
        // Free position mode: use absolute stored coords
        if (cfg.OverlayFreePosition)
            return new Vector2(cfg.OverlayFreeX, cfg.OverlayFreeY);

        float vpL = vp.Pos.X, vpT = vp.Pos.Y;
        float vpR = vp.Pos.X + vp.Size.X;
        float vpB = vp.Pos.Y + vp.Size.Y;
        float ox  = cfg.OverlayOffsetX, oy = cfg.OverlayOffsetY;

        return cfg.OverlayAnchor switch
        {
            0 => new Vector2(vpL + ox,              vpT + oy),  // TL
            1 => new Vector2(vpR - ox - imgWidth,   vpT + oy),  // TR
            2 => new Vector2(vpL + ox,              vpB - oy),  // BL
            3 => new Vector2(vpR - ox - imgWidth,   vpB - oy),  // BR
            _ => new Vector2(vpR - ox - imgWidth,   vpT + oy),
        };
    }

    private static float CalcAlpha(ActiveDisplay d, float fadeDur)
    {
        if (fadeDur <= 0f) return 1f;
        return Math.Clamp((float)d.Age.Elapsed.TotalSeconds / fadeDur, 0f, 1f);
    }

    private static uint ColorWithAlpha(uint rgb, float alpha)
    {
        byte r = (byte)((rgb >> 16) & 0xFF);
        byte g = (byte)((rgb >>  8) & 0xFF);
        byte b = (byte)( rgb        & 0xFF);
        byte a = (byte)(Math.Clamp(alpha, 0f, 1f) * 255f);
        return (uint)((a << 24) | (b << 16) | (g << 8) | r);
    }

    private void DrawVariantWatermark(ImDrawListPtr dl, ImGuiViewportPtr vp, Configuration cfg)
    {
        // No-op — watermark moved to MainWindow title bar
    }

    // =========================================================================
    // ENGINE EVENT HANDLERS
    // =========================================================================

    private void OnStratTriggered(object? sender, StratTriggerEventArgs e)
    {
        _activeDisplays.Add(new ActiveDisplay
        {
            Entry = e.Entry, ResolvedImage = e.ResolvedImage, FightTime = e.FightTime,
        });
    }

    private void OnStratExpired(object? sender, StratExpiredEventArgs e)
        => _activeDisplays.RemoveAll(d => d.Entry.Id == e.Entry.Id);

    private void OnCombatEnded(object? sender, EventArgs e)
        => _activeDisplays.Clear();

    private void OnVariantChanged(object? sender, string variantId) { /* handled in MainWindow */ }
}
