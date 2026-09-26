// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using ClassicUO.Configuration;

namespace ClassicUO.Touch
{
    /// <summary>One on-screen button. Position is the button centre, normalised to the screen (0..1).</summary>
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
        /// <summary>Diameter as a fraction of the screen's shorter side.</summary>
        [JsonPropertyName("size")] public float Size { get; set; } = 0.14f;
    }

    internal sealed class ActionLayout
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("joy_x")] public float JoystickX { get; set; } = 0.14f;
        [JsonPropertyName("joy_y")] public float JoystickY { get; set; } = 0.74f;
        [JsonPropertyName("joy_size")] public float JoystickSize { get; set; } = 0.34f;
        [JsonPropertyName("buttons")] public List<ActionButtonDef> Buttons { get; set; } = new List<ActionButtonDef>();
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

            if (set == null || set.Layouts.Count == 0)
            {
                set = CreateDefault();
            }

            return set;
        }

        public void Save()
        {
            ConfigurationResolver.Save(this, FilePath, MobileJsonContext.Default.ActionLayoutSet);
        }

        private static ActionButtonDef B(string label, string action, float x, float y, float size = 0.14f) =>
            new ActionButtonDef { Label = label.Replace('\n', '|'), Action = action, X = x, Y = y, Size = size };

        /// <summary>
        /// Three starter layouts. Every layout carries the same top-right system row
        /// (layout toggle, edit, cancel target) so the player can never lose the way back.
        /// </summary>
        public static ActionLayoutSet CreateDefault()
        {
            List<ActionButtonDef> SystemRow() => new List<ActionButtonDef>
            {
                B("Layout", "layout_next", 0.95f, 0.07f, 0.10f),
                B("Edit", "edit_layout", 0.87f, 0.07f, 0.10f),
                B("Cancel", "cancel_target", 0.79f, 0.07f, 0.10f),
                B("Chat", "chat", 0.71f, 0.07f, 0.10f),
            };

            ActionLayout combat = new ActionLayout { Name = "Combat" };
            combat.Buttons.AddRange(SystemRow());
            combat.Buttons.AddRange(new[]
            {
                B("Attack\nNearest", "attack_nearest", 0.90f, 0.80f, 0.20f),
                B("Next\nTarget", "target_next", 0.76f, 0.88f),
                B("Last\nTarget", "last_target", 0.78f, 0.70f),
                B("Self", "target_self", 0.90f, 0.58f),
                B("Attack\nLast", "attack_last", 0.64f, 0.88f),
                B("Health\nBar", "healthbar_target", 0.95f, 0.40f, 0.11f),
                B("Bandage", "bandage_self", 0.84f, 0.44f, 0.11f),
                B("War", "war_peace", 0.95f, 0.22f, 0.10f),
            });

            ActionLayout mage = new ActionLayout { Name = "Mage" };
            mage.Buttons.AddRange(SystemRow());
            mage.Buttons.AddRange(new[]
            {
                B("E-Bolt", "spell_lt:EnergyBolt", 0.90f, 0.82f, 0.16f),
                B("Explo", "spell_lt:Explosion", 0.76f, 0.88f),
                B("Flame", "spell_lt:FlameStrike", 0.78f, 0.70f),
                B("Magic\nArrow", "spell_lt:MagicArrow", 0.64f, 0.88f),
                B("Para", "spell_lt:Paralyze", 0.90f, 0.62f),
                B("Heal\nSelf", "spell_self:GreaterHeal", 0.95f, 0.44f, 0.11f),
                B("Cure\nSelf", "spell_self:Cure", 0.84f, 0.46f, 0.11f),
                B("Next\nTarget", "target_next", 0.66f, 0.72f, 0.11f),
                B("Last\nTarget", "last_target", 0.95f, 0.26f, 0.10f),
            });

            ActionLayout utility = new ActionLayout { Name = "Utility" };
            utility.Buttons.AddRange(SystemRow());
            utility.Buttons.AddRange(new[]
            {
                B("Pack", "open:Backpack", 0.90f, 0.82f, 0.14f),
                B("Paper\ndoll", "open:Paperdoll", 0.77f, 0.86f),
                B("Skills", "open:Skills", 0.90f, 0.66f),
                B("Journal", "open:Journal", 0.77f, 0.70f),
                B("Names", "all_names", 0.64f, 0.88f),
                B("Macros", "macro_editor", 0.95f, 0.46f, 0.11f),
                B("Status", "open:Status", 0.84f, 0.50f, 0.11f),
            });

            return new ActionLayoutSet { Active = 0, Layouts = { combat, mage, utility } };
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(ActionLayoutSet))]
    sealed partial class MobileJsonContext : JsonSerializerContext { }
}
