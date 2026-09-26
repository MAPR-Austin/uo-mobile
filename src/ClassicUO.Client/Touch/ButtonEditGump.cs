// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Renderer;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Edits one on-screen button of the active layout: pick a preset action (or one of the
    /// player's own macros), rename it, resize it, or delete it. Opened by tapping a button
    /// while the HUD is in edit mode.
    /// </summary>
    internal sealed class ButtonEditGump : Gump
    {
        private const int W = 520;
        private const int H = 400;
        private const int PAGE_SIZE = 18;

        private const int ID_SAVE = 1, ID_DELETE = 2, ID_SMALLER = 3, ID_BIGGER = 4, ID_PREV = 5, ID_NEXT = 6, ID_CANCEL = 7;
        private const int ID_PRESET_BASE = 100;

        private readonly int _index;
        private readonly List<(string Label, string Action)> _choices;
        private readonly StbTextBox _label, _action;
        private readonly Label _sizeLabel;
        private int _page;

        /// <summary>The preset catalogue, grouped the way a PvPer thinks about it.</summary>
        private static readonly (string Label, string Action)[] Presets =
        {
            ("Attack Nearest", "attack_nearest"), ("Target Nearest", "target_nearest"), ("Next Target", "target_next"),
            ("Prev Target", "target_prev"), ("Attack Last", "attack_last"), ("Last Target", "last_target"),
            ("Target Self", "target_self"), ("Cancel", "cancel_target"), ("Health Bar", "healthbar_target"),
            ("War/Peace", "war_peace"), ("Bandage Self", "bandage_self"), ("Bandage Tgt", "bandage_target"),
            ("Last Spell", "last_spell"), ("Last Object", "last_object"), ("All Names", "all_names"),
            ("E-Bolt>Last", "spell_lt:EnergyBolt"), ("Explo>Last", "spell_lt:Explosion"), ("Flame>Last", "spell_lt:FlameStrike"),
            ("M.Arrow>Last", "spell_lt:MagicArrow"), ("Harm>Last", "spell_lt:Harm"), ("Para>Last", "spell_lt:Paralyze"),
            ("Poison>Last", "spell_lt:Poison"), ("Lightning>Last", "spell_lt:Lightning"), ("Mind Blast>Last", "spell_lt:MindBlast"),
            ("GHeal Self", "spell_self:GreaterHeal"), ("Heal Self", "spell_self:Heal"), ("Cure Self", "spell_self:Cure"),
            ("GHeal>Last", "spell_lt:GreaterHeal"), ("Cure>Last", "spell_lt:Cure"), ("Reflect", "spell:MagicReflection"),
            ("Recall", "spell:Recall"), ("Teleport", "spell:Teleport"), ("Invis", "spell:Invisibility"),
            ("Hide", "skill:Hiding"), ("Meditate", "skill:Meditation"), ("Stealth", "skill:Stealth"),
            ("Backpack", "open:Backpack"), ("Paperdoll", "open:Paperdoll"), ("Skills", "open:Skills"),
            ("Journal", "open:Journal"), ("Status", "open:Status"), ("Spellbook", "open:MageSpellbook"),
            ("World Map", "open:WorldMap"), ("Macro Editor", "macro_editor"), ("Next Layout", "layout_next"),
            ("Edit Layout", "edit_layout"), ("Stop Macro", "stop_macro"), ("UO Macros", "uo_macros"),
        };

        private ButtonEditGump(World world, int index) : base(world, 0, 0)
        {
            _index = index;
            CanMove = true;
            AcceptMouseInput = true;
            CanCloseWithRightClick = true;

            Width = W;
            Height = H;
            X = Math.Max(0, (TouchInput.ScreenW - W) / 2);
            Y = Math.Max(0, (TouchInput.ScreenH - H) / 2);

            ActionButtonDef def = Def;

            _choices = new List<(string, string)>(Presets);

            MobileMacroRunner.EnsureLoaded();

            foreach (MobileMacro m in MobileMacroRunner.Macros.Macros)
            {
                _choices.Add(($"Macro: {m.Name}", $"mmacro:{m.Name}"));
            }

            foreach (Macro m in world.Macros.GetAllMacros())
            {
                _choices.Add(($"UO Macro: {m.Name}", $"macro:{m.Name}"));
            }

            Add(new AlphaBlendControl(0.85f) { Width = W, Height = H });

            Add(new Label("Label", true, 0x0481, 0, 1) { X = 12, Y = 12 });
            Add(new ResizePic(0x0BB8) { X = 70, Y = 8, Width = 170, Height = 26 });
            Add(_label = new StbTextBox(1, 40, 160, true, FontStyle.None, 0x0481) { X = 76, Y = 12, Width = 160, Height = 20 });
            _label.SetText((def?.Label ?? "").Replace("\n", "|"));

            Add(new Label("Action", true, 0x0481, 0, 1) { X = 252, Y = 12 });
            Add(new ResizePic(0x0BB8) { X = 308, Y = 8, Width = 200, Height = 26 });
            Add(_action = new StbTextBox(1, 80, 190, true, FontStyle.None, 0x0481) { X = 314, Y = 12, Width = 190, Height = 20 });
            _action.SetText(def?.Action ?? "");

            Add(new Label("Tip: use | in a label for a line break", true, 0x0386, 0, 1) { X = 12, Y = 40 });

            Add(new NiceButton(12, H - 40, 70, 28, ButtonAction.Activate, "Smaller") { ButtonParameter = ID_SMALLER, IsSelectable = false });
            Add(_sizeLabel = new Label("", true, 0x0481, 0, 1) { X = 90, Y = H - 34 });
            Add(new NiceButton(130, H - 40, 70, 28, ButtonAction.Activate, "Bigger") { ButtonParameter = ID_BIGGER, IsSelectable = false });
            Add(new NiceButton(230, H - 40, 70, 28, ButtonAction.Activate, "Delete", hue: 0x0021) { ButtonParameter = ID_DELETE, IsSelectable = false });
            Add(new NiceButton(340, H - 40, 70, 28, ButtonAction.Activate, "Cancel") { ButtonParameter = ID_CANCEL, IsSelectable = false });
            Add(new NiceButton(430, H - 40, 80, 28, ButtonAction.Activate, "Save", hue: 0x0044) { ButtonParameter = ID_SAVE, IsSelectable = false });

            Add(new NiceButton(12, H - 76, 60, 26, ButtonAction.Activate, "< Prev") { ButtonParameter = ID_PREV, IsSelectable = false });
            Add(new NiceButton(W - 72, H - 76, 60, 26, ButtonAction.Activate, "Next >") { ButtonParameter = ID_NEXT, IsSelectable = false });

            BuildPage();
            UpdateSizeLabel();
        }

        private ActionButtonDef Def
        {
            get
            {
                ActionLayout layout = TouchInput.Current;

                return layout != null && _index >= 0 && _index < layout.Buttons.Count ? layout.Buttons[_index] : null;
            }
        }

        public static void Open(World world, int index)
        {
            UIManager.GetGump<ButtonEditGump>()?.Dispose();
            UIManager.Add(new ButtonEditGump(world, index));
        }

        private readonly List<Control> _pageControls = new List<Control>();

        private void BuildPage()
        {
            foreach (Control c in _pageControls)
            {
                c.Dispose();
            }

            _pageControls.Clear();

            int start = _page * PAGE_SIZE;

            for (int i = 0; i < PAGE_SIZE && start + i < _choices.Count; i++)
            {
                int col = i % 3, row = i / 3;
                NiceButton b = new NiceButton(12 + col * 168, 64 + row * 38, 160, 32, ButtonAction.Activate, _choices[start + i].Label)
                {
                    ButtonParameter = ID_PRESET_BASE + start + i,
                    IsSelectable = false
                };
                _pageControls.Add(b);
                Add(b);
            }
        }

        private void UpdateSizeLabel()
        {
            _sizeLabel.Text = $"{(int)Math.Round((Def?.Size ?? 0) * 100)}";
        }

        public override void OnButtonClick(int buttonID)
        {
            ActionButtonDef def = Def;

            if (def == null)
            {
                Dispose();

                return;
            }

            switch (buttonID)
            {
                case ID_SAVE:
                    def.Label = (_label.Text ?? "").Replace("|", "\n");
                    def.Action = (_action.Text ?? "").Trim();
                    TouchInput.Layouts.Save();
                    TouchInput.MarkChanged();
                    Dispose();

                    break;

                case ID_CANCEL:
                    Dispose();

                    break;

                case ID_DELETE:
                    TouchInput.Current.Buttons.RemoveAt(_index);
                    TouchInput.Layouts.Save();
                    TouchInput.MarkChanged();
                    Dispose();

                    break;

                case ID_SMALLER:
                    def.Size = Math.Max(0.07f, def.Size - 0.01f);
                    TouchInput.MarkChanged();
                    UpdateSizeLabel();

                    break;

                case ID_BIGGER:
                    def.Size = Math.Min(0.30f, def.Size + 0.01f);
                    TouchInput.MarkChanged();
                    UpdateSizeLabel();

                    break;

                case ID_PREV:
                    if (_page > 0)
                    {
                        _page--;
                        BuildPage();
                    }

                    break;

                case ID_NEXT:
                    if ((_page + 1) * PAGE_SIZE < _choices.Count)
                    {
                        _page++;
                        BuildPage();
                    }

                    break;

                default:
                    int choice = buttonID - ID_PRESET_BASE;

                    if (choice >= 0 && choice < _choices.Count)
                    {
                        (string label, string action) = _choices[choice];
                        _action.SetText(action);
                        _label.SetText(label.Contains("Macro: ") ? label.Substring(label.IndexOf("Macro: ") + 7) : label.Replace(" ", "|"));
                    }

                    break;
            }
        }
    }
}
