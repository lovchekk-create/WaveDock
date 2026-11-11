using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinMC = global::Windows.Media.Control;

namespace WaveDock
{
    public sealed class TrackInfo
    {
        public bool HasSession;
        public string Title = "", Artist = "", Album = "", AppId = "";
        public BitmapSource Cover;
        public Color Accent1 = MediaService.DefaultAccent1, Accent2 = MediaService.DefaultAccent2;
        public bool Playing, CanPrev, CanNext, CanSeek;
        public TimeSpan Position, Duration;
        public DateTimeOffset LastUpdated;

        public string Key => Title + "|" + Artist;

        /// <summary>Текущая позиция с поправкой на время, прошедшее с последнего обновления.</summary>
        public TimeSpan LivePosition
        {
            get
            {
                var p = Position;
                if (Playing && LastUpdated.Year > 2000)
                {
                    var d = DateTimeOffset.Now - LastUpdated;
                    if (d > TimeSpan.Zero && d < TimeSpan.FromHours(3)) p += d;
                }
                if (p < TimeSpan.Zero) p = TimeSpan.Zero;
                if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
                return p;
            }
        }
    }

    /// <summary>
    /// Обёртка над системными медиа-сессиями Windows (то же, что показывает плашка громкости):
    /// Spotify, Яндекс Музыка, браузеры, VK, Медиаплеер и т.д.
    /// </summary>
    public sealed class MediaService
    {
        public static readonly Color DefaultAccent1 = Color.FromRgb(0x99, 0x45, 0xFF);
        public static readonly Color DefaultAccent2 = Color.FromRgb(0x14, 0xF1, 0x95);

        readonly Dispatcher _ui;
        WinMC.GlobalSystemMediaTransportControlsSessionManager _mgr;
        WinMC.GlobalSystemMediaTransportControlsSession _session;
        int _version;
        bool _pending;

        string _coverKey;
        BitmapSource _coverCache;
        Color _c1 = DefaultAccent1, _c2 = DefaultAccent2;
        string _retryKey;
        int _coverRetries;

        public TrackInfo Current { get; private set; } = new TrackInfo();
        public event Action Changed;

        public MediaService(Dispatcher ui) { _ui = ui; }

        public async Task InitAsync()
        {
            _mgr = await WinMC.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (s, e) => _ui.BeginInvoke(new Action(AttachCurrent));
            _mgr.SessionsChanged += (s, e) => _ui.BeginInvoke(new Action(AttachCurrent));
            AttachCurrent();
        }

        void AttachCurrent()
        {
            WinMC.GlobalSystemMediaTransportControlsSession s = null;
            try { s = _mgr?.GetCurrentSession(); } catch { }

            if (!ReferenceEquals(s, _session))
            {
                if (_session != null)
                {
                    try
                    {
                        _session.MediaPropertiesChanged -= OnProps;
                        _session.PlaybackInfoChanged -= OnPlayback;
                        _session.TimelinePropertiesChanged -= OnTimeline;
                    }
                    catch { }
                }
                _session = s;
                if (_session != null)
                {
                    _session.MediaPropertiesChanged += OnProps;
                    _session.PlaybackInfoChanged += OnPlayback;
                    _session.TimelinePropertiesChanged += OnTimeline;
                }
            }
            _ = RefreshAsync();
        }

        void OnProps(WinMC.GlobalSystemMediaTransportControlsSession s, WinMC.MediaPropertiesChangedEventArgs a) => Schedule();
        void OnPlayback(WinMC.GlobalSystemMediaTransportControlsSession s, WinMC.PlaybackInfoChangedEventArgs a) => Schedule();
        void OnTimeline(WinMC.GlobalSystemMediaTransportControlsSession s, WinMC.TimelinePropertiesChangedEventArgs a) => Schedule();

        void Schedule()
        {
            _ui.BeginInvoke(new Action(() =>
            {
                if (_pending) return;
                _pending = true;
                _ui.BeginInvoke(new Action(() => { _pending = false; _ = RefreshAsync(); }), DispatcherPriority.Background);
            }));
        }

        async Task RefreshAsync()
        {
            int ver = ++_version;
            var s = _session;
            var info = new TrackInfo();

            if (s != null)
            {
                try
                {
                    info.HasSession = true;
                    info.AppId = s.SourceAppUserModelId ?? "";

                    var pb = s.GetPlaybackInfo();
                    if (pb != null)
                    {
                        info.Playing = pb.PlaybackStatus == WinMC.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                        info.CanPrev = pb.Controls?.IsPreviousEnabled ?? true;
                        info.CanNext = pb.Controls?.IsNextEnabled ?? true;
                        info.CanSeek = pb.Controls?.IsPlaybackPositionEnabled ?? false;
                    }

                    var tl = s.GetTimelineProperties();
                    if (tl != null)
                    {
                        info.Position = tl.Position - tl.StartTime;
                        info.Duration = tl.EndTime - tl.StartTime;
                        info.LastUpdated = tl.LastUpdatedTime;
                    }

                    var props = await s.TryGetMediaPropertiesAsync();
                    if (ver != _version) return;
                    if (props != null)
                    {
                        info.Title = props.Title ?? "";
                        info.Artist = props.Artist ?? "";
                        info.Album = props.AlbumTitle ?? "";

                        string key = info.Title + "|" + info.Artist + "|" + info.Album;
                        if (key != _coverKey || _coverCache == null)
                        {
                            BitmapSource cover = props.Thumbnail != null ? await LoadCoverAsync(props.Thumbnail) : null;
                            if (cover != null && LooksLikeAppIcon(cover)) cover = null; // значок браузера вместо обложки
                            if (ver != _version) return;
                            _coverKey = key;
                            _coverCache = cover;

                            // плееры часто отдают обложку чуть позже названия — пробуем ещё раз
                            if (_retryKey != key) { _retryKey = key; _coverRetries = 0; }
                            if (cover == null && _coverRetries < 4)
                            {
                                _coverRetries++;
                                _ = Task.Delay(700 * _coverRetries).ContinueWith(_ => Schedule());
                            }
                            if (cover != null) ExtractAccent(cover, out _c1, out _c2);
                            else { _c1 = DefaultAccent1; _c2 = DefaultAccent2; }
                        }
                        info.Cover = _coverCache;
                        info.Accent1 = _c1;
                        info.Accent2 = _c2;
                    }
                }
                catch (Exception ex) { App.Log(ex); }
            }

            if (ver != _version) return;
            Current = info;
            Changed?.Invoke();
        }

        static async Task<BitmapSource> LoadCoverAsync(global::Windows.Storage.Streams.IRandomAccessStreamReference reference)
        {
            try
            {
                using var ras = await reference.OpenReadAsync();
                using var src = ras.AsStreamForRead();
                var ms = new MemoryStream();
                await src.CopyToAsync(ms);
                if (ms.Length == 0) return null;
                ms.Position = 0;

                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 320;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        // ---------- управление ----------

        public async void TogglePlayPause()
        {
            var s = _session; if (s == null) return;
            try { await s.TryTogglePlayPauseAsync(); } catch { }
        }

        public async void Next()
        {
            var s = _session; if (s == null) return;
            try { await s.TrySkipNextAsync(); } catch { }
        }

        public async void Previous()
        {
            var s = _session; if (s == null) return;
            try { await s.TrySkipPreviousAsync(); } catch { }
        }

        public async void Seek(TimeSpan position)
        {
            var s = _session; if (s == null) return;
            try { await s.TryChangePlaybackPositionAsync(position.Ticks); } catch { }
        }

        /// <summary>
        /// Браузеры, когда у сайта нет картинки, отдают свой значок (логотип Chrome и т.п.).
        /// У значков прозрачный фон, у настоящих обложек — нет.
        /// </summary>
        static bool LooksLikeAppIcon(BitmapSource src)
        {
            try
            {
                BitmapSource b = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                double sc = 48.0 / Math.Max(b.PixelWidth, b.PixelHeight);
                if (sc < 1) b = new FormatConvertedBitmap(new TransformedBitmap(b, new ScaleTransform(sc, sc)), PixelFormats.Bgra32, null, 0);
                int w = b.PixelWidth, h = b.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                b.CopyPixels(px, stride, 0);
                int transparent = 0, total = w * h;
                for (int i = 3; i < px.Length; i += 4) if (px[i] < 200) transparent++;
                return total > 0 && transparent > total * 0.08;
            }
            catch { return false; }
        }

        // ---------- цвет из обложки ----------

        static void ExtractAccent(BitmapSource src, out Color c1, out Color c2)
        {
            c1 = DefaultAccent1; c2 = DefaultAccent2;
            try
            {
                BitmapSource b = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                double sc = 24.0 / Math.Max(b.PixelWidth, b.PixelHeight);
                if (sc < 1) b = new FormatConvertedBitmap(new TransformedBitmap(b, new ScaleTransform(sc, sc)), PixelFormats.Bgra32, null, 0);

                int w = b.PixelWidth, h = b.PixelHeight, stride = w * 4;
                var px = new byte[stride * h];
                b.CopyPixels(px, stride, 0);

                double r = 0, g = 0, bl = 0, wt = 0;
                for (int i = 0; i + 3 < px.Length; i += 4)
                {
                    double B = px[i] / 255.0, G = px[i + 1] / 255.0, R = px[i + 2] / 255.0;
                    double mx = Math.Max(R, Math.Max(G, B)), mn = Math.Min(R, Math.Min(G, B));
                    double sat = mx <= 0 ? 0 : (mx - mn) / mx;
                    double weight = sat * sat * mx + 0.01;
                    r += R * weight; g += G * weight; bl += B * weight; wt += weight;
                }
                if (wt <= 0) return;

                ToHsv(r / wt, g / wt, bl / wt, out double hh, out double s, out double v);
                if (s < 0.12) return; // почти ч/б обложка — оставляем фирменные цвета
                s = Math.Max(s, 0.6); v = Math.Max(v, 0.92);
                c1 = FromHsv(hh, s, v);
                c2 = FromHsv((hh + 48) % 360, Math.Min(1, s * 0.85), v);
            }
            catch { }
        }

        static void ToHsv(double r, double g, double b, out double h, out double s, out double v)
        {
            double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn;
            v = mx; s = mx <= 0 ? 0 : d / mx;
            if (d <= 0) h = 0;
            else if (mx == r) h = 60 * (((g - b) / d) % 6);
            else if (mx == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }

        static Color FromHsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }
    }
}
