using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WaveDock
{
    /// <summary>
    /// Своя обложка для треков без картинки: виниловая пластинка в цветах трека
    /// с первой буквой названия на «яблоке».
    /// </summary>
    public static class CoverArt
    {
        static readonly Dictionary<string, BitmapSource> _cache = new Dictionary<string, BitmapSource>();

        public static BitmapSource Get(string title, Color c1, Color c2)
        {
            string letter = "♪";
            if (!string.IsNullOrWhiteSpace(title))
            {
                foreach (char ch in title.Trim())
                    if (char.IsLetterOrDigit(ch)) { letter = char.ToUpper(ch).ToString(); break; }
            }

            string key = letter + c1 + c2;
            if (_cache.TryGetValue(key, out var cached)) return cached;
            if (_cache.Count > 64) _cache.Clear();

            var bmp = Render(letter, c1, c2);
            _cache[key] = bmp;
            return bmp;
        }

        static BitmapSource Render(string letter, Color c1, Color c2)
        {
            const int S = 360;
            var rect = new Rect(0, 0, S, S);
            var c = new Point(S / 2.0, S / 2.0);
            var dv = new DrawingVisual();

            using (var dc = dv.RenderOpen())
            {
                // фон — градиент + затемнение к краям
                dc.DrawRectangle(new LinearGradientBrush(c1, c2, new Point(0, 0), new Point(1, 1)), null, rect);
                var vignette = new RadialGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(150, 0, 0, 0))
                { GradientOrigin = new Point(0.3, 0.25), Center = new Point(0.4, 0.35), RadiusX = 0.9, RadiusY = 0.9 };
                dc.DrawRectangle(vignette, null, rect);

                // тень пластинки
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, new Point(c.X + 6, c.Y + 10), S * 0.37, S * 0.37);

                // пластинка
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(14, 12, 22)), null, c, S * 0.37, S * 0.37);
                var groove = new Pen(new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)), 1.2);
                for (double r = 0.355; r > 0.16; r -= 0.018)
                    dc.DrawEllipse(null, groove, c, S * r, S * r);

                // блик на виниле
                var shine = new LinearGradientBrush(
                    Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0, 0), new Point(0.6, 0.6));
                var disc = new EllipseGeometry(c, S * 0.37, S * 0.37);
                dc.PushClip(disc);
                dc.DrawGeometry(shine, null, new PathGeometry(new[]
                {
                    new PathFigure(new Point(0, 0), new PathSegment[]
                    {
                        new LineSegment(new Point(S * 0.62, 0), false),
                        new LineSegment(new Point(0, S * 0.62), false)
                    }, true)
                }));
                dc.Pop();

                // «яблоко» с буквой
                dc.DrawEllipse(new LinearGradientBrush(c2, c1, 45), new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 2), c, S * 0.14, S * 0.14);

                var ft = new FormattedText(letter, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI, Segoe UI Symbol"),
                        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    S * 0.13, Brushes.White, 1.0);
                dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));

                // маленькие волны внизу — фирменный штрих
                var wave = new StreamGeometry();
                using (var g = wave.Open())
                {
                    g.BeginFigure(new Point(0, S), true, true);
                    for (int x = 0; x <= S; x += 4)
                        g.LineTo(new Point(x, S * 0.9 + Math.Sin(x / (double)S * Math.PI * 3) * S * 0.025), true, false);
                    g.LineTo(new Point(S, S), true, false);
                }
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), null, wave);
            }

            var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
    }
}
