// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using ClassicUO.Configuration;

namespace ClassicUO.Touch
{
    /// <summary>
    /// One on-screen button. Positions are the button centre, normalised to the safe area (0..1),
    /// kept separately for landscape (X/Y) and portrait (PX/PY). A missing portrait position is
    /// derived from the landscape one (see <see cref="ActionLayout.Portrait"/>).
    /// </summary>
    internal sealed class ActionButtonDef
    {
        /// <summary>Button text. "|" is a line break: ClassicUO's JSON loader doubles backslashes, so "\n" would not survive a save.</summary>
        [JsonPropertyName("label")] public string Label { get; set; } = "";

        /// <summary>Label as drawn (also repairs layouts saved with a mangled "\n").</summary>
        [JsonIgnore]
        public string DisplayLabel => (Label ?? "").Replace("\\n", "\n").Replace('|', '\n');
        /// <summary>An action id understood by <see cref="MobileActions"/> ("attack_nearest", "spell_lt:EnergyBolt", "macro:My Macro", ...).</summary>
        [JsonPropertyName("action")] public string Action { get; set; } = "";
        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
        [JsonPropertyName("px")] public float? PX { get; set; }
        [JsonPropertyName("py")] public float? PY { get; set; }
        /// <summary>Diameter as a fraction of the screen's shorter side.</summary>
        [JsonPropertyName("size")] public float Size { get; set; } = 0.14f;
    }

    internal sealed class ActionLayout
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("joy_x")] public float JoystickX { get; set; } = 0.14f;
        [JsonPropertyName("joy_y")] public float JoystickY { get; set; } = 0.74f;
        [JsonPropertyName("joy_px")] public float? PJoystickX { get; set; }
        [JsonPropertyName("joy_py")] public float? PJoystickY { get; set; }
        [JsonPropertyName("joy_size")] public float JoystickSize { get; set; } = 0.34f;
        [JsonPropertyName("buttons")] public List<ActionButtonDef> Buttons { get; set; } = new List<ActionButtonDef>();

        /// <summary>
        /// Portrait position for a landscape one that has none: keep the same physical distance from
        /// the nearest corner, staying on the same half of the screen; the middle band stays in the
        /// middle. <paramref name="aspect"/> is long side / short side of the screen.
        /// </summary>
        public static (float X, float Y) Portrait(float x, float y, float aspect)
        {
            float px = Math.Abs(x - 0.5f) < 0.15f ? x
                : x > 0.5f ? Math.Max(0.55f, 1f - (1f - x) * aspect)
                : Math.Min(0.45f, x * aspect);
            float py = y > 0.5f ? 1f - (1f - y) / aspect : y / aspect;

            return (Math.Clamp(px, 0.08f, 0.92f), Math.Clamp(py, 0.03f, 0.97f));
        }
    }

    internal sealed class ActionLayoutSet
    {
        [JsonPropertyName("active")] public int Active { get; set; }
        [JsonPropertyName("layouts")] public List<ActionLayout> Layouts { get; set; } = new List<ActionLayout>();

        [JsonIgnore]
        public ActionLayout Current =>
            Layouts.Count == 0 ? null : Layouts[((Active % Layouts.Count) + Layouts.Count) % Layouts.Count];

        public void Next()
        {
            if (Layouts.Count > 0)
            {
                Active = (Active + 1) % Layouts.Count;
            }
        }

        private static string FilePath =>
            Path.Combine(ProfileManager.ProfilePath ?? CUOEnviroment.ExecutablePath, "mobile_layouts.json");

        public static ActionLayoutSet Load()
        {
            ActionLayoutSet set = null;

            if (File.Exists(FilePath))
            {
                set = ConfigurationResolver.Load(FilePath, MobileJsonContext.Default.ActionLayoutSet);
            }

            // Valid JSON can still hold nulls ("layouts": [null], "buttons": null); drop them
            // rather than crash on every entry to the world.
            if (set?.Layouts != null)
            {
                set.Layouts.RemoveAll(l => l == null);

                foreach (ActionLayout l in set.Layouts)
                {
                    l.Buttons ??= new List<ActionButtonDef>();
                    l.Buttons.RemoveAll(b => b == null);
                }
            }

            if (set?.Layouts == null || set.Layouts.Count == 0)
            {
                set = CreateDefault();
            }
            else
            {
                FillPortraitFromDefaults(set);
            }

            return set;
        }

        /// <summary>
        /// Layouts saved before portrait existed (build 31 and earlier) have no portrait positions.
        /// Deriving them from landscape stacks buttons on the joystick, so take each button's spot
        /// from the matching default layout (same name, else same index), matched by action. Only
        /// buttons the defaults don't have fall back to <see cref="ActionLayout.Portrait"/>.
        /// </summary>
        private static void FillPortraitFromDefaults(ActionLayoutSet set)
        {
            ActionLayoutSet defaults = CreateDefault();

            for (int i = 0; i < set.Layouts.Count; i++)
            {
                ActionLayout layout = set.Layouts[i];
                ActionLayout d = defaults.Layouts.Find(x => x.Name == layout.Name) ?? defaults.Layouts[i % defaults.Layouts.Count];

                if (!layout.PJoystickX.HasValue || !layout.PJoystickY.HasValue)
                {
                    layout.PJoystickX = d.PJoystickX;
                    layout.PJoystickY = d.PJoystickY;
                }

                List<ActionButtonDef> unused = new List<ActionButtonDef>(d.Buttons);

                foreach (ActionButtonDef b in layout.Buttons)
                {
                    if (b.PX.HasValue && b.PY.HasValue)
                    {
                        continue;
                    }

                    ActionButtonDef match = unused.Find(x => x.Action == b.Action);

                    if (match != null)
                    {
                        b.PX = match.PX;
                        b.PY = match.PY;
                        unused.Remove(match); // a second copy of the same action falls back to derivation
                    }
                }
            }
        }

        public void Save()
        {
            ConfigurationResolver.Save(this, FilePath, MobileJsonContext.Default.ActionLayoutSet);
        }

        /// <summary>A button with landscape (x, y) and portrait (px, py) positions.</summary>
        private static ActionButtonDef B(string label, string action, float x, float y, float px, float py, float size = 0.14f) =>
            new ActionButtonDef { Label = label.Replace('\n', '|'), Action = action, X = x, Y = y, PX = px, PY = py, Size = size };

        private static ActionLayout L(string name) => new ActionLayout
        {
            Name = name,
            // portrait: bottom-left, under the world view
            PJoystickX = 0.24f,
            PJoystickY = 0.86f
        };

        /// <summary>
        /// Three starter layouts. Every layout carries the same system row (war/peace, chat, cancel
        /// target, edit, layout toggle) so the player can never lose the way back. Landscape puts the
        /// action cluster bottom-right; portrait stacks it under the world view on the right, with
        /// the joystick on the left.
        /// </summary>
        public static ActionLayoutSet CreateDefault()
        {
            List<ActionButtonDef> SystemRow() => new List<ActionButtonDef>
            {
                B("Layout", "layout_next", 0.95f, 0.07f, 0.90f, 0.035f, 0.10f),
                B("Edit", "edit_layout", 0.87f, 0.07f, 0.70f, 0.035f, 0.10f),
                B("Cancel", "cancel_target", 0.79f, 0.07f, 0.50f, 0.035f, 0.10f),
                B("Chat", "chat", 0.71f, 0.07f, 0.30f, 0.035f, 0.10f),
                B("War", "war_peace", 0.63f, 0.07f, 0.10f, 0.035f, 0.10f),
            };

            ActionLayout combat = L("Combat");
            combat.Buttons.AddRange(SystemRow());
            combat.Buttons.AddRange(new[]
            {
                B("Attack\nNearest", "attack_nearest", 0.90f, 0.80f, 0.82f, 0.90f, 0.20f),
                B("Next\nTarget", "target_next", 0.76f, 0.88f, 0.58f, 0.94f),
                B("Last\nTarget", "last_target", 0.78f, 0.70f, 0.82f, 0.77f),
                B("Self", "target_self", 0.90f, 0.58f, 0.58f, 0.72f),
                B("Attack\nLast", "attack_last", 0.64f, 0.88f, 0.58f, 0.83f),
                B("Health\nBar", "healthbar_target", 0.95f, 0.40f, 0.93f, 0.60f, 0.11f),
                B("Bandage", "bandage_self", 0.84f, 0.44f, 0.70f, 0.62f, 0.11f),
                B("Set\nTarget", "set_target", 0.65f, 0.70f, 0.82f, 0.67f, 0.12f),
            });

            ActionLayout mage = L("Mage");
            mage.Buttons.AddRange(SystemRow());
            mage.Buttons.AddRange(new[]
            {
                B("E-Bolt", "spell_lt:EnergyBolt", 0.90f, 0.82f, 0.82f, 0.90f, 0.16f),
                B("Explo", "spell_lt:Explosion", 0.76f, 0.88f, 0.58f, 0.94f),
                B("Flame", "spell_lt:FlameStrike", 0.78f, 0.70f, 0.58f, 0.83f),
                B("Magic\nArrow", "spell_lt:MagicArrow", 0.64f, 0.88f, 0.82f, 0.78f),
                B("Para", "spell_lt:Paralyze", 0.90f, 0.62f, 0.58f, 0.72f),
                B("Heal\nSelf", "spell_self:GreaterHeal", 0.95f, 0.44f, 0.93f, 0.66f, 0.11f),
                B("Cure\nSelf", "spell_self:Cure", 0.84f, 0.46f, 0.74f, 0.66f, 0.11f),
                B("Next\nTarget", "target_next", 0.66f, 0.72f, 0.93f, 0.59f, 0.11f),
                B("Last\nTarget", "last_target", 0.95f, 0.26f, 0.74f, 0.59f, 0.10f),
                B("Set\nTarget", "set_target", 0.66f, 0.55f, 0.55f, 0.62f, 0.11f),
            });

            ActionLayout utility = L("Utility");
            utility.Buttons.AddRange(SystemRow());
            utility.Buttons.AddRange(new[]
            {
                B("Pack", "open:Backpack", 0.90f, 0.82f, 0.82f, 0.90f, 0.14f),
                B("Paper\ndoll", "open:Paperdoll", 0.77f, 0.86f, 0.58f, 0.94f),
                B("Skills", "open:Skills", 0.90f, 0.66f, 0.82f, 0.77f),
                B("Journal", "open:Journal", 0.77f, 0.70f, 0.58f, 0.83f),
                B("Names", "all_names", 0.64f, 0.88f, 0.58f, 0.72f),
                B("Macros", "macro_editor", 0.95f, 0.46f, 0.93f, 0.66f, 0.11f),
                B("Status", "open:Status", 0.84f, 0.50f, 0.74f, 0.66f, 0.11f),
            });

            return new ActionLayoutSet { Active = 0, Layouts = { combat, mage, utility } };
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(ActionLayoutSet))]
    sealed partial class MobileJsonContext : JsonSerializerContext { }
}
