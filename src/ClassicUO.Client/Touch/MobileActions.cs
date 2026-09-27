// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Utility;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Executes on-screen button actions. Everything that has a ClassicUO macro
    /// equivalent is run as a one-off macro, so targeting, casting and attacking
    /// behave exactly like the desktop client's hotkeys.
    ///
    /// Action ids:
    ///   attack_nearest   select nearest hostile + attack it (fast melee "target closest")
    ///   set_target       next tap on a creature/player makes it the target (no attack)
    ///   target_nearest / target_next / target_prev   hostile selection (sets last target)
    ///   attack_last      attack the last target
    ///   last_target / target_self   answer an open target cursor
    ///   cancel_target    cancel an open target cursor
    ///   healthbar_target pull a health bar for the selected target
    ///   war_peace, all_names, bandage_self, bandage_target, last_spell, last_object
    ///   spell:Name       cast (cursor stays up for a tap)
    ///   spell_lt:Name    cast, wait for cursor, target last target
    ///   spell_self:Name  cast, wait for cursor, target self
    ///   open:Backpack|Paperdoll|Skills|Journal|Status|MageSpellbook|WorldMap ...
    ///   skill:Name       use a skill (Hiding, Meditation, ...)
    ///   say:text
    ///   mmacro:Name      run (or stop, if running) a phone macro - see MobileMacros
    ///   macro:Name       run a desktop-client macro (Options > Macros)
    ///   chat             focus the speech line and show the keyboard
    ///   stop_macro, layout_next, edit_layout, macro_editor, uo_macros   HUD control
    /// </summary>
    internal static class MobileActions
    {
        public static void Run(World world, string action)
        {
            if (world == null || !world.InGame || string.IsNullOrEmpty(action))
            {
                return;
            }

            string arg = null;
            int colon = action.IndexOf(':');

            if (colon >= 0)
            {
                arg = action.Substring(colon + 1);
                action = action.Substring(0, colon);
            }

            switch (action)
            {
                case "attack_nearest":
                    Macro(world, O(MacroType.SelectNearest, MacroSubType.Hostile), O(MacroType.AttackSelectedTarget));

                    break;

                case "set_target": // next tap on a creature/player makes it the current target
                    if (world.TargetManager.IsTargeting)
                    {
                        world.TargetManager.CancelTarget();
                    }

                    world.TargetManager.SetTargeting(obj =>
                    {
                        if (obj is Mobile m && m != world.Player)
                        {
                            SetCurrentTarget(world, m);
                        }
                    }, CursorType.Target, TargetType.Neutral);
                    GameActions.Print(world, "Tap a creature or player to target it.", 0x35);

                    break;

                case "target_nearest":
                    Macro(world, O(MacroType.SelectNearest, MacroSubType.Hostile));

                    break;

                case "target_next":
                    Macro(world, O(MacroType.SelectNext, MacroSubType.Hostile));

                    break;

                case "target_prev":
                    Macro(world, O(MacroType.SelectPrevious, MacroSubType.Hostile));

                    break;

                case "attack_last": Macro(world, O(MacroType.AttackLast)); break;
                case "last_target": Macro(world, O(MacroType.LastTarget)); break;
                case "target_self": Macro(world, O(MacroType.TargetSelf)); break;
                case "war_peace": Macro(world, O(MacroType.WarPeace)); break;
                case "all_names": Macro(world, O(MacroType.AllNames)); break;
                case "bandage_self": Macro(world, O(MacroType.BandageSelf)); break;
                case "bandage_target": Macro(world, O(MacroType.BandageTarget)); break;
                case "last_spell": Macro(world, O(MacroType.LastSpell)); break;
                case "last_object": Macro(world, O(MacroType.LastObject)); break;

                case "cancel_target":
                    if (world.TargetManager.IsTargeting)
                    {
                        world.TargetManager.CancelTarget();
                    }

                    break;

                case "healthbar_target":
                    OpenHealthBar(world, world.TargetManager.SelectedTarget != 0 ? world.TargetManager.SelectedTarget : world.TargetManager.LastTargetInfo.Serial);

                    break;

                case "spell":
                    if (TryParse(arg, out MacroSubType spell))
                    {
                        Macro(world, O(MacroType.CastSpell, spell));
                    }

                    break;

                case "spell_lt":
                    if (TryParse(arg, out spell))
                    {
                        Macro(world, O(MacroType.CastSpell, spell), O(MacroType.WaitForTarget), O(MacroType.LastTarget));
                    }

                    break;

                case "spell_self":
                    if (TryParse(arg, out spell))
                    {
                        Macro(world, O(MacroType.CastSpell, spell), O(MacroType.WaitForTarget), O(MacroType.TargetSelf));
                    }

                    break;

                case "open":
                    if (TryParse(arg, out MacroSubType gump))
                    {
                        Macro(world, O(MacroType.Open, gump));
                    }

                    break;

                case "skill":
                    if (TryParse(arg, out MacroSubType skill))
                    {
                        Macro(world, O(MacroType.UseSkill, skill));
                    }

                    break;

                case "say":
                    Macro(world, new MacroObjectString(MacroType.Say, MacroSubType.MSC_NONE, arg ?? ""));

                    break;

                case "macro":
                    Game.Managers.Macro user = world.Macros.FindMacro(arg ?? "");

                    if (user != null && user.Items is MacroObject first)
                    {
                        Execute(world, first);
                    }
                    else
                    {
                        GameActions.Print(world, $"No macro named '{arg}'.");
                    }

                    break;

                case "chat": // raise the keyboard on the speech line
                    TouchInput.OpenChat();

                    break;

                case "mmacro":
                    MobileMacroRunner.Run(world, arg ?? "");

                    break;

                case "stop_macro":
                    MobileMacroRunner.Stop();

                    break;

                case "macro_editor":
                    MacroEditorGump.Open(world);

                    break;

                case "uo_macros": // the desktop client's own macro editor
                    GameActions.OpenSettings(world, 4);

                    break;

                case "layout_next":
                    TouchInput.NextLayout();

                    break;

                case "edit_layout":
                    TouchInput.ToggleEditMode();

                    break;

                default:
                    GameActions.Print(world, $"Unknown button action '{action}'.");

                    break;
            }
        }

        private static uint _seenLastTarget, _seenSelected, _seenAttack, _shown;

        /// <summary>
        /// The mobile the HUD shows as "your target": whichever changed most recently of the last
        /// target (Set Target, Next Target, a spell's target), the selected target, or the last
        /// mobile you attacked (Attack buttons, double-tap in war mode).
        /// </summary>
        public static Mobile CurrentTarget(World world)
        {
            if (world?.Player == null)
            {
                return null;
            }

            TargetManager tm = world.TargetManager;
            uint last = tm.LastTargetInfo.IsEntity ? tm.LastTargetInfo.Serial : 0;

            if (last != _seenLastTarget) { _seenLastTarget = last; if (SerialHelper.IsMobile(last)) _shown = last; }
            if (tm.SelectedTarget != _seenSelected) { _seenSelected = tm.SelectedTarget; if (SerialHelper.IsMobile(_seenSelected)) _shown = _seenSelected; }
            if (tm.LastAttack != _seenAttack) { _seenAttack = tm.LastAttack; if (SerialHelper.IsMobile(_seenAttack)) _shown = _seenAttack; }

            return SerialHelper.IsMobile(_shown) && world.Mobiles.TryGetValue(_shown, out Mobile m) && m != world.Player ? m : null;
        }

        /// <summary>Same effect as the client's target-selection macros (MacroManager.SetLastTarget).</summary>
        public static void SetCurrentTarget(World world, Mobile mobile)
        {
            world.TargetManager.NewTargetSystemSerial = mobile.Serial;
            world.TargetManager.SelectedTarget = mobile.Serial;
            world.TargetManager.LastTargetInfo.SetEntity(mobile.Serial);
            GameActions.MessageOverhead(world, $"Target: {mobile.Name}", Notoriety.GetHue(mobile.NotorietyFlag), world.Player);
        }

        public static void OpenHealthBar(World world, uint serial)
        {
            if (!SerialHelper.IsMobile(serial))
            {
                return;
            }

            if (!world.Mobiles.TryGetValue(serial, out Mobile mobile) || mobile == null || UIManager.GetGump<BaseHealthBarGump>(serial) != null)
            {
                return;
            }

            BaseHealthBarGump bar = ProfileManager.CurrentProfile.CustomBarsToggled
                ? new HealthBarGumpCustom(world, mobile)
                : new HealthBarGump(world, mobile);

            // Stack pulled bars down the left edge, under the status area.
            int count = 0;

            foreach (var c in UIManager.Gumps)
            {
                if (c is BaseHealthBarGump && !c.IsDisposed)
                {
                    count++;
                }
            }

            bar.X = 8;
            bar.Y = 90 + count * 50;
            UIManager.Add(bar);
        }

        private static bool TryParse(string name, out MacroSubType value) =>
            Enum.TryParse(name, true, out value) && value != MacroSubType.MSC_NONE;

        private static MacroObject O(MacroType type, MacroSubType sub = MacroSubType.MSC_NONE) => new MacroObject(type, sub);

        private static void Macro(World world, params MacroObject[] steps)
        {
            var macro = new Game.Managers.Macro("__mobile");

            foreach (MacroObject step in steps)
            {
                macro.PushToBack(step);
            }

            Execute(world, (MacroObject)macro.Items);
        }

        private static void Execute(World world, MacroObject first)
        {
            world.Macros.SetMacroToExecute(first);
            world.Macros.WaitForTargetTimer = 0;
            world.Macros.Update();
        }
    }
}
