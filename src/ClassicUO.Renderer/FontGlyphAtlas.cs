// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ClassicUO.Renderer
{
    public struct GlyphAtlasEntry
    {
        public Texture2D Texture;
        public Rectangle UV;
        public int BearingX;
        public int BearingY;
        public int AdvanceWidth;
        public int GlyphWidth;
        public int GlyphHeight;

        public bool IsValid => Texture != null;

        public static readonly GlyphAtlasEntry Empty = new GlyphAtlasEntry();
    }

    public sealed class FontGlyphAtlas : IDisposable
    {
        private readonly Dictionary<long, GlyphAtlasEntry> _cache = new Dictionary<long, GlyphAtlasEntry>();
        private readonly Dictionary<(long, uint), GlyphAtlasEntry> _coloredCache = new Dictionary<(long, uint), GlyphAtlasEntry>();
        private readonly TextureAtlas _atlas;
        private readonly FontsLoader _fontsLoader;

        public FontGlyphAtlas(FontsLoader fontsLoader, GraphicsDevice device)
        {
            _fontsLoader = fontsLoader;
            _device = device;
            _atlas = new TextureAtlas(device, 2048, 2048, SurfaceFormat.Color);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long MakeKey(byte font, char character, bool isUnicode, bool hasBorder, bool isSolid, bool isItalic)
        {
            // Pack into long: font(8) | char(16) | unicode(1) | border(1) | solid(1) | italic(1)
            return ((long)font << 20)
                 | ((long)(ushort)character << 4)
                 | (isUnicode ? 8L : 0L)
                 | (hasBorder ? 4L : 0L)
                 | (isSolid ? 2L : 0L)
                 | (isItalic ? 1L : 0L);
        }

        /// <summary>
        /// Get a glyph entry without baked color (color = 0, rendered as white for Unicode).
        /// Used for non-HTML text where hue is applied via shader at draw time.
        /// </summary>
        public ref readonly GlyphAtlasEntry GetEntry(byte font, char character, bool isUnicode, bool hasBorder, bool isSolid, bool isItalic)
        {
            long key = MakeKey(font, character, isUnicode, hasBorder, isSolid, isItalic);

            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_cache, key, out bool exists);

            if (!exists)
            {
                entry = CreateEntry(font, character, isUnicode, hasBorder, isSolid, isItalic, 0);
            }

            return ref entry;
        }

        /// <summary>
        /// Get a glyph entry with a specific baked ARGB color.
        /// Used for HTML text where each character can have a different color.
        /// </summary>
        public GlyphAtlasEntry GetColoredEntry(byte font, char character, bool isUnicode, bool hasBorder, bool isSolid, bool isItalic, uint color)
        {
            if (color == 0)
            {
                return GetEntry(font, character, isUnicode, hasBorder, isSolid, isItalic);
            }

            long baseKey = MakeKey(font, character, isUnicode, hasBorder, isSolid, isItalic);
            var key = (baseKey, color);

            ref var entry = ref CollectionsMarshal.GetValueRefOrAddDefault(_coloredCache, key, out bool exists);

            if (!exists)
            {
                entry = CreateEntry(font, character, isUnicode, hasBorder, isSolid, isItalic, color);
            }

            return entry;
        }

        private GlyphAtlasEntry CreateEntry(byte font, char character, bool isUnicode, bool hasBorder, bool isSolid, bool isItalic, uint colorOrHue)
        {
            FontsLoader.SingleGlyphInfo glyphInfo;

            if (isUnicode)
            {
                glyphInfo = _fontsLoader.RenderSingleGlyphUnicode(font, character, hasBorder, isSolid, isItalic, colorOrHue);
            }
            else
            {
                // For ASCII, colorOrHue is the hue index (ushort)
                glyphInfo = _fontsLoader.RenderSingleGlyphASCII(font, character, (ushort)colorOrHue);
            }

            if (glyphInfo.Data == null || glyphInfo.Data.Length == 0 || glyphInfo.Width <= 0 || glyphInfo.Height <= 0)
            {
                return new GlyphAtlasEntry
                {
                    Texture = null,
                    BearingX = glyphInfo.BearingX,
                    BearingY = glyphInfo.BearingY,
                    AdvanceWidth = glyphInfo.AdvanceWidth,
                    GlyphWidth = 0,
                    GlyphHeight = 0
                };
            }

            var texture = _atlas.AddSprite(
                glyphInfo.Data,
                glyphInfo.Width,
                glyphInfo.Height,
                out var uv
            );

            return new GlyphAtlasEntry
            {
                Texture = texture,
                UV = uv,
                BearingX = glyphInfo.BearingX,
                BearingY = glyphInfo.BearingY,
                AdvanceWidth = glyphInfo.AdvanceWidth,
                GlyphWidth = glyphInfo.Width,
                GlyphHeight = glyphInfo.Height
            };
        }

        // ── UO Mobile (build 58): glyphs drawn at their final size ──
        //
        // UO's fonts are 1-bit pixel fonts. A zoomed window on a phone shows them ~2.5-3.3 screen pixels per font pixel, a
        // non-integer factor: nearest-neighbour sampling makes some strokes a pixel wider than others (and, at 1.3x in
        // UI units, every third column doubled), and linear sampling blurs them. Instead, a glyph is rasterised once at the
        // exact scale it is shown at, each screen pixel taking the share of the font pixels it covers (an exact box filter),
        // and then drawn 1:1 at a whole screen pixel: strokes keep one width and their edges are anti-aliased by a pixel.
        // Scales are quantised to 1/32; the cache is dropped when it outgrows its budget (see BeginFrame).

        /// <summary>The scale step of <see cref="TryGetScaledEntry"/> (a scale is passed as steps: 32 = 1:1).</summary>
        public const int SCALE_STEPS = 32;

        private const int SCALED_ATLAS_SIZE = 1024, SCALED_ATLAS_BUDGET = 6; // (6 x 4 MB)

        private readonly Dictionary<(long, uint, int), GlyphAtlasEntry> _scaledCache = new Dictionary<(long, uint, int), GlyphAtlasEntry>();
        private TextureAtlas _scaledAtlas;
        private bool _scaledResetPending;
        private GraphicsDevice _device;

        /// <summary>Scaled glyphs made since start (for logs/tests).</summary>
        public int ScaledCreated { get; private set; }

        /// <summary>Times the scaled cache was dropped for its budget.</summary>
        public int ScaledResets { get; private set; }

        /// <summary>
        /// Call before anything is drawn in a frame: drops the scaled glyphs if they outgrew their budget last frame (never
        /// mid-frame, when the batch may still hold their textures).
        /// </summary>
        public void BeginFrame()
        {
            _frameSpent = 0;

            if (_scaledResetPending)
            {
                _scaledResetPending = false;
                _scaledAtlas?.Dispose();
                _scaledAtlas = null;
                _scaledCache.Clear();
                ScaledResets++;
            }
        }

        /// <summary>
        /// The glyph rasterised at <paramref name="scaleSteps"/> / <see cref="SCALE_STEPS"/> output pixels per font pixel.
        /// Bearing/advance stay in font pixels (the layout's units); <see cref="GlyphAtlasEntry.UV"/> is the output's size.
        /// </summary>
        /// <returns>
        /// False when the glyph isn't made yet and this frame's budget for making them (<see cref="FRAME_BUDGET_MS"/>) is
        /// spent: draw it the plain way this frame (a whole menu at a new zoom turns crisp over a few frames, never a stall).
        /// </returns>
        public bool TryGetScaledEntry(byte font, char character, bool isUnicode, bool hasBorder, bool isSolid, bool isItalic, uint color, int scaleSteps, out GlyphAtlasEntry entry)
        {
            var key = (MakeKey(font, character, isUnicode, hasBorder, isSolid, isItalic), color, scaleSteps);

            if (_scaledCache.TryGetValue(key, out entry))
            {
                return true;
            }

            if (_frameSpent * 1000 > FRAME_BUDGET_MS * System.Diagnostics.Stopwatch.Frequency)
            {
                Deferred++;

                return false;
            }

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            entry = CreateScaledEntry(font, character, isUnicode, hasBorder, isSolid, isItalic, color, scaleSteps);
            _scaledCache[key] = entry;
            _frameSpent += System.Diagnostics.Stopwatch.GetTimestamp() - t0;

            return true;
        }

        /// <summary>The most time per frame (reset by <see cref="BeginFrame"/>) spent making scaled glyphs.</summary>
        public const int FRAME_BUDGET_MS = 4;

        private long _frameSpent; // (Stopwatch ticks this frame)

        /// <summary>Glyphs drawn the plain way since start because a frame's budget was spent.</summary>
        public int Deferred { get; private set; }

        private GlyphAtlasEntry CreateScaledEntry(byte font, char character, bool isUnicode, bool hasBorder, bool isSolid, bool isItalic, uint colorOrHue, int scaleSteps)
        {
            FontsLoader.SingleGlyphInfo g = isUnicode
                ? _fontsLoader.RenderSingleGlyphUnicode(font, character, hasBorder, isSolid, isItalic, colorOrHue)
                : _fontsLoader.RenderSingleGlyphASCII(font, character, (ushort)colorOrHue);

            var result = new GlyphAtlasEntry
            {
                BearingX = g.BearingX,
                BearingY = g.BearingY,
                AdvanceWidth = g.AdvanceWidth,
                GlyphWidth = g.Width,
                GlyphHeight = g.Height
            };

            if (g.Data == null || g.Data.Length == 0 || g.Width <= 0 || g.Height <= 0)
            {
                result.GlyphWidth = result.GlyphHeight = 0;

                return result;
            }

            float s = scaleSteps / (float)SCALE_STEPS;
            int ow = Math.Max(1, (int)MathF.Ceiling(g.Width * s - 0.01f));
            int oh = Math.Max(1, (int)MathF.Ceiling(g.Height * s - 0.01f));
            uint[] dst = Resample(g.Data, g.Width, g.Height, ow, oh, s);

            if (ow + 1 > SCALED_ATLAS_SIZE || oh + 1 > SCALED_ATLAS_SIZE)
            {
                result.GlyphWidth = result.GlyphHeight = 0; // (absurd scale: drawn as nothing rather than failing)

                return result;
            }

            _scaledAtlas ??= new TextureAtlas(_device, SCALED_ATLAS_SIZE, SCALED_ATLAS_SIZE, SurfaceFormat.Color);

            // one transparent pixel right and below keeps neighbours apart (the 1:1 draw never reaches it)
            uint[] padded = new uint[(ow + 1) * (oh + 1)];

            for (int y = 0; y < oh; y++)
            {
                Array.Copy(dst, y * ow, padded, y * (ow + 1), ow);
            }

            result.Texture = _scaledAtlas.AddSprite(padded, ow + 1, oh + 1, out Rectangle uv);
            result.UV = new Rectangle(uv.X, uv.Y, ow, oh);
            ScaledCreated++;

            if (_scaledAtlas.TexturesCount > SCALED_ATLAS_BUDGET)
            {
                _scaledResetPending = true;
            }

            return result;
        }

        /// <summary>
        /// Box-filters a font bitmap to <paramref name="ow"/> x <paramref name="oh"/> at <paramref name="s"/> output pixels per
        /// source pixel: each output pixel averages the source pixels under it, weighted by the area covered. The glyph
        /// pixels are opaque or zero, so averaging the bytes gives premultiplied colour (what the batcher's blend expects).
        /// </summary>
        public static uint[] Resample(uint[] src, int w, int h, int ow, int oh, float s)
        {
            uint[] dst = new uint[ow * oh];
            float inv = 1f / s;

            for (int oy = 0; oy < oh; oy++)
            {
                float y0 = oy * inv, y1 = Math.Min(h, (oy + 1) * inv);

                for (int ox = 0; ox < ow; ox++)
                {
                    float x0 = ox * inv, x1 = Math.Min(w, (ox + 1) * inv);
                    float r = 0, gg = 0, b = 0, a = 0;

                    for (int sy = (int)y0; sy < h && sy < y1; sy++)
                    {
                        float wy = Math.Min(y1, sy + 1) - Math.Max(y0, sy);

                        if (wy <= 0)
                        {
                            continue;
                        }

                        for (int sx = (int)x0; sx < w && sx < x1; sx++)
                        {
                            uint p = src[sy * w + sx];

                            if (p == 0)
                            {
                                continue;
                            }

                            float wt = wy * (Math.Min(x1, sx + 1) - Math.Max(x0, sx));

                            if (wt <= 0)
                            {
                                continue;
                            }

                            r += (p & 0xFF) * wt;
                            gg += ((p >> 8) & 0xFF) * wt;
                            b += ((p >> 16) & 0xFF) * wt;
                            a += (p >> 24) * wt;
                        }
                    }

                    float k = s * s; // (the weights cover 1/s x 1/s source pixels in all)

                    if (a > 0)
                    {
                        dst[oy * ow + ox] = (uint)Math.Min(255, (int)(r * k + 0.5f)) | (uint)Math.Min(255, (int)(gg * k + 0.5f)) << 8 |
                                            (uint)Math.Min(255, (int)(b * k + 0.5f)) << 16 | (uint)Math.Min(255, (int)(a * k + 0.5f)) << 24;
                    }
                }
            }

            return dst;
        }

        public void Dispose()
        {
            _atlas?.Dispose();
            _cache.Clear();
            _coloredCache.Clear();
            _scaledAtlas?.Dispose();
            _scaledAtlas = null;
            _scaledCache.Clear();
        }
    }
}
