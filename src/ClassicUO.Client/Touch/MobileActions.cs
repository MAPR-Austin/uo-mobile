// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Network;
using ClassicUO.Utility;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Executes on-screen button actions. Everything that has a ClassicUO macro
    /// equivalent is run as a one-off macro, so targeting, casting and attacking
    /// behave exactly like the desktop client's hotkeys.
    ///
    /// Action ids:
    ///   attack_nearest   select nearest enemy + attack it (fast melee "target closest")
    ///   useonce[:GROUP]  use the next not-yet-used item of a kind (default: a trapped pouch)
    ///   dress:NAME / undress:NAME / dress_save:NAME   dress sets (Touch/Agents)
    ///   target_closest:SPEC / target_next:SPEC   the closest / next mobile by colour and kind, e.g.
    ///                    "red,human", "blue", "enemy,monster" (Touch/SmartTargeting; no SPEC: enemy)
    ///   set_target       next tap on a creature/player makes it the target (no attack)
    ///   target_nearest / target_next / target_prev   hostile selection (sets last target)
    ///   attack_last      attack the last target
    ///   last_target / target_self   answer an open target cursor
    ///   cancel_target    cancel an open target cursor
    ///   healthbar_target pull a health bar for the selected target
    ///   war_peace, all_names, bandage_self, bandage_target, last_spell, last_object
    ///   stun, disarm     toggle the UOR wrestling stun punch / disarm (empty hands)
    ///   arm_disarm       weapon to the pack / back in hand (to drink, cast or bandage)
    ///   spell:Name       cast (cursor stays up for a tap)
    ///   spell_lt:Name    cast, wait for cursor, target last target
    ///   spell_self:Name  cast, wait for cursor, target self
    ///   open:Backpack|Paperdoll|Skills|Journal|Status|MageSpellbook|WorldMap ...
    ///   skill:Name       use a skill (Hiding, Meditation, ...)
    ///   say:text
    ///   mmacro:Name      run (or stop, if running) a phone macro - see MobileMacros
    ///   triggers[:on|off]   switch the phone macros' triggers ("when ..." macros) on or off
    ///   autoloot[:on|off|LIST]   auto loot from corpses (Touch/AutoLoot); LIST: "gold,regs,arrows"
    ///   record           start recording a phone macro, or stop and save it (Touch/MacroRecorder)
    ///   restock[:LIST]   top the pack up from the open bank box or chest: "restock:bandages=100,regs=30"
    ///   organize[:LIST]  move items of LIST (default gold) from the pack into the container opened last
    ///   counters[:on|off]   show or hide the counter strip (a long press on it hides it too)
    ///   macro:Name       run a desktop-client macro (Options > Macros)
    ///   chat             focus the speech line and show the keyboard
    ///   stop_macro, layout_next, edit_layout, macro_editor, uo_macros   HUD control
    /// </summary>
    internal static class MobileActions
    {
        private static MacroSubType _armHand = MacroSubType.RightHand; // the hand arm_disarm last emptied
        private static bool _armStashed; // arm_disarm put a weapon away and hasn't brought it back

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

            SmartTargeting.ClearQueue(); // any other action: a spell's queued target isn't wanted any more

            MacroRecorder.OnAction(action, arg); // recording: the button itself is the step; what it does isn't

            if (MacroRecorder.Quiet == 0)
            {
                MobileMacroRunner.PlayerActedAt = Time.Ticks; // a button the player tapped: triggers and auto loot wait a moment
            }

            MacroRecorder.Quiet++;

            try
            {
                Dispatch(world, action, arg);
            }
            finally
            {
                MacroRecorder.Quiet--;
            }
        }

        private static void Dispatch(World world, string action, string arg)
        {
            switch (action)
            {
                case "record":
                    MacroRecorder.Toggle(world);

                    break;

                case "attack_nearest":
                    {
                        Mobile foe = SmartTargeting.Select(world, arg ?? "enemy", false);

                        if (foe != null)
                        {
                            GameActions.Attack(world, foe.Serial);
                        }
                    }

                    break;

                case "target_closest":
                    SmartTargeting.Select(world, arg ?? "enemy", false);

                    break;

                case "useonce": Agents.UseOnce(world, arg); break;
                case "dress": Agents.Dress(world, arg); break;
                case "undress": Agents.Undress(world, arg); break;
                case "dress_save": Agents.SaveDress(world, arg); break;

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
                    SmartTargeting.Select(world, "enemy", false);

                    break;

                case "target_next":
                    SmartTargeting.Select(world, arg ?? "enemy", true);

                    break;

                case "target_prev":
                    Macro(world, O(MacroType.SelectPrevious, MacroSubType.Hostile));

                    break;

                case "attack_last": Macro(world, O(MacroType.AttackLast)); break;
                case "last_target": SmartTargeting.TargetLast(world); break; // harmful/beneficial memory, queue, range check
                case "target_self": SmartTargeting.TargetSelf(world); break;
                case "war_peace": Macro(world, O(MacroType.WarPeace)); break;
                case "all_names": Macro(world, O(MacroType.AllNames)); break;
                case "bandage_self": Macro(world, O(MacroType.BandageSelf)); break;
                case "bandage_target": Macro(world, O(MacroType.BandageTarget)); break;
                case "last_spell": Macro(world, O(MacroType.LastSpell)); break;
                case "last_object": Macro(world, O(MacroType.LastObject)); break;

                // UOR wrestling moves: each request toggles the move on the server (the client's
                // ability helper tracks its own toggle and can send the wrong one on a second press).
                // ClassicUO's names are swapped against the packets: Send_StunRequest writes 0xBF/0x09,
                // which ServUO (and OSI) handle as the disarm request, and Send_DisarmRequest writes 0x0A,
                // the stun request.
                case "stun": NetClient.Socket.Send_DisarmRequest(); break;
                case "disarm": NetClient.Socket.Send_StunRequest(); break;

                case "arm_disarm": // weapon to the pack (to drink, cast or bandage) / back in hand
                {
                    MacroSubType hand;

                    if (world.Player.FindItemByLayer(Layer.OneHanded) != null)
                    {
                        hand = MacroSubType.RightHand; // a one-hander goes, a shield stays
                        _armStashed = true;
                    }
                    else if (_armStashed)
                    {
                        hand = _armHand; // the weapon put away last comes back
                        _armStashed = false;
                    }
                    else if (world.Player.FindItemByLayer(Layer.TwoHanded) != null)
                    {
                        hand = MacroSubType.LeftHand; // a two-hander
                        _armStashed = true;
                    }
                    else
                    {
                        // hands empty but out of step (a press while dragging, a relog): ask the
                        // client's own arm/disarm to bring back whatever it remembers for that hand
                        hand = _armHand;
                    }

                    _armHand = hand;
                    Macro(world, O(MacroType.ArmDisarm, hand));

                    break;
                }

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
                        if (world.TargetManager.IsTargeting)
                        {
                            world.TargetManager.CancelTarget(); // a stale cursor would take this spell's target
                        }

                        Macro(world, O(MacroType.CastSpell, spell));
                        SmartTargeting.QueueLast(); // answered when this spell's cursor comes up
                    }

                    break;

                case "spell_self":
                    if (TryParse(arg, out spell))
                    {
                        if (world.TargetManager.IsTargeting)
                        {
                            world.TargetManager.CancelTarget();
                        }

                        Macro(world, O(MacroType.CastSpell, spell));
                        SmartTargeting.QueueSelf();
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

                case "triggers":
                    MobileMacroRunner.SetTriggers(world, arg);

                    break;

                case "autoloot":
                    AutoLoot.Set(world, arg);

                    break;

                case "restock":
                    Agents.Restock(world, arg);

                    break;

                case "organize":
                    Agents.Organize(world, arg);

                    break;

                case "counters":
                    PhoneDefaults.ToggleStrip(world, arg);

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
            SmartTargeting.Remember(world, mobile); // the last harmful (foe) or beneficial (friend) target
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
