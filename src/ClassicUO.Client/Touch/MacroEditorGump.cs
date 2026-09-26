// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Game;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Controls;
using ClassicUO.Game.UI.Gumps;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Touch-friendly editor for <see cref="MobileMacro"/>s. Macros are built mostly by tapping:
    /// pick a step template from the categories (Flow / Target / Spells / Items / Actions) and it
    /// is inserted under the selected line; any line can still be edited as text.
    /// </summary>
    internal sealed class MacroEditorGump : Gump
    {
        private const int W = 640;
        private const int H = 470;
        private const int VISIBLE_LINES = 9;
        private const int ROW_H = 30;

        private const int ID_PREV_MACRO = 1, ID_NEXT_MACRO = 2, ID_NEW = 3, ID_DELETE_MACRO = 4, ID_RENAME = 5;
        private const int ID_SCROLL_UP = 6, ID_SCROLL_DOWN = 7, ID_LINE_UP = 8, ID_LINE_DOWN = 9, ID_LINE_DELETE = 10, ID_LINE_APPLY = 11;
        private const int ID_SAVE = 12, ID_RUN = 13, ID_CLOSE = 14, ID_ADD_TO_BAR = 15;
        private const int ID_CATEGORY = 50, ID_ROW = 100, ID_TEMPLATE = 200;

        private static readonly (string Name, string[] Lines)[] Templates =
        {
            ("Flow", new[] { "if hp < 50", "if mana < 30", "if poisoned", "if not hidden", "if targethp < 30", "if targetrange <= 1", "if not targetalive", "if count 0x0E21 < 5", "elseif hp < 70", "else", "endif", "loop", "loop 3", "endloop", "wait 500", "wait 1000", "waitfortarget 3000", "stop" }),
            ("Target", new[] { "target last", "target self", "target nearest", "target next", "settarget nearest", "settarget next", "attack nearest", "attack last", "action healthbar_target", "action cancel_target" }),
            ("Spells", new[] { "cast MagicArrow", "cast Harm", "cast Fireball", "cast Lightning", "cast MindBlast", "cast EnergyBolt", "cast Explosion", "cast FlameStrike", "cast Paralyze", "cast Poison", "cast Curse", "cast Heal", "cast GreaterHeal", "cast Cure", "cast ArchCure", "cast MagicReflection", "cast Teleport", "cast Recall", "cast Invisibility", "cast Dispel" }),
            ("Items/Skills", new[] { "useitem 0x0E21", "useitem 0x0F0C", "useitem 0x0F07", "useitem 0x0F0B", "useitem 0x0F09", "action bandage_self", "action bandage_target", "skill Hiding", "skill Stealth", "skill Meditation", "skill DetectingHidden", "skill Anatomy", "skill EvaluatingIntelligence" }),
            ("Actions", new[] { "action attack_nearest", "action target_next", "action war_peace", "action last_spell", "action last_object", "action all_names", "action open:Backpack", "action open:Paperdoll", "action open:Skills", "action layout_next", "say Guards!", "say I will aid thee" }),
        };

        private readonly List<Control> _dynamic = new List<Control>();
        private readonly StbTextBox _nameBox, _lineBox;
        private readonly Label _status;
        private MobileMacroSet _set;
        private int _macro, _selected = -1, _scroll, _category;

        private MacroEditorGump(World world) : base(world, 0, 0)
        {
            CanMove = true;
            AcceptMouseInput = true;
            CanCloseWithRightClick = true;
            Width = W;
            Height = H;
            X = Math.Max(0, (TouchInput.ScreenW - W) / 2);
            Y = Math.Max(0, (TouchInput.ScreenH - H) / 2);

            MobileMacroRunner.EnsureLoaded();
            _set = MobileMacroRunner.Macros;

            if (_set.Macros.Count == 0)
            {
                _set.Macros.Add(new MobileMacro { Name = "New Macro" });
            }

            Add(new AlphaBlendControl(0.88f) { Width = W, Height = H });

            // macro switcher
            Add(Btn(8, 8, 36, 28, "<", ID_PREV_MACRO));
            Add(new ResizePic(0x0BB8) { X = 48, Y = 8, Width = 200, Height = 28 });
            Add(_nameBox = new StbTextBox(1, 40, 190, true, FontStyle.None, 0x0481) { X = 54, Y = 12, Width = 190, Height = 22 });
            Add(Btn(252, 8, 36, 28, ">", ID_NEXT_MACRO));
            Add(Btn(294, 8, 70, 28, "Rename", ID_RENAME));
            Add(Btn(368, 8, 56, 28, "New", ID_NEW));
            Add(Btn(428, 8, 64, 28, "Delete", ID_DELETE_MACRO, 0x0021));
            Add(Btn(W - 104, 8, 96, 28, "On Bar", ID_ADD_TO_BAR));

            // line editor
            Add(new ResizePic(0x0BB8) { X = 8, Y = H - 110, Width = 300, Height = 28 });
            Add(_lineBox = new StbTextBox(1, 80, 290, true, FontStyle.None, 0x0481) { X = 14, Y = H - 106, Width = 290, Height = 22 });
            Add(Btn(312, H - 110, 70, 28, "Set line", ID_LINE_APPLY));

            Add(Btn(8, H - 76, 60, 30, "Up", ID_LINE_UP));
            Add(Btn(72, H - 76, 60, 30, "Down", ID_LINE_DOWN));
            Add(Btn(136, H - 76, 70, 30, "Delete", ID_LINE_DELETE, 0x0021));
            Add(Btn(210, H - 76, 44, 30, "^", ID_SCROLL_UP));
            Add(Btn(258, H - 76, 44, 30, "v", ID_SCROLL_DOWN));

            Add(Btn(8, H - 40, 90, 30, "Save", ID_SAVE, 0x0044));
            Add(Btn(102, H - 40, 90, 30, "Run", ID_RUN));
            Add(Btn(196, H - 40, 90, 30, "Close", ID_CLOSE));

            Add(_status = new Label("", true, 0x0481, 300, 1) { X = 300, Y = H - 34 });

            Rebuild();
        }

        public static void Open(World world)
        {
            UIManager.GetGump<MacroEditorGump>()?.Dispose();
            UIManager.Add(new MacroEditorGump(world));
        }

        private MobileMacro Current => _set.Macros[Math.Clamp(_macro, 0, _set.Macros.Count - 1)];

        private static NiceButton Btn(int x, int y, int w, int h, string text, int id, ushort hue = 0xFFFF) =>
            new NiceButton(x, y, w, h, ButtonAction.Activate, text, hue: hue) { ButtonParameter = id, IsSelectable = false };

        private void Rebuild()
        {
            foreach (Control c in _dynamic)
            {
                c.Dispose();
            }

            _dynamic.Clear();

            MobileMacro m = Current;
            _nameBox.SetText(m.Name);

            // step list, indented by nesting
            int depth = 0;
            var indents = new List<int>();

            foreach (string line in m.Lines)
            {
                string op = FirstWord(line);

                if (op is "endif" or "endloop" or "else" or "elseif")
                {
                    depth = Math.Max(0, depth - 1);
                }

                indents.Add(depth);

                if (op is "if" or "loop" or "else" or "elseif")
                {
                    depth++;
                }
            }

            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, m.Lines.Count - VISIBLE_LINES + 1));

            for (int row = 0; row < VISIBLE_LINES; row++)
            {
                int index = _scroll + row;
                int y = 44 + row * ROW_H;

                if (index < m.Lines.Count)
                {
                    string text = new string(' ', indents[index] * 3) + $"{index + 1}. {m.Lines[index].Trim()}";
                    AddDynamic(new NiceButton(8, y, 300, ROW_H - 2, ButtonAction.Activate, text, align: TEXT_ALIGN_TYPE.TS_LEFT, hue: index == _selected ? (ushort)0x0035 : (ushort)0xFFFF)
                    {
                        ButtonParameter = ID_ROW + index,
                        IsSelectable = false
                    });
                }
                else if (index == m.Lines.Count)
                {
                    AddDynamic(new Label(m.Lines.Count == 0 ? "(empty: pick a step on the right)" : "(end)", true, 0x0386, 300, 1) { X = 12, Y = y + 6 });
                }
            }

            // template categories + templates
            for (int c = 0; c < Templates.Length; c++)
            {
                AddDynamic(Btn(318 + c * 64, 44, 62, 26, Templates[c].Name, ID_CATEGORY + c, c == _category ? (ushort)0x0035 : (ushort)0xFFFF));
            }

            string[] lines = Templates[_category].Lines;

            for (int i = 0; i < lines.Length; i++)
            {
                int col = i % 2, row = i / 2;
                AddDynamic(Btn(318 + col * 158, 76 + row * 28, 154, 26, lines[i], ID_TEMPLATE + i));
            }

            _lineBox.SetText(_selected >= 0 && _selected < m.Lines.Count ? m.Lines[_selected].Trim() : "");

            string err = MobileMacroRunner.Validate(m.Lines);
            _status.Text = err == null ? $"OK - {m.Lines.Count} steps" : err;
            _status.Hue = err == null ? (ushort)0x0044 : (ushort)0x0021;
        }

        private void AddDynamic(Control c)
        {
            _dynamic.Add(c);
            Add(c);
        }

        private static string FirstWord(string line)
        {
            line = (line ?? "").Trim();
            int sp = line.IndexOf(' ');

            return (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
        }

        public override void OnButtonClick(int buttonID)
        {
            MobileMacro m = Current;

            if (buttonID >= ID_TEMPLATE)
            {
                string[] lines = Templates[_category].Lines;
                int t = buttonID - ID_TEMPLATE;

                if (t < lines.Length)
                {
                    int at = _selected < 0 ? m.Lines.Count : _selected + 1;
                    m.Lines.Insert(at, lines[t]);
                    _selected = at;

                    if (_selected >= _scroll + VISIBLE_LINES)
                    {
                        _scroll = _selected - VISIBLE_LINES + 1;
                    }
                }

                Rebuild();

                return;
            }

            if (buttonID >= ID_ROW)
            {
                int row = buttonID - ID_ROW;
                _selected = _selected == row ? -1 : row;
                Rebuild();

                return;
            }

            if (buttonID >= ID_CATEGORY)
            {
                _category = buttonID - ID_CATEGORY;
                Rebuild();

                return;
            }

            switch (buttonID)
            {
                case ID_PREV_MACRO:
                case ID_NEXT_MACRO:
                    _macro = (_macro + (buttonID == ID_NEXT_MACRO ? 1 : -1) + _set.Macros.Count) % _set.Macros.Count;
                    _selected = -1;
                    _scroll = 0;

                    break;

                case ID_NEW:
                    _set.Macros.Add(new MobileMacro { Name = UniqueName("New Macro") });
                    _macro = _set.Macros.Count - 1;
                    _selected = -1;
                    _scroll = 0;

                    break;

                case ID_DELETE_MACRO:
                    _set.Macros.RemoveAt(_macro);

                    if (_set.Macros.Count == 0)
                    {
                        _set.Macros.Add(new MobileMacro { Name = "New Macro" });
                    }

                    _macro = Math.Min(_macro, _set.Macros.Count - 1);
                    _selected = -1;
                    _set.Save();

                    break;

                case ID_RENAME:
                    string name = (_nameBox.Text ?? "").Trim();

                    if (name.Length > 0 && (_set.Find(name) == null || _set.Find(name) == m))
                    {
                        m.Name = name;
                    }

                    break;

                case ID_LINE_APPLY:
                    string text = (_lineBox.Text ?? "").Trim();

                    if (_selected >= 0 && _selected < m.Lines.Count)
                    {
                        m.Lines[_selected] = text;
                    }
                    else if (text.Length > 0)
                    {
                        m.Lines.Add(text);
                        _selected = m.Lines.Count - 1;
                    }

                    break;

                case ID_LINE_UP:
                    if (_selected > 0)
                    {
                        (m.Lines[_selected - 1], m.Lines[_selected]) = (m.Lines[_selected], m.Lines[_selected - 1]);
                        _selected--;
                    }

                    break;

                case ID_LINE_DOWN:
                    if (_selected >= 0 && _selected < m.Lines.Count - 1)
                    {
                        (m.Lines[_selected + 1], m.Lines[_selected]) = (m.Lines[_selected], m.Lines[_selected + 1]);
                        _selected++;
                    }

                    break;

                case ID_LINE_DELETE:
                    if (_selected >= 0 && _selected < m.Lines.Count)
                    {
                        m.Lines.RemoveAt(_selected);
                        _selected = Math.Min(_selected, m.Lines.Count - 1);
                    }

                    break;

                case ID_SCROLL_UP:
                    _scroll = Math.Max(0, _scroll - (VISIBLE_LINES - 1));

                    break;

                case ID_SCROLL_DOWN:
                    _scroll += VISIBLE_LINES - 1;

                    break;

                case ID_SAVE:
                    _set.Save();
                    GameActions.Print(World, $"Macros saved.", 0x44);

                    break;

                case ID_RUN:
                    _set.Save();
                    MobileMacroRunner.Run(World, m.Name);

                    break;

                case ID_ADD_TO_BAR:
                    _set.Save();
                    AddToBar(m);

                    break;

                case ID_CLOSE:
                    _set.Save();
                    Dispose();

                    return;
            }

            Rebuild();
        }

        private string UniqueName(string baseName)
        {
            string name = baseName;

            for (int i = 2; _set.Find(name) != null; i++)
            {
                name = $"{baseName} {i}";
            }

            return name;
        }

        /// <summary>Drops a button for this macro in the middle of the current layout and enters edit mode to place it.</summary>
        private void AddToBar(MobileMacro m)
        {
            ActionLayout layout = TouchInput.Current;

            if (layout == null)
            {
                return;
            }

            string label = m.Name.Length > 12 ? m.Name.Substring(0, 12) : m.Name;
            layout.Buttons.Add(new ActionButtonDef { Label = label.Replace(' ', '\n'), Action = "mmacro:" + m.Name, X = 0.5f, Y = 0.5f, Size = 0.13f });
            TouchInput.Layouts.Save();
            TouchInput.MarkChanged();

            if (!TouchInput.EditMode)
            {
                TouchInput.ToggleEditMode();
            }

            GameActions.Print(World, $"'{m.Name}' added to the {layout.Name} layout - drag it where you want it.", 0x35);
        }
    }
}
