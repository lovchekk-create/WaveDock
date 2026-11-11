using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WaveDock
{
    /// <summary>Всплывающий плеер над виджетом.</summary>
    public partial class PlayerWindow : Window
    {
        const double CardMargin = 26;

        readonly MediaService _media;
        readonly DispatcherTimer _tick;
        IntPtr _hwnd, _miniHwnd;
        string _lastKey;
        bool _wasDown;
        int _tickCount;

        public bool IsOpen { get; private set; }
        public DateTime LastClosed { get; private set; } = DateTime.MinValue;

        public PlayerWindow(MediaService media)
        {
            InitializeComponent();
            _media = media;
            _media.Changed += () => UpdateUi(false);
            Settings.Changed += () => { ApplySettings(); UpdateUi(false); };

            GearBtn.MouseEnter += (s, e) => GearRotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(90, TimeSpan.FromMilliseconds(400)) { EasingFunction = new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } });
            GearBtn.MouseLeave += (s, e) => GearRotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

            TitleText.RenderTransform = new TranslateTransform();
            ArtistText.RenderTransform = new TranslateTransform();
            ProgressArea.RenderTransform = new TranslateTransform();
            ControlsRow.RenderTransform = new TranslateTransform();
            AppText.RenderTransform = new TranslateTransform();

            Card.SizeChanged += (s, e) =>
                Card.Clip = new RectangleGeometry(new Rect(e.NewSize), 26, 26);
            LyrBg.SizeChanged += (s, e) =>
                LyrBg.Clip = new RectangleGeometry(new Rect(e.NewSize), 24, 24);
            Track.SizeChanged += (s, e) => UpdateProgress();

            SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(_hwnd, noActivate: false);
            };

            Deactivated += (s, e) => CloseAnimated();
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) CloseAnimated();
                else if (e.Key == Key.Space) { _media.TogglePlayPause(); e.Handled = true; }
                else if (e.Key == Key.Right) _media.Next();
                else if (e.Key == Key.Left) _media.Previous();
            };

            _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _tick.Tick += OnTick;

            Opacity = 0;
            ApplySettings();
            UpdateUi(true);
        }

        void ApplySettings()
        {
            var st = Settings.Current;
            BgCover.Visibility = st.BlurCover ? Visibility.Visible : Visibility.Collapsed;
            LyrBgCover.Visibility = BgCover.Visibility;
            BgWave.SpeedMul = Settings.Clamp(st.WaveSpeed, 0.1, 4);
            BgWave.AmpMul = Settings.Clamp(st.WaveHeight, 0.1, 3);
            BgWave.Layers = (int)Math.Round(Settings.Clamp(st.WaveLayers, 1, 3));
            BgWave.PlayLevel = Settings.Clamp(st.FillLevel, 0.1, 0.95);
            BgWave.Reactive = st.AudioReactive;
            BgWave.Sensitivity = Settings.Clamp(st.Sensitivity, 0.2, 3);
            BgWave.ApplyState();
            if (IsOpen) Opacity = Settings.Clamp(st.PopupOpacity, 0.3, 1);
        }

        static double Speed => Settings.Clamp(Settings.Current.AnimSpeed, 0.25, 3);

        /// <summary>Пружинистая или плавная кривая — по настройке.</summary>
        static IEasingFunction Bouncy(double amplitude) => Settings.Current.SpringAnim
            ? new BackEase { Amplitude = amplitude, EasingMode = EasingMode.EaseOut }
            : new QuarticEase { EasingMode = EasingMode.EaseOut };

        static IEasingFunction Elastic() => Settings.Current.SpringAnim
            ? new ElasticEase { Oscillations = 1, Springiness = 5, EasingMode = EasingMode.EaseOut }
            : new QuarticEase { EasingMode = EasingMode.EaseOut };

        protected override void OnClosing(CancelEventArgs e)
        {
            if (App.Exiting) return;
            // окно живёт всё время работы программы — просто прячем
            e.Cancel = true;
            CloseAnimated();
        }

        // ---------- открытие / закрытие ----------

        public void OpenFrom(Rect anchor, IntPtr miniHwnd, bool withLyrics = false)
        {
            _miniHwnd = miniHwnd;
            var wa = SystemParameters.WorkArea;

            double left = anchor.Left - CardMargin - 4;
            left = Math.Max(wa.Left - CardMargin + 8, Math.Min(left, wa.Right - Width + CardMargin - 8));
            Left = Math.Round(left);
            Top = Math.Round(wa.Bottom - Height + CardMargin - 12);

            // анимация «вырастает» из виджета
            double cardW = Width - 2 * CardMargin;
            double ox = (anchor.Left + anchor.Width / 2 - (Left + CardMargin)) / cardW;
            Root.RenderTransformOrigin = new Point(Math.Max(0, Math.Min(1, ox)), 1.08);

            IsOpen = true;
            _wasDown = true; // не реагируем на клик, которым нас открыли
            UpdateUi(true);
            Opacity = Settings.Clamp(Settings.Current.PopupOpacity, 0.3, 1);
            Show();
            Activate();
            if (_hwnd != IntPtr.Zero) Native.KeepTopmost(_hwnd);
            _tick.Start();
            PlayOpenAnimation();

            HookAudio();
            UpdateVolumeUi();
            if (withLyrics) _lyricsOpen = true;
            if (_lyricsOpen) ShowLyricsCard(true);
        }

        public void CloseAnimated()
        {
            if (!IsOpen) return;
            IsOpen = false;
            LastClosed = DateTime.Now;
            _tick.Stop();

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            Anim(RootScale, ScaleTransform.ScaleXProperty, null, 0.86, 170, 0, ease);
            Anim(RootScale, ScaleTransform.ScaleYProperty, null, 0.86, 170, 0, ease);
            Anim(RootShift, TranslateTransform.YProperty, null, 16, 170, 0, ease);
            Anim(Root, OpacityProperty, null, 0, 160, 0, ease, (s, e) => { if (!IsOpen) Hide(); });
            if (LyricsCard.Visibility == Visibility.Visible) Anim(LyricsCard, OpacityProperty, null, 0, 140, 0, ease);
        }

        void PlayOpenAnimation()
        {
            var back = Bouncy(0.35);
            var cubic = new CubicEase { EasingMode = EasingMode.EaseOut };

            // карточка: вырастает, всплывает и проявляется
            Anim(RootScale, ScaleTransform.ScaleXProperty, 0.25, 1, 520, 0, back);
            Anim(RootScale, ScaleTransform.ScaleYProperty, 0.25, 1, 520, 0, back);
            Anim(RootShift, TranslateTransform.YProperty, 40, 0, 520, 0, cubic);
            Anim(Root, OpacityProperty, 0, 1, 200, 0, cubic);

            // обложка: прыжок с лёгким поворотом
            Stagger(CoverHost, 60);
            Anim(CoverScale, ScaleTransform.ScaleXProperty, 0.5, 1, 650, 60, Elastic());
            Anim(CoverScale, ScaleTransform.ScaleYProperty, 0.5, 1, 650, 60, Elastic());
            Anim(CoverRotate, RotateTransform.AngleProperty, -14, 0, 650, 60, back);

            // текст и кнопки — каскадом снизу
            Stagger(AppText, 120, 14);
            Stagger(TitleText, 160, 14);
            Stagger(ArtistText, 200, 14);
            Stagger(ProgressArea, 240, 14);
            Stagger(ControlsRow, 290, 18);

            // свечение «вспыхивает»
            Anim(Glow, DropShadowEffect.OpacityProperty, 0, 0.9, 300, 100, cubic,
                (s, e) => Anim(Glow, DropShadowEffect.OpacityProperty, null, 0.55, 700, 0, cubic));
            BgWave.Splash();
        }

        void PlayTrackChangeAnimation()
        {
            var back = Bouncy(0.5);
            Anim(CoverScale, ScaleTransform.ScaleXProperty, 0.82, 1, 520, 0, back);
            Anim(CoverScale, ScaleTransform.ScaleYProperty, 0.82, 1, 520, 0, back);
            Anim(CoverRotate, RotateTransform.AngleProperty, 8, 0, 520, 0, back);
            SlideIn(TitleText, 0);
            SlideIn(ArtistText, 60);
            BgWave.Splash();
        }

        static void SlideIn(UIElement el, double delay)
        {
            var tt = (TranslateTransform)el.RenderTransform;
            el.BeginAnimation(OpacityProperty, null);
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            el.Opacity = 0; tt.X = 22;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Anim(el, OpacityProperty, null, 1, 380, delay, ease);
            Anim(tt, TranslateTransform.XProperty, null, 0, 420, delay, ease);
        }

        /// <summary>Появление с задержкой: прозрачность + сдвиг снизу.</summary>
        static void Stagger(UIElement el, double delay, double fromY = 0)
        {
            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Anim(el, OpacityProperty, null, 1, 360, delay, ease);

            if (fromY != 0 && el.RenderTransform is TranslateTransform tt)
            {
                tt.BeginAnimation(TranslateTransform.YProperty, null);
                tt.Y = fromY;
                Anim(tt, TranslateTransform.YProperty, null, 0, 420, delay, ease);
            }
        }

        static void Anim(IAnimatable target, DependencyProperty dp, double? from, double to,
                         double ms, double delayMs, IEasingFunction ease, EventHandler done = null)
        {
            var a = new DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms / Speed),
                BeginTime = TimeSpan.FromMilliseconds(delayMs / Speed),
                EasingFunction = ease
            };
            if (from.HasValue) a.From = from.Value;
            if (done != null) a.Completed += done;
            target.BeginAnimation(dp, a);
        }

        // ---------- данные ----------

        void UpdateUi(bool opening)
        {
            var t = _media.Current;

            TitleText.Text = t.HasSession
                ? (string.IsNullOrWhiteSpace(t.Title) ? "Без названия" : t.Title)
                : "Ничего не играет";
            ArtistText.Text = t.HasSession ? t.Artist : "Включи музыку в любом плеере";
            AppText.Text = t.HasSession ? PrettyApp(t.AppId).ToUpperInvariant() : "WAVEDOCK";

            PlayBtn.Content = t.Playing ? "" : "";
            PlayBtn.IsEnabled = t.HasSession;
            PrevBtn.IsEnabled = t.HasSession && t.CanPrev;
            NextBtn.IsEnabled = t.HasSession && t.CanNext;

            // цвета
            Settings.Current.GetAccent(t, out var c1, out var c2);
            var accent = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1));
            accent.Freeze();
            Resources["AccentBrush"] = accent;
            Glow.Color = c1;
            LyrGlow.Color = c1;
            BgWave.SetColors(c1, c2);
            BgWave.SetState(t.HasSession, t.Playing);

            // обложка: настоящая или своя «пластинка», если у трека нет картинки
            BitmapSource cover = t.Cover ?? CoverArt.Get(t.HasSession ? t.Title : "", c1, c2);
            var ib = new ImageBrush(cover) { Stretch = Stretch.UniformToFill };
            ib.Freeze();
            CoverHost.Background = ib;
            CoverPlaceholder.Visibility = Visibility.Collapsed;
            BgCover.Source = cover;
            LyrBgCover.Source = cover;

            if (t.Key != _lastKey)
            {
                if (IsOpen && !opening && _lastKey != null && t.HasSession) PlayTrackChangeAnimation();
                _lastKey = t.Key;
                if (_lyricsOpen && IsOpen) _ = LoadLyricsAsync();
            }

            UpdateProgress();
        }

        void UpdateProgress()
        {
            var t = _media.Current;
            if (!t.HasSession || t.Duration.TotalSeconds < 1)
            {
                ProgressArea.Visibility = Visibility.Hidden;
                return;
            }
            ProgressArea.Visibility = Visibility.Visible;

            var pos = t.LivePosition;
            double r = Math.Max(0, Math.Min(1, pos.TotalSeconds / t.Duration.TotalSeconds));
            double w = Track.ActualWidth;
            ProgressFill.Width = r * w;
            Knob.Margin = new Thickness(Math.Max(0, r * w - 5), 0, 0, 0);
            PosText.Text = Fmt(pos);
            DurText.Text = Fmt(t.Duration);
        }

        void OnTick(object sender, EventArgs e)
        {
            if (++_tickCount % 4 == 0) UpdateProgress();
            if (_lyricsOpen && _tickCount % 2 == 0) UpdateLyricsHighlight(false);
            if (_hwnd != IntPtr.Zero && _tickCount % 10 == 0) Native.KeepTopmost(_hwnd);

            // запасной способ закрыться по клику мимо окна
            bool down = Native.LeftButtonDown;
            if (down && !_wasDown && IsOpen && _hwnd != IntPtr.Zero && Native.GetCursorPos(out var p))
            {
                bool inMini = _miniHwnd != IntPtr.Zero && Native.GetWindowRect(_miniHwnd, out var mr) && Native.Contains(mr, p);
                if (!inMini && Native.GetWindowRect(_hwnd, out var r))
                {
                    double s = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                    int m = (int)(CardMargin * s);
                    int top = _lyricsOpen ? m : (int)((Height - Root.Height - CardMargin) * s);
                    var card = new Native.RECT { Left = r.Left + m, Top = r.Top + top, Right = r.Right - m, Bottom = r.Bottom - m };
                    if (!Native.Contains(card, p)) CloseAnimated();
                }
            }
            _wasDown = down;
        }

        static string Fmt(TimeSpan t) =>
            t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

        static string PrettyApp(string id)
        {
            if (string.IsNullOrEmpty(id)) return "Now playing";
            string l = id.ToLowerInvariant();
            if (l.Contains("spotify")) return "Spotify";
            if (l.Contains("yandex")) return "Яндекс Музыка";
            if (l.Contains("chrome")) return "Chrome";
            if (l.Contains("msedge") || l.Contains("edge")) return "Edge";
            if (l.Contains("firefox")) return "Firefox";
            if (l.Contains("opera")) return "Opera";
            if (l.Contains("telegram")) return "Telegram";
            if (l.Contains("vk")) return "VK";
            if (l.Contains("zunemusic") || l.Contains("media")) return "Media Player";
            if (l.Contains("apple") || l.Contains("itunes")) return "Apple Music";
            if (l.Contains("soundcloud")) return "SoundCloud";
            if (l.Contains("aimp")) return "AIMP";
            string s = id;
            int bang = s.LastIndexOf('!');
            if (bang >= 0) s = s.Substring(bang + 1);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s;
        }


        // ================= громкость =================

        bool _audioHooked, _volSync;

        void HookAudio()
        {
            if (_audioHooked || AudioService.Instance == null) return;
            _audioHooked = true;
            AudioService.Instance.VolumeChanged += UpdateVolumeUi;
        }

        void UpdateVolumeUi()
        {
            var a = AudioService.Instance;
            if (a == null) { VolSlider.IsEnabled = MuteBtn.IsEnabled = false; return; }
            _volSync = true;
            double v = a.Volume;
            bool muted = a.Muted;
            VolSlider.Value = Math.Round(v * 100);
            VolSlider.Opacity = muted ? 0.45 : 1;
            MuteBtn.Content = AudioService.VolumeGlyph(v, muted);
            MuteBtn.ToolTip = muted ? "Включить звук" : $"Громкость {Math.Round(v * 100)}% · клик — без звука";
            _volSync = false;
        }

        void VolSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_volSync || AudioService.Instance == null) return;
            AudioService.Instance.Volume = e.NewValue / 100.0;
            MuteBtn.Content = AudioService.VolumeGlyph(e.NewValue / 100.0, false);
            VolSlider.Opacity = 1;
        }

        void Mute_Click(object sender, RoutedEventArgs e)
        {
            var a = AudioService.Instance;
            if (a == null) return;
            a.Muted = !a.Muted;
            UpdateVolumeUi();
        }

        // ================= текст песни =================

        bool _lyricsOpen;
        string _lyricsKey;
        LyricsResult _lyrics;
        readonly List<TextBlock> _lineBlocks = new List<TextBlock>();
        int _curLine = -1;

        public void ToggleLyrics()
        {
            _lyricsOpen = !_lyricsOpen;
            ShowLyricsCard(_lyricsOpen);
        }

        void Lyrics_Click(object sender, RoutedEventArgs e) => ToggleLyrics();

        void ShowLyricsCard(bool show)
        {
            if (show) LyricsBtn.SetResourceReference(BackgroundProperty, "AccentBrush");
            else LyricsBtn.Background = new SolidColorBrush(Color.FromArgb(0x14, 255, 255, 255));

            var cubic = new CubicEase { EasingMode = EasingMode.EaseOut };
            if (show)
            {
                LyricsCard.Visibility = Visibility.Visible;
                LyricsCard.IsHitTestVisible = true;
                Anim(LyricsCard, OpacityProperty, 0, 1, 260, 0, cubic);
                Anim(LyrScale, ScaleTransform.ScaleXProperty, 0.9, 1, 460, 0, Bouncy(0.4));
                Anim(LyrScale, ScaleTransform.ScaleYProperty, 0.9, 1, 460, 0, Bouncy(0.4));
                Anim(LyrShift, TranslateTransform.YProperty, 30, 0, 460, 0, Bouncy(0.4));
                if (_lyricsKey != _media.Current.Key || _lyrics == null) _ = LoadLyricsAsync();
                else Dispatcher.BeginInvoke(new Action(() => UpdateLyricsHighlight(true)), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else
            {
                LyricsCard.IsHitTestVisible = false;
                Anim(LyrShift, TranslateTransform.YProperty, null, 24, 200, 0, new CubicEase { EasingMode = EasingMode.EaseIn });
                Anim(LyricsCard, OpacityProperty, null, 0, 180, 0, new CubicEase { EasingMode = EasingMode.EaseIn },
                    (s, e) => { if (!_lyricsOpen) LyricsCard.Visibility = Visibility.Hidden; });
            }
        }

        async System.Threading.Tasks.Task LoadLyricsAsync()
        {
            var t = _media.Current;
            string key = t.Key;
            _lyricsKey = key;
            _lyrics = null;
            _curLine = -1;
            _lineBlocks.Clear();
            LyrLines.Children.Clear();
            LyrScroll.BeginAnimation(TranslateTransform.YProperty, null);
            LyrScroll.Y = 0;

            if (!t.HasSession || string.IsNullOrWhiteSpace(t.Title))
            {
                LyrStatus.Text = "Включи музыку — и здесь появится текст";
                LyrStatus.Visibility = Visibility.Visible;
                return;
            }

            LyrStatus.Text = "Ищу текст…";
            LyrStatus.Visibility = Visibility.Visible;

            var res = await LyricsService.GetAsync(t.Title, t.Artist, t.Album, t.Duration);
            if (key != _lyricsKey) return; // трек уже сменился
            _lyrics = res;

            if (res == null) { LyrStatus.Text = "Текст для этого трека не найден"; return; }
            if (res.Instrumental && res.Lines.Count == 0) { LyrStatus.Text = "Инструментал — без слов ♪"; return; }

            LyrStatus.Visibility = Visibility.Collapsed;
            LyrSource.Text = res.Synced ? "lrclib.net · синхронно" : "lrclib.net";

            foreach (var line in res.Lines)
            {
                var tb = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(line.Text) ? " " : line.Text,
                    FontSize = 17,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 3, 0, 3),
                    Opacity = res.Synced ? 0.32 : 0.85,
                    RenderTransformOrigin = new Point(0, 0.5),
                    RenderTransform = new ScaleTransform(1, 1)
                };
                if (res.Synced)
                {
                    var time = line.Time;
                    tb.Cursor = Cursors.Hand;
                    tb.MouseLeftButtonUp += (s, e) => _media.Seek(time); // клик по строке — перемотка
                }
                _lineBlocks.Add(tb);
                LyrLines.Children.Add(tb);
            }

            Dispatcher.BeginInvoke(new Action(() => UpdateLyricsHighlight(true)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        void UpdateLyricsHighlight(bool force)
        {
            var res = _lyrics;
            if (res == null || !res.Synced || _lineBlocks.Count == 0) return;

            var pos = _media.Current.LivePosition + TimeSpan.FromMilliseconds(250);
            int idx = -1;
            for (int i = 0; i < res.Lines.Count && i < _lineBlocks.Count; i++)
            {
                if (res.Lines[i].Time <= pos) idx = i; else break;
            }
            if (idx == _curLine && !force) return;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            if (_curLine >= 0 && _curLine < _lineBlocks.Count && _curLine != idx)
            {
                var old = _lineBlocks[_curLine];
                Anim(old, OpacityProperty, null, 0.32, 300, 0, ease);
                Anim((ScaleTransform)old.RenderTransform, ScaleTransform.ScaleXProperty, null, 1, 300, 0, ease);
                Anim((ScaleTransform)old.RenderTransform, ScaleTransform.ScaleYProperty, null, 1, 300, 0, ease);
            }
            _curLine = idx;
            if (idx < 0) { ScrollLyricsTo(0); return; }

            var cur = _lineBlocks[idx];
            Anim(cur, OpacityProperty, null, 1, 300, 0, ease);
            Anim((ScaleTransform)cur.RenderTransform, ScaleTransform.ScaleXProperty, null, 1.06, 380, 0, Bouncy(0.5));
            Anim((ScaleTransform)cur.RenderTransform, ScaleTransform.ScaleYProperty, null, 1.06, 380, 0, Bouncy(0.5));

            // держим текущую строку по центру
            double y = cur.TranslatePoint(new Point(0, 0), LyrLines).Y + cur.ActualHeight / 2;
            ScrollLyricsTo(LyrViewport.ActualHeight / 2 - y);
        }

        void ScrollLyricsTo(double target)
        {
            double min = Math.Min(0, LyrViewport.ActualHeight - LyrLines.ActualHeight - 20);
            if (_lyrics != null && _lyrics.Synced) min = -LyrLines.ActualHeight; // для синхронного текста центрируем без ограничений
            target = Math.Max(min, Math.Min(_lyrics != null && _lyrics.Synced ? LyrViewport.ActualHeight / 2 : 0, target));
            Anim(LyrScroll, TranslateTransform.YProperty, null, target, 520, 0, new CubicEase { EasingMode = EasingMode.EaseOut });
        }

        void LyricsCard_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_lyrics == null || _lyrics.Synced) return;
            double current = LyrScroll.Y;
            LyrScroll.BeginAnimation(TranslateTransform.YProperty, null);
            LyrScroll.Y = current;
            ScrollLyricsTo(current + e.Delta * 0.5);
            e.Handled = true;
        }

        // ---------- кнопки ----------

        void Gear_Click(object sender, RoutedEventArgs e)
        {
            CloseAnimated();
            SettingsWindow.ShowSingle(_media);
        }

        void Play_Click(object sender, RoutedEventArgs e) { _media.TogglePlayPause(); BgWave.Splash(); }
        void Next_Click(object sender, RoutedEventArgs e) { _media.Next(); BgWave.Splash(); }
        void Prev_Click(object sender, RoutedEventArgs e) { _media.Previous(); BgWave.Splash(); }

        void Track_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var t = _media.Current;
            if (!t.HasSession || t.Duration.TotalSeconds < 1 || Track.ActualWidth <= 0) return;
            double r = Math.Max(0, Math.Min(1, e.GetPosition(Track).X / Track.ActualWidth));
            _media.Seek(TimeSpan.FromSeconds(t.Duration.TotalSeconds * r));
            BgWave.Splash();
        }
    }
}
