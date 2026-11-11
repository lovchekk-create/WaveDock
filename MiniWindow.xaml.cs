using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace WaveDock
{
    /// <summary>Маленький волновой квадрат поверх панели задач.</summary>
    public partial class MiniWindow : Window
    {
        readonly MediaService _media;
        readonly PlayerWindow _player;
        readonly ToastWindow _toast = new ToastWindow();
        string _lastTrackKey;
        string _hotkeySig;
        Settings _settings => Settings.Current;
        readonly DispatcherTimer _hoverTimer;
        readonly DispatcherTimer _timer;
        IntPtr _hwnd;

        bool _pressed, _dragging, _openAtDown;
        Point _downScreen;
        double _downOffset;

        public MiniWindow(MediaService media)
        {
            InitializeComponent();
            _media = media;
            _player = new PlayerWindow(media);
            _media.Changed += OnMediaChanged;
            Settings.Changed += ApplySettings;

            SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(_hwnd, noActivate: true);
                HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
                RegisterHotkeys();
                Reposition();
            };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => Maintain();
            _timer.Start();

            _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
            _hoverTimer.Tick += (s, e) =>
            {
                _hoverTimer.Stop();
                if (_settings.OpenOnHover && IsMouseOver && !_pressed && !_player.IsOpen)
                    OpenPlayer(false);
            };

            MouseEnter += (s, e) => { AnimateScale(1.1); if (_settings.OpenOnHover) _hoverTimer.Start(); };
            MouseLeave += (s, e) => { AnimateScale(1.0); _hoverTimer.Stop(); };
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Middle && _settings.MiddlePause) { _media.TogglePlayPause(); Wave.Splash(); }
            };
            MouseWheel += (s, e) =>
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) { ChangeVolume(e.Delta > 0 ? 0.02 : -0.02); return; }
                if (!_settings.WheelTracks) return;
                if (e.Delta < 0) _media.Next(); else _media.Previous();
                Wave.Splash();
            };

            _toast.Clicked += () => OpenPlayer(false);
            Closed += (s, e) => UnregisterHotkeys();

            BuildMenu();
            ApplySettings();
            OnMediaChanged();
        }

        void ApplySettings()
        {
            var st = _settings;
            double size = Math.Round(Settings.Clamp(st.Size, 22, 42));
            if (Math.Abs(Width - (size + 12)) > 0.1) { Width = size + 12; Height = size + 12; }

            Wave.Radius = Settings.Clamp(st.CornerRadius, 0, size / 2);
            Wave.BgOpacity = Settings.Clamp(st.BgOpacity, 0, 1);
            Wave.ShowBorder = st.Border;
            Wave.SpeedMul = Settings.Clamp(st.WaveSpeed, 0.1, 4);
            Wave.AmpMul = Settings.Clamp(st.WaveHeight, 0.1, 3);
            Wave.Layers = (int)Math.Round(Settings.Clamp(st.WaveLayers, 1, 3));
            Wave.PlayLevel = Settings.Clamp(st.FillLevel, 0.1, 0.95);
            Wave.Reactive = st.AudioReactive;
            Wave.Sensitivity = Settings.Clamp(st.Sensitivity, 0.2, 3);
            Wave.ApplyState();
            RegisterHotkeys();

            double glow = Settings.Clamp(st.Glow, 0, 1);
            MiniGlow.Opacity = glow;
            MiniGlow.BlurRadius = 2 + 16 * glow;

            ApplyColors(false);
            Reposition();
        }

        void ApplyColors(bool animate)
        {
            _settings.GetAccent(_media.Current, out var c1, out var c2);
            Wave.SetColors(c1, c2);
            if (animate)
                MiniGlow.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(c1, TimeSpan.FromMilliseconds(600)));
            else
            {
                MiniGlow.BeginAnimation(DropShadowEffect.ColorProperty, null);
                MiniGlow.Color = c1;
            }
        }

        void BuildMenu()
        {
            var menu = new ContextMenu();
            var auto = new MenuItem { Header = "Запускать вместе с Windows", IsCheckable = true };
            menu.Opened += (s, e) => auto.IsChecked = Autostart.IsEnabled;
            auto.Click += (s, e) => Autostart.Set(auto.IsChecked);

            var reset = new MenuItem { Header = "Вернуть на место" };
            reset.Click += (s, e) => { _settings.OffsetX = 8; _settings.OffsetY = 0; Reposition(); };

            var settings = new MenuItem { Header = "Настройки…" };
            settings.Click += (s, e) => SettingsWindow.ShowSingle(_media);

            var exit = new MenuItem { Header = "Выход" };
            exit.Click += (s, e) => { App.Exiting = true; Application.Current.Shutdown(); };

            menu.Items.Add(settings);
            menu.Items.Add(auto);
            menu.Items.Add(reset);
            menu.Items.Add(new Separator());
            menu.Items.Add(exit);
            ContextMenu = menu;
        }

        void OnMediaChanged()
        {
            var t = _media.Current;
            Wave.SetState(t.HasSession, t.Playing);
            ApplyColors(true);

            // плашка «сейчас играет» при смене трека
            if (t.HasSession && !string.IsNullOrWhiteSpace(t.Title))
            {
                _settings.GetAccent(t, out var c1, out var c2);
                if (t.Key != _lastTrackKey)
                {
                    bool first = _lastTrackKey == null;
                    _lastTrackKey = t.Key;
                    if (!first && _settings.TrackToast && !_player.IsOpen && IsVisible)
                        _toast.ShowTrack(t, c1, c2, AnchorRect, Settings.Clamp(_settings.ToastSeconds, 1.5, 10));
                }
                else _toast.RefreshTrack(t, c1, c2);
            }
            ToolTip = t.HasSession && !string.IsNullOrEmpty(t.Title)
                ? (string.IsNullOrEmpty(t.Artist) ? t.Title : t.Artist + " — " + t.Title)
                : "WaveDock — ничего не играет";
        }


        Rect AnchorRect => new Rect(Left, Top, Width, Height);

        void OpenPlayer(bool withLyrics)
        {
            _toast.HideAnimated();
            _player.OpenFrom(AnchorRect, _hwnd, withLyrics);
        }

        void ChangeVolume(double delta)
        {
            var a = AudioService.Instance;
            if (a == null) return;
            a.Volume = a.Volume + delta;
            _settings.GetAccent(_media.Current, out var c1, out var c2);
            if (!_player.IsOpen) _toast.ShowVolume(a.Volume, a.Muted, c1, c2, AnchorRect);
            Wave.Splash();
        }

        // ---------- глобальные горячие клавиши ----------

        const int HK_PLAY = 1, HK_NEXT = 2, HK_PREV = 3, HK_VOLUP = 4, HK_VOLDOWN = 5, HK_LYRICS = 6;

        void RegisterHotkeys()
        {
            if (_hwnd == IntPtr.Zero) return;
            string sig = _settings.Hotkeys + _settings.HotkeyMod;
            if (sig == _hotkeySig) return;
            UnregisterHotkeys();
            _hotkeySig = sig;
            if (!_settings.Hotkeys) return;

            uint mod = _settings.HotkeyMod == "CtrlShift" ? Native.MOD_CONTROL | Native.MOD_SHIFT
                     : _settings.HotkeyMod == "AltShift" ? Native.MOD_ALT | Native.MOD_SHIFT
                     : Native.MOD_CONTROL | Native.MOD_ALT;

            var failed = new System.Collections.Generic.List<string>();
            void Reg(int id, uint vk, bool repeat, string name)
            {
                if (!Native.RegisterHotKey(_hwnd, id, mod | (repeat ? 0 : Native.MOD_NOREPEAT), vk)) failed.Add(name);
            }
            if (!Native.RegisterHotKey(_hwnd, HK_PLAY, mod | Native.MOD_NOREPEAT, 0x20))      // Пробел занят другой программой —
                Reg(HK_PLAY, 0x50, false, "P");                                                  // тогда пауза на P
            Reg(HK_NEXT, 0x27, false, "→");
            Reg(HK_PREV, 0x25, false, "←");
            Reg(HK_VOLUP, 0x26, true, "↑");
            Reg(HK_VOLDOWN, 0x28, true, "↓");
            Reg(HK_LYRICS, 0x4C, false, "L");
            if (failed.Count > 0)
                App.Log(new Exception("Hotkeys already taken by another program: " + string.Join(", ", failed)));
        }

        void UnregisterHotkeys()
        {
            if (_hwnd == IntPtr.Zero) return;
            for (int id = HK_PLAY; id <= HK_LYRICS; id++) Native.UnregisterHotKey(_hwnd, id);
            _hotkeySig = null;
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != Native.WM_HOTKEY) return IntPtr.Zero;
            handled = true;
            switch (wParam.ToInt32())
            {
                case HK_PLAY: _media.TogglePlayPause(); Wave.Splash(); break;
                case HK_NEXT: _media.Next(); Wave.Splash(); break;
                case HK_PREV: _media.Previous(); Wave.Splash(); break;
                case HK_VOLUP: ChangeVolume(0.04); break;
                case HK_VOLDOWN: ChangeVolume(-0.04); break;
                case HK_LYRICS:
                    if (_player.IsOpen) _player.ToggleLyrics();
                    else OpenPlayer(true);
                    break;
            }
            return IntPtr.Zero;
        }

        // ---------- позиция на панели задач ----------

        double DpiScale
        {
            get
            {
                var src = PresentationSource.FromVisual(this);
                return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            }
        }

        void Maintain()
        {
            if (_hwnd == IntPtr.Zero) return;

            bool hide = _settings.HideFullscreen && Native.IsFullscreenAppRunning();
            var want = hide ? Visibility.Hidden : Visibility.Visible;
            if (Visibility != want)
            {
                Visibility = want;
                App.Log(hide ? "hidden: fullscreen app detected" : "visible again");
            }
            if (hide) return;

            if (!_dragging) Reposition();
            Native.KeepTopmost(_hwnd); // панель задач любит подниматься поверх
        }

        void Reposition()
        {
            if (!Native.TryGetTaskbarRect(out var tb)) return;
            double s = DpiScale;
            double tbLeft = tb.Left / s, tbTop = tb.Top / s;
            double tbW = (tb.Right - tb.Left) / s, tbH = (tb.Bottom - tb.Top) / s;

            double maxOffset = Math.Max(0, tbW - Width);
            double off = Math.Max(0, Math.Min(_settings.OffsetX, maxOffset));
            double left = Math.Round(tbLeft + off);
            double top = Math.Round(tbTop + (tbH - Height) / 2 + Settings.Clamp(_settings.OffsetY, -20, 20));

            if (Math.Abs(Left - left) > 0.5) Left = left;
            if (Math.Abs(Top - top) > 0.5) Top = top;
        }

        // ---------- мышь: клик / перетаскивание ----------

        void OnDown(object sender, MouseButtonEventArgs e)
        {
            _pressed = true;
            // плеер мог закрыться прямо в момент нажатия (потерял фокус) — запоминаем, что он был открыт
            _openAtDown = _player.IsOpen || (DateTime.Now - _player.LastClosed).TotalMilliseconds < 450;
            _dragging = false;
            _downScreen = PointToScreen(e.GetPosition(this));
            _downOffset = _settings.OffsetX;
            CaptureMouse();
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            if (!_pressed) return;
            var p = PointToScreen(e.GetPosition(this));
            double dx = (p.X - _downScreen.X) / DpiScale;
            if (!_dragging && Math.Abs(dx) > 6) _dragging = true;
            if (_dragging)
            {
                _settings.OffsetX = Math.Max(0, _downOffset + dx);
                Reposition();
            }
        }

        void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_pressed) return;
            _pressed = false;
            ReleaseMouseCapture();

            if (_dragging)
            {
                _dragging = false;
                _settings.Save();
                return;
            }

            Wave.Splash();
            Bounce();
            if (_openAtDown)
            {
                if (_player.IsOpen) _player.CloseAnimated(); // второй клик — закрыть
            }
            else OpenPlayer(false);
        }

        // ---------- анимации ----------

        void AnimateScale(double to)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(260))
            { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } };
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        void Bounce()
        {
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0.82, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(1.1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(420)),
                new ElasticEase { Oscillations = 2, Springiness = 4, EasingMode = EasingMode.EaseOut }));
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }
}
