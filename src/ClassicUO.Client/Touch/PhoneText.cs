// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Build 58 (owner, 2026-10-25: "The menus are still hard to read, using the original UO script doesn't work because
    /// when zoomed in it is almost illegible"). Three parts, all on touch builds only:
    /// <list type="bullet">
    /// <item>The UI is drawn at the screen's pixels (<see cref="UiAtPixels"/>): before, it was drawn at UI units (480 on a
    /// phone's short side) into a small target and stretched ~2.5x onto the screen with linear filtering, after a zoomed
    /// window's own nearest-neighbour 1.3x; each glyph was resampled twice and smeared.</item>
    /// <item>Text is drawn at its final size (<see cref="Crisp"/>, RenderedText.DrawGlyphs and
    /// Renderer.FontGlyphAtlas.TryGetScaledEntry): each glyph is rasterised once at the exact screen scale it is shown at
    /// (box filter) and drawn 1:1 at a whole screen pixel, so strokes keep one width.</item>
    /// <item>UO's blackletter Unicode font (font 0: server menus' &lt;BIG&gt; text, the journal) is replaced by UO's own
    /// clean bold Unicode font 3 (<see cref="ApplyFonts"/>), nearly the same width, so menus laid out for it still fit;
    /// journal lines sent in UO's ASCII fonts (all ornate) are shown in it too.</item>
    /// </list>
    /// Each part can be turned off for comparison: UOM_HIRES_UI=0, UOM_CRISP_TEXT=0, UOM_PHONE_FONTS=0.
    /// </summary>
    internal static class PhoneText
    {
        private static readonly bool _noHiRes = Environment.GetEnvironmentVariable("UOM_HIRES_UI") == "0";
        private static readonly bool _noCrisp = Environment.GetEnvironmentVariable("UOM_CRISP_TEXT") == "0";
        private static readonly bool _noFonts = Environment.GetEnvironmentVariable("UOM_PHONE_FONTS") == "0";

        /// <summary>The UI is drawn straight at the backbuffer's pixels.</summary>
        public static bool UiAtPixels => TouchInput.Enabled && !_noHiRes;

        /// <summary>Text not drawn 1:1 is rasterised at its final scale. Not during a window pinch (the scale changes every frame).</summary>
        public static bool Crisp => TouchInput.Enabled && !_noCrisp && !TouchInput.PinchingWindow;

        /// <summary>The phone's font substitutions are on.</summary>
        public static bool Fonts => TouchInput.Enabled && !_noFonts;

        /// <summary>UO's blackletter Unicode font, and the clean bold one shown instead.</summary>
        public const byte SCRIPT_FONT = 0, CLEAN_FONT = 3;

        /// <summary>Pixels taken off the clean font's letter spacing: it is wider than the script, and menus were laid out for the script.</summary>
        public const sbyte CLEAN_TIGHTEN = 2;

        /// <summary>Once, after the UO files are loaded and before any text is laid out.</summary>
        public static void ApplyFonts(Assets.FontsLoader fonts)
        {
            if (!Fonts)
            {
                return;
            }

            bool done = fonts.AliasUnicodeFont(SCRIPT_FONT, CLEAN_FONT, CLEAN_TIGHTEN);
            Utility.Logging.Log.Info($"[UOMobile] phone fonts: Unicode font {SCRIPT_FONT} shown as {CLEAN_FONT}: {(done ? "yes" : "no (font missing)")}");
        }

        /// <summary>Each frame before the UI is drawn (never mid-batch).</summary>
        public static void BeginFrame() => Client.Game.UO.FontGlyphAtlas?.BeginFrame();
    }
}
