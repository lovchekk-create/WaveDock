using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WaveDock
{
    /// <summary>Плашка над квадратом: «сейчас играет» при смене трека и индикатор громкости.</summary>
    public partial class ToastWindow : Window
    {
        const double M = 20; // прозрачный отступ под тень
        readonly DispatcherTimer _hideTimer = new DispatcherTimer();
        IntPtr _hwnd;
        bool _shown;
        string _trackKey;

        public event Action Clicked;

        public ToastWindow()
        {
            InitializeComponent();
            SourceInitialized += (s, e) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(_hwnd, noActivate: true);
            };
            _hideTimer.Tick += (s, e) => HideAnimated();
            MouseLeftButtonUp += (s, e) => { HideAnimated(); Clicked?.Invoke(); };
            MouseEnter += (s, e) => _hideTimer.Stop();
            MouseLeave += (s, e) => { if (_shown) { _hideTimer.Interval = TimeSpan.FromSeconds(1.5); _hideTimer.Start(); } };
        }

        void SetAccent(Color c1, Color c2)
        {
            var b = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1));
            b.Freeze();
            Resources["AccentBrush"] = b;
            Glow.Color = c1;
        }

        public void ShowTrack(TrackInfo t, Color c1, Color c2, Rect anchor, double seconds)
        {
            _trackKey = t.Key;
            TrackPanel.Visibility = Visibility.Visible;
            VolumePanel.Visibility = Visibility.Collapsed;
            SetAccent(c1, c2);
            FillTrack(t, c1, c2);

            bool wasShown = _shown;
            PlaceAndShow(anchor, seconds);
            if (wasShown)
            {
                var back = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };
                CoverScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = back });
                CoverScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = back });
            }
        }

        /// <summary>Обложка/название пришли позже — обновить, если плашка ещё на экране.</summary>
        public void RefreshTrack(TrackInfo t, Color c1, Color c2)
        {
            if (!_shown || TrackPanel.Visibility != Visibility.Visible || t.Key != _trackKey) return;
            SetAccent(c1, c2);
            FillTrack(t, c1, c2);
        }

        void FillTrack(TrackInfo t, Color c1, Color c2)
        {
            TitleText.Text = string.IsNullOrWhiteSpace(t.Title) ? "Без названия" : t.Title;
            ArtistText.Text = t.Artist ?? "";
            BitmapSource cover = t.Cover ?? CoverArt.Get(t.Title, c1, c2);
            var ib = new ImageBrush(cover) { Stretch = Stretch.UniformToFill };
            ib.Freeze();
            Cover.Background = ib;
        }

        public void ShowVolume(double volume, bool muted, Color c1, Color c2, Rect anchor)
        {
            TrackPanel.Visibility = Visibility.Collapsed;
            VolumePanel.Visibility = Visibility.Visible;
            SetAccent(c1, c2);

            int pct = (int)Math.Round(volume * 100);
            VolIcon.Text = AudioService.VolumeGlyph(volume, muted);
            VolText.Text = muted ? "выкл" : pct + "%";
            PlaceAndShow(anchor, 1.4);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                double w = VolTrack.ActualWidth * (muted ? 0 : Math.Max(0, Math.Min(1, volume)));
                VolFill.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(160))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }), DispatcherPriority.Loaded);
        }

        void PlaceAndShow(Rect anchor, double seconds)
        {
            var wa = SystemParameters.WorkArea;
            Left = Math.Round(Math.Max(wa.Left - M + 8, Math.Min(anchor.Left - M - 4, wa.Right - Width + M - 8)));
            Top = Math.Round(wa.Bottom - Height + M - 10);

            if (!_shown)
            {
                _shown = true;
                Show();
                var back = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut };
                var dur = TimeSpan.FromMilliseconds(460);
                Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
                ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(36, 0, dur) { EasingFunction = back });
                ToastScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, 1, dur) { EasingFunction = back });
                ToastScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, 1, dur) { EasingFunction = back });
            }
            if (_hwnd != IntPtr.Zero) Native.KeepTopmost(_hwnd);

            // полоска-таймер
            double barW = Math.Max(0, Width - 2 * M - 32);
            TimerBar.BeginAnimation(WidthProperty, new DoubleAnimation(barW, 0, TimeSpan.FromSeconds(seconds)));

            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromSeconds(seconds);
            _hideTimer.Start();
        }

        public void HideAnimated()
        {
            _hideTimer.Stop();
            if (!_shown) return;
            _shown = false;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var dur = TimeSpan.FromMilliseconds(200);
            ToastShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, dur) { EasingFunction = ease });
            var fade = new DoubleAnimation(0, dur) { EasingFunction = ease };
            fade.Completed += (s, e) => { if (!_shown) Hide(); };
            Root.BeginAnimation(OpacityProperty, fade);
        }
    }
}
