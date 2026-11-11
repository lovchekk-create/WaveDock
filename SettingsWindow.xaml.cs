using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WaveDock
{
    /// <summary>Окно настроек. Открывается шестерёнкой в плеере или из меню по правому клику.</summary>
    public partial class SettingsWindow : Window
    {
        static SettingsWindow _instance;
        readonly MediaService _media;

        public static void ShowSingle(MediaService media)
        {
            if (_instance == null)
            {
                _instance = new SettingsWindow(media);
                _instance.Closed += (s, e) => _instance = null;
                _instance.Show();
            }
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
        }

        SettingsWindow(MediaService media)
        {
            InitializeComponent();
            _media = media;
            DataContext = Settings.Current;

            AutoChk.IsChecked = Autostart.IsEnabled;
            AutoChk.Checked += (s, e) => Autostart.Set(true);
            AutoChk.Unchecked += (s, e) => Autostart.Set(false);

            Settings.Changed += Apply;
            _media.Changed += Apply;
            Closed += (s, e) => { Settings.Changed -= Apply; _media.Changed -= Apply; };

            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += (s, e) => PlayOpen();
            Apply();
        }

        void Apply()
        {
            var st = Settings.Current;
            st.GetAccent(_media.Current, out var c1, out var c2);

            var accent = new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1));
            accent.Freeze();
            Resources["AccentBrush"] = accent;
            Glow.Color = c1;

            // превью в шапке повторяет настройки квадрата
            double size = Settings.Clamp(st.Size, 22, 42);
            Preview.Radius = Settings.Clamp(st.CornerRadius, 0, size / 2) * Preview.Width / size;
            Preview.BgOpacity = Settings.Clamp(st.BgOpacity, 0, 1);
            Preview.ShowBorder = st.Border;
            Preview.SpeedMul = Settings.Clamp(st.WaveSpeed, 0.1, 4);
            Preview.AmpMul = Settings.Clamp(st.WaveHeight, 0.1, 3);
            Preview.Layers = (int)Math.Round(Settings.Clamp(st.WaveLayers, 1, 3));
            Preview.PlayLevel = Settings.Clamp(st.FillLevel, 0.1, 0.95);
            Preview.Reactive = st.AudioReactive;
            Preview.Sensitivity = Settings.Clamp(st.Sensitivity, 0.2, 3);
            Preview.SetColors(c1, c2);
            Preview.SetState(true, true);
            Preview.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = c1, ShadowDepth = 0, BlurRadius = 4 + 20 * Settings.Clamp(st.Glow, 0, 1),
                Opacity = Settings.Clamp(st.Glow, 0, 1)
            };
        }

        void PlayOpen()
        {
            Root.Opacity = 0;
            var ease = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(380);
            RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, dur) { EasingFunction = ease });
            RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, dur) { EasingFunction = ease });
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            Preview.Splash();
        }

        void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        void Close_Click(object sender, RoutedEventArgs e) => Close();

        void Preset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string tag) return;
            var parts = tag.Split(',');
            if (parts.Length != 2) return;
            var st = Settings.Current;
            st.Color1 = parts[0];
            st.Color2 = parts[1];
            st.AutoColors = false;
            Preview.Splash();
        }

        void Reset_Click(object sender, RoutedEventArgs e)
        {
            Settings.Current.ResetToDefaults();
            Preview.Splash();
        }
    }
}
