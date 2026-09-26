// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ClassicUO.Game;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Touch
{
    /// <summary>
    /// Dev-only automation so the touch layer can be exercised with no human at the
    /// screen. Active only when the UOM_DEV environment variable is set. Every frame the
    /// client checks for &lt;exe&gt;/dev/cmd.txt; when it appears its lines are queued and
    /// the file is deleted. One command runs per frame (except "wait"):
    ///
    ///   down ID X Y      finger down   (X, Y normalised 0..1)
    ///   move ID X Y      finger move
    ///   up ID X Y        finger up
    ///   tap X Y          down, ~80 ms, up on finger 900
    ///   wait N           wait N milliseconds
    ///   action ID        run a MobileActions id directly
    ///   say TEXT         speak (server commands start with [)
    ///   shot PATH        save a screenshot as PNG
    ///   log TEXT         write TEXT to the client log (markers for the test runner)
    /// </summary>
    internal static class DevScript
    {
        public static readonly bool Active = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("UOM_DEV"));

        private static readonly Queue<string> _lines = new Queue<string>();
        private static uint _waitUntil;
        private static uint _nextPoll;

        private static string Dir => Path.Combine(CUOEnviroment.ExecutablePath, "dev");

        public static void Update()
        {
            if (!Active)
            {
                return;
            }

            if (Time.Ticks >= _nextPoll)
            {
                _nextPoll = Time.Ticks + 250;
                string file = Path.Combine(Dir, "cmd.txt");

                if (File.Exists(file))
                {
                    try
                    {
                        foreach (string line in File.ReadAllLines(file))
                        {
                            if (!string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith("#"))
                            {
                                _lines.Enqueue(line.Trim());
                            }
                        }

                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                        // writer still has it open; try again next poll
                    }
                }
            }

            // Update runs many times per drawn frame, so waits are in real time and a
            // screenshot holds the script until the frame has actually been captured.
            if (Time.Ticks < _waitUntil || Client.Game.ScreenshotPending)
            {
                return;
            }

            if (_lines.Count == 0)
            {
                return;
            }

            string cmd = _lines.Dequeue();

            try
            {
                Run(cmd);
            }
            catch (Exception e)
            {
                Log.Error($"[devscript] '{cmd}' failed: {e.Message}");
            }
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        private static void Run(string cmd)
        {
            string[] p = cmd.Split(' ', 2);
            string rest = p.Length > 1 ? p[1] : "";
            string[] a = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            switch (p[0])
            {
                case "down":
                    TouchInput.OnDown(long.Parse(a[0]), TouchInput.ToUi(F(a[1]), F(a[2])));

                    break;

                case "move":
                    TouchInput.OnMove(long.Parse(a[0]), TouchInput.ToUi(F(a[1]), F(a[2])));

                    break;

                case "up":
                    TouchInput.OnUp(long.Parse(a[0]), TouchInput.ToUi(F(a[1]), F(a[2])));

                    break;

                case "tap":
                    TouchInput.OnDown(900, TouchInput.ToUi(F(a[0]), F(a[1])));
                    _lines.Enqueue($"up 900 {a[0]} {a[1]}");

                    // keep the up right behind the down
                    int n = _lines.Count - 1;

                    for (int i = 0; i < n; i++)
                    {
                        _lines.Enqueue(_lines.Dequeue());
                    }

                    _waitUntil = Time.Ticks + 80;

                    break;

                case "wait":
                    _waitUntil = Time.Ticks + uint.Parse(a[0]);

                    break;

                case "action":
                    MobileActions.Run(Client.Game.UO.World, rest);

                    break;

                case "say":
                    GameActions.Say(rest);

                    break;

                case "shot":
                    Client.Game.RequestScreenshot(rest);

                    break;

                case "info":
                    var g = Client.Game;
                    var cam = (g.Scene as Game.Scenes.GameScene)?.Camera.Bounds;
                    Log.Info($"[devscript] dpi={g.DpiScale} client={g.Window.ClientBounds} backbuffer={g.GraphicManager.PreferredBackBufferWidth}x{g.GraphicManager.PreferredBackBufferHeight} ui={TouchInput.ScreenW}x{TouchInput.ScreenH} camera={cam} player={g.UO.World?.Player?.X},{g.UO.World?.Player?.Y},{g.UO.World?.Player?.Z}");

                    break;

                case "log":
                    Log.Info($"[devscript] {rest}");

                    break;

                default:
                    Log.Warn($"[devscript] unknown command '{cmd}'");

                    break;
            }
        }
    }
}
