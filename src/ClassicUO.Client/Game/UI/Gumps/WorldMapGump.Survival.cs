// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Renderer;
using ClassicUO.Touch;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.UI.Gumps
{
    /// <summary>
    /// UO Mobile: Zombie Survival's markers on ClassicUO's own world map too (the phone has its own map, Touch/SurvivalMapGump):
    /// every town as a dot coloured by its state (held green, cleared yellow, overrun red) with its name, the wandering
    /// peddlers (orange), your camp (gold) and your friends' camps (blue), as the server last sent them (Touch/ZombieMarkers,
    /// which asks again while this map is open). Nothing on a shard that sends none.
    /// </summary>
    internal partial class WorldMapGump
    {
        private void DrawSurvivalMarkers(UltimaBatcher2D batcher, int x, int y, int width, int height, float layerDepth)
        {
            if (!ZombieMarkers.Supported || _map == null)
            {
                return;
            }

            foreach (SurvivalMarker k in ZombieMarkers.Markers)
            {
                if (k.Facet != _map.Index)
                {
                    continue;
                }

                Point rot = RotatePoint(k.X - _center.X, k.Y - _center.Y, Zoom, 1, _flipMap ? 45f : 0f);
                rot.X += x + width;
                rot.Y += y + height;

                if (rot.X < x || rot.X > x + Width - 8 || rot.Y < y || rot.Y > y + Height - 8)
                {
                    continue;
                }

                Color color;
                ushort hue;
                int size;

                switch (k.Kind)
                {
                    case SurvivalMarkerKind.Town:
                        color = k.TownState == 2 ? Color.LimeGreen : k.TownState == 1 ? Color.Gold : Color.Red;
                        hue = k.TownState == 2 ? (ushort)0x0044 : k.TownState == 1 ? (ushort)0x0035 : (ushort)0x0022;
                        size = 8;

                        break;

                    case SurvivalMarkerKind.Peddler:
                        color = k.Fleeing ? Color.OrangeRed : Color.Orange;
                        hue = 0x002B;
                        size = 6;

                        break;

                    case SurvivalMarkerKind.Camp:
                        color = k.UnderAttack ? Color.Red : Color.Gold;
                        hue = 0x0035;
                        size = 8;

                        break;

                    default:
                        color = Color.LightSkyBlue;
                        hue = 0x0059;
                        size = 7;

                        break;
                }

                Vector3 hueVector = ShaderHueTranslator.GetHueVector(0);
                batcher.Draw(SolidColorTextureCache.GetTexture(Color.Black), new Rectangle(rot.X - size / 2 - 1, rot.Y - size / 2 - 1, size + 2, size + 2), hueVector, layerDepth);
                batcher.Draw(SolidColorTextureCache.GetTexture(color), new Rectangle(rot.X - size / 2, rot.Y - size / 2, size, size), hueVector, layerDepth);

                if (k.Kind == SurvivalMarkerKind.Peddler && _zoomIndex < 4)
                {
                    continue; // peddlers are named when zoomed in
                }

                Vector2 textSize = Fonts.Regular.MeasureString(k.Name);
                int tx = rot.X - (int)(textSize.X / 2);
                int ty = rot.Y + size / 2 + 1;
                hueVector.X = 0;
                hueVector.Y = 1;
                batcher.DrawString(Fonts.Regular, k.Name, tx + 1, ty + 1, hueVector, layerDepth);
                hueVector.X = hue;
                hueVector.Y = 1;
                hueVector.Z = 1;
                batcher.DrawString(Fonts.Regular, k.Name, tx, ty, hueVector, layerDepth);
            }
        }
    }
}
