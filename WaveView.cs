using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace WaveDock
{
    /// <summary>
    /// Живая «жидкость» в скруглённом квадрате: несколько слоёв синусоид,
    /// которые плавно меняют высоту, силу и цвет в зависимости от состояния музыки.
    /// </summary>
    public class WaveView : FrameworkElement
    {
        public double Radius { get; set; } = 10;
        public bool DrawBackground { get; set; } = true;
        public double WaveScale { get; set; } = 1.0;

        // настраиваемые параметры
        public double SpeedMul { get; set; } = 1.0;
        public double AmpMul { get; set; } = 1.0;
        public int Layers { get; set; } = 3;
        public double PlayLevel { get; set; } = 0.56;
        public double BgOpacity { get; set; } = 1.0;
        public bool ShowBorder { get; set; } = true;
        bool _hasSession, _playing;

        // реакция на настоящий звук
        public static Func<double> AudioLevel;
        public bool Reactive { get; set; } = true;
        public double Sensitivity { get; set; } = 1.0;
        double _react, _peak = 0.05;

        Color _a1 = MediaService.DefaultAccent1, _a2 = MediaService.DefaultAccent2;
        Color _t1 = MediaService.DefaultAccent1, _t2 = MediaService.DefaultAccent2;
        double _level = 0.25, _levelT = 0.25, _amp = 0.15, _ampT = 0.15, _speed = 0.5, _speedT = 0.5;
        double _phase, _time, _last;
        readonly Stopwatch _sw = Stopwatch.StartNew();

        public WaveView()
        {
            Loaded += (s, e) => CompositionTarget.Rendering += OnFrame;
            Unloaded += (s, e) => CompositionTarget.Rendering -= OnFrame;
        }

        public void SetColors(Color c1, Color c2) { _t1 = c1; _t2 = c2; }

        public void SetState(bool hasSession, bool playing)
        {
            _hasSession = hasSession; _playing = playing;
            double lvl = Math.Max(0.1, Math.Min(0.95, PlayLevel));
            if (!hasSession) { _levelT = Math.Min(0.22, lvl); _ampT = 0.12; _speedT = 0.35; }
            else if (playing) { _levelT = lvl; _ampT = 1.0; _speedT = 1.7; }
            else { _levelT = lvl * 0.62; _ampT = 0.3; _speedT = 0.55; }
        }

        /// <summary>Пересчитать после смены настроек.</summary>
        public void ApplyState() => SetState(_hasSession, _playing);

        /// <summary>Всплеск — например, при клике.</summary>
        public void Splash() { _amp += 0.9; _speed += 2.5; }

        void OnFrame(object sender, EventArgs e)
        {
            if (!IsVisible) return;
            double now = _sw.Elapsed.TotalSeconds;
            double dt = Math.Min(0.05, now - _last);
            _last = now;
            _time += dt;

            double k = 1 - Math.Exp(-dt * 3.0);
            _level += (_levelT - _level) * k;
            _amp += (_ampT - _amp) * k;
            _speed += (_speedT - _speed) * k;
            _a1 = Lerp(_a1, _t1, k);
            _a2 = Lerp(_a2, _t2, k);

            // громкость звука -> «всплеск» волн: быстрая атака, плавный спад, авто-подстройка под громкость
            double target = 0;
            if (Reactive && _playing && AudioLevel != null)
            {
                double lv = AudioLevel();
                _peak = Math.Max(Math.Max(lv, 0.015), _peak * Math.Pow(0.5, dt / 2.5));
                target = Math.Min(1.6, Math.Pow(lv / _peak, 1.3) * Sensitivity);
            }
            double rk = target > _react ? 1 - Math.Exp(-dt * 28) : 1 - Math.Exp(-dt * 6);
            _react += (target - _react) * rk;

            _phase += dt * _speed * 2.4 * SpeedMul * (1 + 0.9 * _react);

            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            var rect = new Rect(0, 0, w, h);

            if (DrawBackground)
            {
                var bg = new LinearGradientBrush(Color.FromRgb(0x1C, 0x15, 0x33), Color.FromRgb(0x0A, 0x08, 0x14), 90)
                { Opacity = Math.Max(0, Math.Min(1, BgOpacity)) };
                bg.Freeze();
                dc.DrawRoundedRectangle(bg, null, rect, Radius, Radius);
            }

            var clip = new RectangleGeometry(rect, Radius, Radius);
            clip.Freeze();
            dc.PushClip(clip);

            // «дыхание» музыки: неровная пульсация силы волн
            bool live = Reactive && _playing && AudioLevel != null;
            double pulse = live
                ? 0.3 + 1.15 * _react
                : 0.72 + 0.28 * Math.Abs(Math.Sin(_time * 2.1) * Math.Sin(_time * 1.33 + 0.7));
            double step = Math.Max(1.0, w / 70.0);

            int layers = Math.Max(1, Math.Min(3, Layers));
            for (int i = layers - 1; i >= 0; i--) // сначала дальние слои
            {
                double baseY = h * (1 - _level) - i * h * 0.07 - (live ? h * 0.09 * _react : 0);
                double amp = _amp * pulse * h * 0.11 * WaveScale * AmpMul * (1 - i * 0.2);
                double f = 2 * Math.PI / w * (1.0 + i * 0.4);
                double ph = _phase * (1 + i * 0.35) + i * 1.9;

                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(0, h), true, true);
                    for (double x = 0; x <= w + step; x += step)
                    {
                        double y = baseY + amp * Math.Sin(x * f + ph) + amp * 0.35 * Math.Sin(x * f * 2.3 - ph * 1.4);
                        c.LineTo(new Point(x, y), true, false);
                    }
                    c.LineTo(new Point(w + step, h), true, false);
                }
                g.Freeze();

                byte alpha = (byte)(i == 0 ? 240 : i == 1 ? 140 : 80);
                var br = new LinearGradientBrush(WithA(_a1, alpha), WithA(_a2, alpha), new Point(0, 0), new Point(1, 1));
                br.Freeze();
                dc.DrawGeometry(br, null, g);
            }

            // стеклянный блик сверху
            var shine = new LinearGradientBrush(Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
            shine.Freeze();
            dc.DrawRectangle(shine, null, new Rect(0, 0, w, h * 0.45));
            dc.Pop();

            if (DrawBackground && ShowBorder)
            {
                var pb = new LinearGradientBrush(WithA(_a1, 220), WithA(_a2, 220), 45);
                pb.Freeze();
                var pen = new Pen(pb, 1.2);
                pen.Freeze();
                dc.DrawRoundedRectangle(null, pen, new Rect(0.6, 0.6, w - 1.2, h - 1.2), Radius, Radius);
            }
        }

        static Color WithA(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

        static Color Lerp(Color a, Color b, double t) => Color.FromRgb(Step(a.R, b.R, t), Step(a.G, b.G, t), Step(a.B, b.B, t));

        static byte Step(byte a, byte b, double t)
        {
            if (a == b) return a;
            int r = (int)Math.Round(a + (b - a) * t);
            if (r == a) r += Math.Sign(b - a);
            return (byte)Math.Max(0, Math.Min(255, r));
        }
    }
}
