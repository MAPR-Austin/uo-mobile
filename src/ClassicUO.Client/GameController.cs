// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.Scenes;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Input;
using ClassicUO.Touch;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using static SDL3.SDL;

namespace ClassicUO
{
    internal unsafe class GameController : Microsoft.Xna.Framework.Game
    {
        private SDL_EventFilter _filter;

        private bool _ignoreNextTextInput;
        private readonly float[] _intervalFixedUpdate = new float[2];
        private double _totalElapsed, _currentFpsTime;
        private uint _totalFrames;
        private UltimaBatcher2D _uoSpriteBatch;
        private RenderTargets _renderTargets = new();
        private readonly RenderLists _renderLists = new();
        private bool _suppressedDraw;
        private bool _pluginsInitialized = false;
        private float _displayScale;

        public GameController(IPluginHost pluginHost)
        {
            GraphicManager = new GraphicsDeviceManager(this);

            GraphicManager.PreparingDeviceSettings += (sender, e) =>
            {
                PresentationParameters pp = e.GraphicsDeviceInformation.PresentationParameters;
                pp.RenderTargetUsage = RenderTargetUsage.DiscardContents;

                // Phone: FNA shapes the backbuffer by the last orientation *event* (landscape until
                // one arrives), so a launch held in portrait got a landscape backbuffer. The window's
                // own shape is the truth: the app can't resize its window on iOS, and on desktop
                // (UOM_PHONE_FIT) this keeps the window the size it was dragged/set to.
                Rectangle bounds = Window.ClientBounds;

                if (PhoneFit && bounds.Width > 0 && bounds.Height > 0)
                {
                    pp.BackBufferWidth = bounds.Width;
                    pp.BackBufferHeight = bounds.Height;
                    pp.DisplayOrientation = bounds.Height > bounds.Width ? DisplayOrientation.Portrait : DisplayOrientation.LandscapeRight;
                }
            };

            GraphicManager.PreferredDepthStencilFormat = DepthFormat.Depth24Stencil8;
            SetVSync(false);

            Window.ClientSizeChanged += WindowOnClientSizeChanged;
            Window.AllowUserResizing = true;
            Window.Title = $"ClassicUO - {CUOEnviroment.Version}";
            IsMouseVisible = Settings.GlobalSettings.RunMouseInASeparateThread;

            IsFixedTimeStep = false; // Settings.GlobalSettings.FixedTimeStep;
            TargetElapsedTime = TimeSpan.FromMilliseconds(1000.0 / 250.0);
            PluginHost = pluginHost;
        }

        public Scene Scene { get; private set; }
        public AudioManager Audio { get; private set; }
        public UltimaOnline UO { get; } = new UltimaOnline();
        public IPluginHost PluginHost { get; private set; }
        public GraphicsDeviceManager GraphicManager { get; }

        public Rectangle ClientBounds
        {
            get
            {
                var window_rectangle = Window.ClientBounds;
                return new Rectangle(
                    window_rectangle.X,
                    window_rectangle.Y,
                    (int)((float)(window_rectangle.Width) / DpiScale),
                    (int)((float)(window_rectangle.Height) / DpiScale)
                );
            }
        }

        public readonly uint[] FrameDelay = new uint[2];

        private readonly List<(uint, Action)> _queuedActions = new ();

        public void EnqueueAction(uint time, Action action)
        {
            _queuedActions.Add((Time.Ticks + time, action));
        }

        protected override void Initialize()
        {
            if (GraphicManager.GraphicsDevice.Adapter.IsProfileSupported(GraphicsProfile.HiDef))
            {
                GraphicManager.GraphicsProfile = GraphicsProfile.HiDef;
            }

            GraphicManager.ApplyChanges();

            // iOS: the window is the whole screen; FNA starts with its 800x480 default backbuffer.
            FillScreenOnPhone();

            if (OperatingSystem.IsIOS())
            {
                ClassicUO.Utility.Platforms.PlatformHelper.OpenUrlOverride = url => SDL_OpenURL(url);
            }

            SetRefreshRate(Settings.GlobalSettings.FPS);
            _uoSpriteBatch = new UltimaBatcher2D(GraphicsDevice);

            _filter = HandleSdlEvent;
            SDL_SetEventFilter(_filter, IntPtr.Zero);

            // On phones text input raises the on-screen keyboard; Touch/TouchInput shows it on demand.
            if (!TouchInput.HasVirtualKeyboard)
            {
                Microsoft.Xna.Framework.Input.TextInputEXT.StartTextInput();
            }

            FitUiToPhone();

            if (OperatingSystem.IsIOS())
            {
                SetVSync(true); // don't render at 250 fps on a phone battery
            }

            _displayScale = DpiScale;

            base.Initialize();
        }

        /// <summary>
        /// iOS: make the backbuffer the screen (scenes resize the "window" to 640x480 on desktop,
        /// which would squash a 4:3 image onto a phone) and refit the UI scale.
        /// </summary>
        /// <summary>
        /// The phone screen-fit path (iOS; on desktop with UOM_PHONE_FIT=1 so it can be tested by
        /// resizing the window to a phone shape).
        /// </summary>
        internal static readonly bool PhoneFit = OperatingSystem.IsIOS() || Environment.GetEnvironmentVariable("UOM_PHONE_FIT") == "1";

        internal void FillScreenOnPhone()
        {
            if (!PhoneFit)
            {
                return;
            }

            Rectangle bounds = Window.ClientBounds;

            if (bounds.Width > 0 && bounds.Height > 0 && (PreferredDiffers(bounds) || DeviceDiffers(bounds)))
            {
                GraphicManager.PreferredBackBufferWidth = bounds.Width;
                GraphicManager.PreferredBackBufferHeight = bounds.Height;
                GraphicManager.ApplyChanges(); // the setters always mark a change, so this resets
            }

            FitUiToPhone();
        }

        private bool PreferredDiffers(Rectangle bounds) =>
            GraphicManager.PreferredBackBufferWidth != bounds.Width || GraphicManager.PreferredBackBufferHeight != bounds.Height;

        // FNA's orientation handler resets the device itself, shaped by its orientation event.
        private bool DeviceDiffers(Rectangle bounds)
        {
            PresentationParameters pp = GraphicManager.GraphicsDevice?.PresentationParameters;

            return pp != null && (pp.BackBufferWidth != bounds.Width || pp.BackBufferHeight != bounds.Height);
        }

        // Portrait with a window wider than the 480-unit screen open: fit 640x480 like the login screen.
        private bool _phoneWideUi;

        /// <summary>
        /// iOS rotation: FNA flips the device's backbuffer but not the preferred size ClassicUO reads,
        /// and no resize event reaches the game, so compare every frame and refill the screen. Also
        /// refits when a too-wide window opens or closes in portrait.
        /// </summary>
        private void FollowPhoneRotation()
        {
            Rectangle bounds = Window.ClientBounds;

            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            bool resized = PreferredDiffers(bounds) || DeviceDiffers(bounds);
            bool wide = TouchInput.WantsWideUi();

            if (!resized && wide == _phoneWideUi)
            {
                return;
            }

            _phoneWideUi = wide;
            FillScreenOnPhone();
            TouchInput.KeepGumpsOnScreen();
        }

        /// <summary>
        /// iOS: pick ScreenScale (UI space = backbuffer / DpiScale). UO's windows assume ~480 UI units:
        /// in game the screen's short side is 480 units (landscape: 480 high; portrait: 480 wide).
        /// The login/character screens are one 640x480 picture, so there the UI must contain 640x480;
        /// so must portrait while a window wider than 480 units (macro editor, options) is open.
        /// Logged for tuning.
        /// </summary>
        private void FitUiToPhone()
        {
            if (!PhoneFit)
            {
                return;
            }

            float display = SDL_GetWindowDisplayScale(Window.Handle);

            if (display <= 0f)
            {
                display = 1f;
            }

            int w = GraphicManager.PreferredBackBufferWidth;
            int h = GraphicManager.PreferredBackBufferHeight;

            if (w <= 0 || h <= 0)
            {
                return;
            }

            float scale = Scene is GameScene && !_phoneWideUi
                ? Math.Min(w, h) / (display * 480f)
                : LoginLayout.Active
                    ? LoginLayout.FitScale(w, h, display) // the login screens' picture inside the safe area
                    : Math.Min(w / 640f, h / 480f) / display;

            ScreenScale = Math.Clamp(scale, 0.25f, 8f);
            Log.Info($"[UOMobile] display scale {display}, window {Window.ClientBounds}, backbuffer {w}x{h}, scene {Scene?.GetType().Name}, ScreenScale {ScreenScale}, UI {(int)(w / DpiScale)}x{(int)(h / DpiScale)}");
        }

        protected override void LoadContent()
        {
            base.LoadContent();

            Fonts.Initialize(GraphicsDevice);
            SolidColorTextureCache.Initialize(GraphicsDevice);
            Audio = new AudioManager();

            // Phones: sound effects at 48 kHz, band-limited (FAudio's own upsampling of the 22 kHz
            // originals is audible as static). Touch mode on desktop takes the same path, for tests.
            ClassicUO.IO.Audio.Sound.OutputRate = TouchInput.Enabled ? 48000 : 0;

            var bytes = Loader.GetBackgroundImage().ToArray();
            using var ms = new MemoryStream(bytes);
            _renderTargets.InitializeBackground(Texture2D.FromStream(GraphicsDevice, ms));

            // Phone: the game files come from the shard's file server. Nothing below may touch
            // them until they're in place, so the download screen runs first (Update/Draw) and
            // finishes loading when it's done.
            if (GameFiles.Configured)
            {
                _download = new DownloadScreen(new GameFiles(Settings.GlobalSettings.UltimaOnlineDirectory));

                return;
            }

            FinishLoadContent();
        }

        private DownloadScreen _download;

        /// <summary>Dev automation: a tap on the download screen (UI units).</summary>
        internal bool DownloadTap(Point ui)
        {
            _download?.OnTap(ui);

            return _download != null;
        }

        /// <summary>LoadContent's second half: everything that reads the UO files.</summary>
        private void FinishLoadContent()
        {
#if false
            SetScene(new MainScene(this));
#else
            UO.Load(this);
            Audio.Initialize();
            // TODO: temporary fix to avoid crash when laoding plugins
            Settings.GlobalSettings.Encryption = (byte) NetClient.Socket.Load(UO.FileManager.Version, (EncryptionType) Settings.GlobalSettings.Encryption);

            Log.Trace("Loading plugins...");
            PluginHost?.Initialize();

            foreach (string p in Settings.GlobalSettings.Plugins)
            {
                Plugin.Create(p);
            }
            _pluginsInitialized = true;

            Log.Trace("Done!");

            SetScene(new LoginScene(UO.World));
#endif
            SetWindowPositionBySettings();
        }

        protected override void UnloadContent()
        {
            SDL_GetWindowBordersSize(Window.Handle, out int top, out int left, out _, out _);

            Settings.GlobalSettings.WindowPosition = new Point(
                Math.Max(0, Window.ClientBounds.X - left),
                Math.Max(0, Window.ClientBounds.Y - top)
            );

            Audio?.StopMusic();
            Settings.GlobalSettings.Save();
            Plugin.OnClosing();

            UO.Unload();

            base.UnloadContent();
        }

        public void SetWindowTitle(string title)
        {
            if (string.IsNullOrEmpty(title))
            {
#if DEV_BUILD
                Window.Title = $"ClassicUO [dev] - {CUOEnviroment.Version}";
#else
                Window.Title = $"ClassicUO - {CUOEnviroment.Version}";
#endif
            }
            else
            {
#if DEV_BUILD
                Window.Title = $"{title} - ClassicUO [dev] - {CUOEnviroment.Version}";
#else
                Window.Title = $"{title} - ClassicUO - {CUOEnviroment.Version}";
#endif
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T GetScene<T>() where T : Scene
        {
            return Scene as T;
        }

        public void SetScene(Scene scene)
        {
            Scene?.Dispose();
            Scene = scene;
            Scene?.Load();
        }

        public void SetVSync(bool value)
        {
            GraphicManager.SynchronizeWithVerticalRetrace = value;
        }

        public void SetRefreshRate(int rate)
        {
            if (rate < Constants.MIN_FPS)
            {
                rate = Constants.MIN_FPS;
            }
            else if (rate > Constants.MAX_FPS)
            {
                rate = Constants.MAX_FPS;
            }

            float frameDelay;

            if (rate == Constants.MIN_FPS)
            {
                // The "real" UO framerate is 12.5. Treat "12" as "12.5" to match.
                frameDelay = 80;
            }
            else
            {
                frameDelay = 1000.0f / rate;
            }

            FrameDelay[0] = FrameDelay[1] = (uint)frameDelay;
            FrameDelay[1] = FrameDelay[1] >> 1;

            Settings.GlobalSettings.FPS = rate;

            _intervalFixedUpdate[0] = frameDelay;
            _intervalFixedUpdate[1] = 217; // 5 FPS
        }

        private void SetWindowPosition(int x, int y)
        {
            SDL_SetWindowPosition(Window.Handle, x, y);
        }

        public void SetWindowSize(int width, int height)
        {
            //width = (int) ((double) width * Client.Game.GraphicManager.PreferredBackBufferWidth / Client.Game.Window.ClientBounds.Width);
            //height = (int) ((double) height * Client.Game.GraphicManager.PreferredBackBufferHeight / Client.Game.Window.ClientBounds.Height);

            /*if (CUOEnviroment.IsHighDPI)
            {
                width *= 2;
                height *= 2;
            }
            */

            GraphicManager.PreferredBackBufferWidth = width;
            GraphicManager.PreferredBackBufferHeight = height;
            GraphicManager.ApplyChanges();
        }

        public void SetWindowBorderless(bool borderless)
        {
            SDL_WindowFlags flags = (SDL_WindowFlags)SDL_GetWindowFlags(Window.Handle);

            if ((flags & SDL_WindowFlags.SDL_WINDOW_BORDERLESS) != 0 && borderless)
            {
                return;
            }

            if ((flags & SDL_WindowFlags.SDL_WINDOW_BORDERLESS) == 0 && !borderless)
            {
                return;
            }

            SDL_SetWindowBordered(
                Window.Handle,
                !borderless
            );
            SDL_DisplayMode* displayMode = (SDL_DisplayMode * )SDL_GetCurrentDisplayMode(
                SDL_GetDisplayForWindow(Window.Handle)
            );

            int width = displayMode->w;
            int height = displayMode->h;

            if (borderless)
            {
                SetWindowSize(width, height);
                SDL_GetDisplayUsableBounds(
                    SDL_GetDisplayForWindow(Window.Handle),
                    out SDL_Rect rect
                );
                SDL_SetWindowPosition(Window.Handle, rect.x, rect.y);
            }
            else
            {
                SDL_GetWindowBordersSize(Window.Handle, out int top, out _, out int bottom, out _);

                SetWindowSize(width, height - (top - bottom));
                SetWindowPositionBySettings();
            }

            WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();

            if (viewport != null && ProfileManager.CurrentProfile.GameWindowFullSize)
            {
                viewport.ResizeGameWindow(new Point(width, height));
                viewport.X = -5;
                viewport.Y = -5;
            }
        }

        public void MaximizeWindow()
        {
            SDL_MaximizeWindow(Window.Handle);

            GraphicManager.PreferredBackBufferWidth = Client.Game.Window.ClientBounds.Width;
            GraphicManager.PreferredBackBufferHeight = Client.Game.Window.ClientBounds.Height;
            GraphicManager.ApplyChanges();
        }

        public bool IsWindowMaximized()
        {
            SDL_WindowFlags flags = (SDL_WindowFlags)SDL_GetWindowFlags(Window.Handle);

            return (flags & SDL_WindowFlags.SDL_WINDOW_MAXIMIZED) != 0;
        }

        public void RestoreWindow()
        {
            SDL_RestoreWindow(Window.Handle);
        }

        public void SetWindowPositionBySettings()
        {
            var borderSizesRetrieved = SDL_GetWindowBordersSize(Window.Handle, out int top, out int left, out _, out _);

            if (!borderSizesRetrieved)
            {
                top = 0;
                left = 0;
            }

            if (Settings.GlobalSettings.WindowPosition.HasValue)
            {
                int x = left + Settings.GlobalSettings.WindowPosition.Value.X;
                int y = top + Settings.GlobalSettings.WindowPosition.Value.Y;
                x = Math.Max(0, x);
                y = Math.Max(0, y);

                SDL_Point desiredStartPoint = new() { x = x, y = y };
                var displayId = SDL_GetDisplayForPoint(ref desiredStartPoint);
                if (displayId <= 0)
                {
                    // Make sure the window is actually in view and not out of bounds
                    SetWindowPosition(left, top);
                }

                var boundsRetrieved = SDL_GetDisplayUsableBounds(displayId, out SDL_Rect displayBounds);
                if (!boundsRetrieved)
                {
                    return; // we have no clue - the user is unfortunately on their own
                }

                if (x < displayBounds.x || x >= displayBounds.x + displayBounds.w)
                {
                    // Make sure the window is actually in view and not out of bounds
                    x = left + displayBounds.x;
                }

                if (y < displayBounds.y || y >= displayBounds.y + displayBounds.h)
                {
                    y = top + displayBounds.y;
                }

                SetWindowPosition(x, y);
            }
        }

        private volatile bool _inBackground;

        /// <summary>iOS has the app in the background (no GPU work then).</summary>
        internal bool InBackground => _inBackground;

        private void SaveForSuspend()
        {
            try
            {
                if (UO.World != null && UO.World.InGame && ProfileManager.CurrentProfile != null && ProfileManager.ProfilePath != null)
                {
                    if (Scene is GameScene gs)
                    {
                        ProfileManager.CurrentProfile.DefaultScale = gs.Camera.Zoom; // pinch zoom (normally saved on scene unload)
                    }

                    ProfileManager.CurrentProfile.Save(UO.World, ProfileManager.ProfilePath);
                    UO.World.Macros?.Save();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"save on suspend (profile): {ex.Message}");
            }

            try
            {
                Settings.GlobalSettings?.Save();
                TouchInput.Layouts?.Save();
                MobileMacroRunner.Macros?.Save();
            }
            catch (Exception ex)
            {
                Log.Error($"save on suspend (settings): {ex.Message}");
            }

            try
            {
                GumpScale.Save();
            }
            catch (Exception ex)
            {
                Log.Error($"save on suspend (window zoom): {ex.Message}");
            }
        }

        protected override void Update(GameTime gameTime)
        {
            if (_inBackground)
            {
                SuppressDraw();
            }

            if (Profiler.InContext(Profiler.ProfilerContext.OUT_OF_CONTEXT))
            {
                Profiler.ExitContext(Profiler.ProfilerContext.OUT_OF_CONTEXT);
            }

            Time.Ticks = (uint)gameTime.TotalGameTime.TotalMilliseconds;
            Time.Delta = (float)gameTime.ElapsedGameTime.TotalSeconds;

            if (PhoneFit && !_inBackground)
            {
                FollowPhoneRotation();
            }

            Mouse.Update();

            if (_download != null)
            {
                _download.Update();
                DevScript.Update();

                // Not while backgrounded: loading creates textures, and iOS forbids GPU work there.
                if (_download.Done && !_inBackground)
                {
                    bool downloaded = _download.UoPathOverride == null;

                    if (!downloaded)
                    {
                        Settings.GlobalSettings.UltimaOnlineDirectory = _download.UoPathOverride;
                    }

                    _download = null;

                    try
                    {
                        FinishLoadContent();
                    }
                    catch
                    {
                        // The files are on record but the game can't load them: next launch,
                        // check every file again (only mismatches download).
                        if (downloaded)
                        {
                            GameFiles.ForgetInstalled(Settings.GlobalSettings.UltimaOnlineDirectory);
                        }

                        throw;
                    }
                }

                base.Update(gameTime);

                return;
            }

            var data = NetClient.Socket.CollectAvailableData();
            var packetsCount = PacketHandlers.Handler.ParsePackets(NetClient.Socket, UO.World, data);

            NetClient.Socket.Statistics.TotalPacketsReceived += (uint)packetsCount;
            NetClient.Socket.Flush();

            Plugin.Tick();

            if (Scene != null && Scene.IsLoaded && !Scene.IsDestroyed)
            {
                Profiler.EnterContext(Profiler.ProfilerContext.UPDATE_WORLD);
                Scene.Update();
                Profiler.ExitContext(Profiler.ProfilerContext.UPDATE_WORLD);
            }

            UIManager.Update();
            TouchInput.Update();
            DevScript.Update();

            _totalElapsed += gameTime.ElapsedGameTime.TotalMilliseconds;
            _currentFpsTime += gameTime.ElapsedGameTime.TotalMilliseconds;

            if (_currentFpsTime >= 1000)
            {
                CUOEnviroment.CurrentRefreshRate = _totalFrames;

                _totalFrames = 0;
                _currentFpsTime = 0;
            }

            double x = _intervalFixedUpdate[
                !IsActive
                && ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.ReduceFPSWhenInactive
                    ? 1
                    : 0
            ];
            _suppressedDraw = false;

            if (_totalElapsed > x)
            {
                _totalElapsed %= x;
            }
            else
            {
                _suppressedDraw = true;
                SuppressDraw();

                if (!gameTime.IsRunningSlowly)
                {
                    Thread.Sleep(1);
                }
            }

            // Only on ticks that draw a frame (not the limiter's idle ticks), and never while iOS has us
            // in the background (GPU uploads).
            if (!_inBackground && !_suppressedDraw)
            {
                GumpWarmup.Update(UO.World);
            }

            UO.GameCursor?.Update();
            Audio?.Update();


            for (var i = _queuedActions.Count - 1; i >= 0; i--)
            {
                (var time, var fn) = _queuedActions[i];

                if (Time.Ticks > time)
                {
                    fn();
                    _queuedActions.RemoveAt(i);
                    break;
                }
            }

             base.Update(gameTime);
        }

        protected override void Draw(GameTime gameTime)
        {
            if (_download != null)
            {
                DrawCount++;
                GraphicsDevice.SetRenderTarget(null);
                GraphicsDevice.Clear(Color.Black);
                _download.Draw(_uoSpriteBatch, GraphicManager.PreferredBackBufferWidth, GraphicManager.PreferredBackBufferHeight, DpiScale);

                if (_pendingScreenshot != null)
                {
                    SaveScreenshot(_pendingScreenshot);
                    _pendingScreenshot = null;
                }

                base.Draw(gameTime);

                return;
            }

            _renderTargets.EnsureSizes(
                GraphicsDevice,
                new Rectangle(0, 0, GraphicManager.PreferredBackBufferWidth, GraphicManager.PreferredBackBufferHeight),
                Scene.Camera.Bounds,
                DpiScale
            );

            Profiler.EndFrame();
            Profiler.BeginFrame();

            if (Profiler.InContext(Profiler.ProfilerContext.OUT_OF_CONTEXT))
            {
                Profiler.ExitContext(Profiler.ProfilerContext.OUT_OF_CONTEXT);
            }

            Profiler.EnterContext(Profiler.ProfilerContext.RENDER_FRAME);

            _totalFrames++;
            DrawCount++;

            GraphicsDevice.Clear(Color.Black);

            if (Scene != null && Scene.IsLoaded && !Scene.IsDestroyed)
            {
                Scene.Draw(_uoSpriteBatch, _renderTargets);
            }

            _uoSpriteBatch.GraphicsDevice.SetRenderTarget(_renderTargets.UiRenderTarget);
            GraphicsDevice.Clear(Color.Transparent);

            if ((UO.World?.InGame ?? false) && SelectedObject.Object is TextObject t)
            {
                if (t.IsTextGump)
                {
                    t.ToTopD();
                }
                else
                {
                    UO.World.WorldTextManager?.MoveToTop(t);
                }
            }

            SelectedObject.HealthbarObject = null;
            SelectedObject.SelectedContainer = null;

            _uoSpriteBatch.Begin();
            if (Scene != null && Scene.IsLoaded && !Scene.IsDestroyed)
            {
                Scene.DrawUI(_uoSpriteBatch);
            }
            _uoSpriteBatch.End();

            UIManager.Draw(_uoSpriteBatch);

            _uoSpriteBatch.Begin();
            UO.GameCursor?.Draw(_uoSpriteBatch);
            _uoSpriteBatch.End();

            _uoSpriteBatch.GraphicsDevice.SetRenderTarget(null);

            _renderTargets.Draw(_uoSpriteBatch);

            Profiler.ExitContext(Profiler.ProfilerContext.RENDER_FRAME);
            Profiler.EnterContext(Profiler.ProfilerContext.OUT_OF_CONTEXT);

            Plugin.ProcessDrawCmdList(GraphicsDevice);

            if (_pendingScreenshot != null)
            {
                SaveScreenshot(_pendingScreenshot);
                _pendingScreenshot = null;
            }

            base.Draw(gameTime);
        }

        private string _pendingScreenshot;

        internal bool ScreenshotPending => _pendingScreenshot != null;

        /// <summary>Frames actually drawn since start (Update runs many times per drawn frame).</summary>
        internal ulong DrawCount { get; private set; }

        /// <summary>Captures the next fully drawn frame to <paramref name="path"/> (dev automation).</summary>
        internal void RequestScreenshot(string path) => _pendingScreenshot = path;

        private float _screenScale = Settings.GlobalSettings.ScreenScale;
        public float ScreenScale {
            get => _screenScale;
            set {
                if (value != _screenScale) {
                    _screenScale = value;
                    UO.GameCursor?.CreateGraphic(DpiScale);
                }
            }
        }

        public float DpiScale
        {
            get => SDL_GetWindowDisplayScale(Window.Handle) * ScreenScale;
        }

        public int ScaleWithDpi(int value, float previousDpi = 1)
        {
            return (int)Math.Round((value / previousDpi) * DpiScale);
        }

        protected override bool BeginDraw()
        {
            return !_suppressedDraw && base.BeginDraw();
        }

        private void WindowOnClientSizeChanged(object sender, EventArgs e)
        {
            int width = Window.ClientBounds.Width;
            int height = Window.ClientBounds.Height;

            WindowOnClientSizeChanged(width, height);
        }

        private void WindowOnClientSizeChanged(int width, int height)
        {
            // Phone: no resize event reaches the game on iOS; FollowPhoneRotation handles every size
            // change. Returning here makes the desktop UOM_PHONE_FIT test take that same path, and
            // keeps the display-scale handler from refitting with rescaled sizes.
            if (PhoneFit)
            {
                return;
            }

            if (!IsWindowMaximized() && Window.AllowUserResizing)
            {
                if (ProfileManager.CurrentProfile != null)
                    ProfileManager.CurrentProfile.WindowClientBounds = new Point(width, height);
            }

            SetWindowSize(width, height);

            WorldViewportGump viewport = UIManager.GetGump<WorldViewportGump>();

            if (viewport != null && ProfileManager.CurrentProfile != null && ProfileManager.CurrentProfile.GameWindowFullSize)
            {
                viewport.ResizeGameWindow(new Point(width, height));
                viewport.X = -5;
                viewport.Y = -5;
            }
        }

        // SDL3's SDL_TOUCH_MOUSEID ((SDL_MouseID)-1): mouse events SDL synthesizes from fingers.
        private const uint TOUCH_MOUSE_ID = uint.MaxValue;

        private bool HandleSdlEvent(IntPtr userData, SDL_Event* sdlEvent)
        {
            // Don't pass SDL events to the plugin host before the plugins are initialized
            // or the garbage collector can get screwed up
            if (_pluginsInitialized && Plugin.ProcessWndProc(sdlEvent) != 0)
            {
                if ((SDL_EventType)sdlEvent->type == SDL_EventType.SDL_EVENT_MOUSE_MOTION)
                {
                    if (UO.GameCursor != null)
                    {
                        UO.GameCursor.AllowDrawSDLCursor = false;
                    }
                }

                return true;
            }

            switch ((SDL_EventType)sdlEvent->type)
            {
                // iOS: no GPU work while backgrounded (the OS may kill the app for it). These
                // arrive synchronously here, inside the UIKit callback, as SDL recommends.
                case SDL_EventType.SDL_EVENT_WILL_ENTER_BACKGROUND:
                    _inBackground = true;

                    break;

                // iOS kills suspended apps without UnloadContent/GameScene.Unload running, so this
                // is the last reliable moment to save the profile, settings and touch layouts.
                case SDL_EventType.SDL_EVENT_DID_ENTER_BACKGROUND:
                case SDL_EventType.SDL_EVENT_TERMINATING:
                    _inBackground = true;
                    SaveForSuspend();

                    break;

                case SDL_EventType.SDL_EVENT_WILL_ENTER_FOREGROUND:
                case SDL_EventType.SDL_EVENT_DID_ENTER_FOREGROUND:
                    _inBackground = false;

                    break;

                case SDL_EventType.SDL_EVENT_AUDIO_DEVICE_ADDED:
                    Console.WriteLine("AUDIO ADDED: {0}", sdlEvent->adevice.which);

                    break;

                case SDL_EventType.SDL_EVENT_AUDIO_DEVICE_REMOVED:
                    Console.WriteLine("AUDIO REMOVED: {0}", sdlEvent->adevice.which);

                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_MOUSE_ENTER:
                    Mouse.MouseInWindow = true;
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_MOUSE_LEAVE:
                    Mouse.MouseInWindow = false;
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED:
                    Plugin.OnFocusGained();
                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
                    Plugin.OnFocusLost();
                    break;

                // While the download screen runs there is no scene or world yet: it only takes taps.
                case SDL_EventType.SDL_EVENT_KEY_DOWN when _download != null:
                case SDL_EventType.SDL_EVENT_KEY_UP when _download != null:
                case SDL_EventType.SDL_EVENT_TEXT_INPUT when _download != null:
                case SDL_EventType.SDL_EVENT_MOUSE_MOTION when _download != null:
                case SDL_EventType.SDL_EVENT_MOUSE_WHEEL when _download != null:
                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN when _download != null:
                    break;

                case SDL_EventType.SDL_EVENT_KEY_DOWN:

                    Keyboard.OnKeyDown(sdlEvent->key);

                    if (
                        Plugin.ProcessHotkeys(
                            (int)sdlEvent->key.key,
                            (int)sdlEvent->key.mod,
                            true
                        )
                    )
                    {
                        _ignoreNextTextInput = false;

                        UIManager.KeyboardFocusControl?.InvokeKeyDown(
                            (SDL_Keycode)sdlEvent->key.key,
                            sdlEvent->key.mod
                        );

                        Scene.OnKeyDown(sdlEvent->key);
                    }
                    else
                    {
                        _ignoreNextTextInput = true;
                    }

                    break;

                case SDL_EventType.SDL_EVENT_KEY_UP:

                    Keyboard.OnKeyUp(sdlEvent->key);
                    UIManager.KeyboardFocusControl?.InvokeKeyUp(
                        (SDL_Keycode)sdlEvent->key.key,
                        sdlEvent->key.mod
                    );
                    Scene.OnKeyUp(sdlEvent->key);
                    Plugin.ProcessHotkeys(0, 0, false);

                    if ((SDL_Keycode)sdlEvent->key.key == SDL_Keycode.SDLK_PRINTSCREEN)
                    {
                        TakeScreenshot();
                    }

                    break;

                case SDL_EventType.SDL_EVENT_TEXT_INPUT:

                    if (_ignoreNextTextInput)
                    {
                        break;
                    }

                    // Fix for linux OS: https://github.com/andreakarasho/ClassicUO/pull/1263
                    // Fix 2: SDL owns this behaviour. Cheating is not a real solution.
                    /*if (!Utility.Platforms.PlatformHelper.IsWindows)
                    {
                        if (Keyboard.Alt || Keyboard.Ctrl)
                        {
                            break;
                        }
                    }*/

                    /* We get to do strlen ourselves! */
                    byte* ptr = sdlEvent->text.text;
                    while (*ptr != 0)
                    {
                        ptr++;
                    }

                    string s = System.Text.Encoding.UTF8.GetString(
                        sdlEvent->text.text,
                        (int)(ptr - sdlEvent->text.text)
                    );

                    if (!string.IsNullOrEmpty(s))
                    {
                        UIManager.KeyboardFocusControl?.InvokeTextInput(s);
                        Scene.OnTextInput(s);
                    }

                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_MOTION:

                    if (sdlEvent->motion.which == TOUCH_MOUSE_ID)
                    {
                        break; // synthesized from a finger; Touch/TouchInput handles fingers itself
                    }

                    if (UO.GameCursor != null && !UO.GameCursor.AllowDrawSDLCursor)
                    {
                        UO.GameCursor.AllowDrawSDLCursor = true;
                        UO.GameCursor.Graphic = 0xFFFF;
                    }

                    Mouse.TouchPosition = null;
                    Mouse.Update();

                    if (TouchInput.OwnsFinger(TouchInput.MouseFingerId))
                    {
                        TouchInput.OnMove(TouchInput.MouseFingerId, Mouse.Position);

                        break;
                    }

                    DispatchMouseMotion();

                    break;

                case SDL_EventType.SDL_EVENT_FINGER_DOWN when _download != null:
                case SDL_EventType.SDL_EVENT_FINGER_MOTION when _download != null:
                case SDL_EventType.SDL_EVENT_FINGER_CANCELED when _download != null:
                    break;

                case SDL_EventType.SDL_EVENT_FINGER_UP when _download != null:
                    _download.OnTap(TouchInput.ToUi(sdlEvent->tfinger.x, sdlEvent->tfinger.y));

                    break;

                case SDL_EventType.SDL_EVENT_FINGER_DOWN:
                    TouchInput.OnDown((long)sdlEvent->tfinger.fingerID, TouchInput.ToUi(sdlEvent->tfinger.x, sdlEvent->tfinger.y));

                    break;

                case SDL_EventType.SDL_EVENT_FINGER_MOTION:
                    TouchInput.OnMove((long)sdlEvent->tfinger.fingerID, TouchInput.ToUi(sdlEvent->tfinger.x, sdlEvent->tfinger.y));

                    break;

                case SDL_EventType.SDL_EVENT_FINGER_UP:
                case SDL_EventType.SDL_EVENT_FINGER_CANCELED:
                    TouchInput.OnUp((long)sdlEvent->tfinger.fingerID, TouchInput.ToUi(sdlEvent->tfinger.x, sdlEvent->tfinger.y));

                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                    Mouse.Update();
                    bool isScrolledUp = sdlEvent->wheel.y > 0;

                    Plugin.ProcessMouse(0, (int)sdlEvent->wheel.y);

                    if (!Scene.OnMouseWheel(isScrolledUp))
                    {
                        UIManager.OnMouseWheel(isScrolledUp);
                    }

                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
                    if (sdlEvent->button.which == TOUCH_MOUSE_ID)
                    {
                        break;
                    }

                    Mouse.TouchPosition = null;
                    Mouse.Update();

                    // With the touch layer on, a left click on the HUD (joystick/buttons) goes to it.
                    if ((MouseButtonType)sdlEvent->button.button == MouseButtonType.Left
                        && TouchInput.OnDown(TouchInput.MouseFingerId, Mouse.Position, isMouse: true))
                    {
                        break;
                    }

                    DispatchMouseDown((MouseButtonType)sdlEvent->button.button);

                    break;

                case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                    if (sdlEvent->button.which == TOUCH_MOUSE_ID)
                    {
                        break;
                    }

                    if (_download != null) // desktop test of the download screen
                    {
                        Mouse.Update();
                        _download.OnTap(Mouse.Position);

                        break;
                    }

                    if ((MouseButtonType)sdlEvent->button.button == MouseButtonType.Left
                        && TouchInput.OwnsFinger(TouchInput.MouseFingerId))
                    {
                        Mouse.Update();
                        TouchInput.OnUp(TouchInput.MouseFingerId, Mouse.Position);

                        break;
                    }

                    DispatchMouseUp((MouseButtonType)sdlEvent->button.button);

                    break;

                case SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_SCALE_CHANGED:
                case SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_CHANGED:
                {
                    // when starting scaled, SDL will raise the scale changed event before the window has properly loaded and the previous scale set
                    if (_displayScale != 0 && _displayScale != DpiScale)
                    {
                        // The effective DPI scale has changed. SDL handles the window content automatically
                        // but we need to make sure to resize the window properly
                        // This is especially important when the window size is restricted, for example
                        // in the LoginScene
                        WindowOnClientSizeChanged(
                            Client.Game.ScaleWithDpi(Window.ClientBounds.Width, previousDpi: _displayScale),
                            Client.Game.ScaleWithDpi(Window.ClientBounds.Height, previousDpi: _displayScale)
                        );

                        SDL_GetWindowMinimumSize(Client.Game.Window.Handle, out int previousMinWidth, out int previousMinHeight);

                        SDL_SetWindowMinimumSize(
                            Client.Game.Window.Handle,
                            Client.Game.ScaleWithDpi(previousMinWidth, previousDpi: _displayScale),
                            Client.Game.ScaleWithDpi(previousMinHeight, previousDpi: _displayScale)
                        );

                        _displayScale = DpiScale;
                    }
                    break;
                }
            }

            return true;
        }

        protected override void OnExiting(object sender, EventArgs args)
        {
            Scene?.Dispose();

            base.OnExiting(sender, args);
        }

        // Mouse button handling, pulled out of HandleSdlEvent so the touch layer
        // (Mobile/TouchInput) can drive the exact same Scene/UIManager path.
        // The values in MouseButtonType are chosen to exactly match the SDL values.
        internal void DispatchMouseMotion()
        {
            Mouse.Update();

            if (Mouse.IsDragging)
            {
                if (!Scene.OnMouseDragging())
                {
                    UIManager.OnMouseDragging();
                }
            }
        }

        internal void DispatchMouseDown(MouseButtonType buttonType)
        {
            uint lastClickTime = 0;

            switch (buttonType)
            {
                case MouseButtonType.Left:
                    lastClickTime = Mouse.LastLeftButtonClickTime;

                    break;

                case MouseButtonType.Middle:
                    lastClickTime = Mouse.LastMidButtonClickTime;

                    break;

                case MouseButtonType.Right:
                    lastClickTime = Mouse.LastRightButtonClickTime;

                    break;

                case MouseButtonType.XButton1:
                case MouseButtonType.XButton2:
                    break;

                default:
                    Log.Warn($"No mouse button handled: {buttonType}");

                    break;
            }

            Mouse.ButtonPress(buttonType);
            Mouse.Update();

            uint ticks = Time.Ticks;

            if (lastClickTime + Mouse.MOUSE_DELAY_DOUBLE_CLICK >= ticks)
            {
                lastClickTime = 0;

                bool res =
                    Scene.OnMouseDoubleClick(buttonType)
                    || UIManager.OnMouseDoubleClick(buttonType);

                if (!res)
                {
                    if (!Scene.OnMouseDown(buttonType))
                    {
                        UIManager.OnMouseButtonDown(buttonType);
                    }
                }
                else
                {
                    lastClickTime = 0xFFFF_FFFF;
                }
            }
            else
            {
                if (
                    buttonType != MouseButtonType.Left
                    && buttonType != MouseButtonType.Right
                )
                {
                    Plugin.ProcessMouse((byte)buttonType, 0);
                }

                if (!Scene.OnMouseDown(buttonType))
                {
                    UIManager.OnMouseButtonDown(buttonType);
                }

                lastClickTime = Mouse.CancelDoubleClick ? 0 : ticks;
            }

            switch (buttonType)
            {
                case MouseButtonType.Left:
                    Mouse.LastLeftButtonClickTime = lastClickTime;

                    break;

                case MouseButtonType.Middle:
                    Mouse.LastMidButtonClickTime = lastClickTime;

                    break;

                case MouseButtonType.Right:
                    Mouse.LastRightButtonClickTime = lastClickTime;

                    break;
            }
        }

        internal void DispatchMouseUp(MouseButtonType buttonType)
        {
            uint lastClickTime = 0;

            switch (buttonType)
            {
                case MouseButtonType.Left:
                    lastClickTime = Mouse.LastLeftButtonClickTime;

                    break;

                case MouseButtonType.Middle:
                    lastClickTime = Mouse.LastMidButtonClickTime;

                    break;

                case MouseButtonType.Right:
                    lastClickTime = Mouse.LastRightButtonClickTime;

                    break;

                default:
                    Log.Warn($"No mouse button handled: {buttonType}");

                    break;
            }

            if (lastClickTime != 0xFFFF_FFFF)
            {
                if (
                    !Scene.OnMouseUp(buttonType)
                    || UIManager.LastControlMouseDown(buttonType) != null
                )
                {
                    UIManager.OnMouseButtonUp(buttonType);
                }
            }

            Mouse.ButtonRelease(buttonType);
            Mouse.Update();
        }

        /// <summary>Writes the current back buffer to <paramref name="path"/> as a PNG.</summary>
        internal void SaveScreenshot(string path)
        {
            Color[] colors = new Color[
                GraphicManager.PreferredBackBufferWidth * GraphicManager.PreferredBackBufferHeight
            ];

            GraphicsDevice.GetBackBufferData(colors);

            using (
                Texture2D texture = new Texture2D(
                    GraphicsDevice,
                    GraphicManager.PreferredBackBufferWidth,
                    GraphicManager.PreferredBackBufferHeight,
                    false,
                    SurfaceFormat.Color
                )
            )
            using (FileStream fileStream = File.Create(path))
            {
                texture.SetData(colors);
                texture.SaveAsPng(fileStream, texture.Width, texture.Height);
            }
        }

        private void TakeScreenshot()
        {
            string screenshotsFolder = FileSystemHelper.CreateFolderIfNotExists(
                CUOEnviroment.ExecutablePath,
                "Data",
                "Client",
                "Screenshots"
            );

            string path = Path.Combine(
                screenshotsFolder,
                $"screenshot_{DateTime.Now:yyyy-MM-dd_hh-mm-ss}.png"
            );

            SaveScreenshot(path);

            {
                string message = string.Format(ResGeneral.ScreenshotStoredIn0, path);

                if (
                    ProfileManager.CurrentProfile == null
                    || ProfileManager.CurrentProfile.HideScreenshotStoredInMessage
                )
                {
                    Log.Info(message);
                }
                else
                {
                    GameActions.Print(UO.World, message, 0x44, MessageType.System);
                }
            }
        }
    }
}
