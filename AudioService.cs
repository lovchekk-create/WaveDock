using System;
using System.Threading;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WaveDock
{
    /// <summary>
    /// Слушает звук, который сейчас выводится на колонки/наушники (для живых волн),
    /// и управляет системной громкостью.
    /// </summary>
    public sealed class AudioService
    {
        public static AudioService Instance { get; private set; }

        readonly Dispatcher _ui;
        readonly MMDeviceEnumerator _enum = new MMDeviceEnumerator();
        readonly DispatcherTimer _deviceWatch;
        MMDevice _device;
        string _deviceId;
        WasapiLoopbackCapture _capture;
        double _rms;
        long _lastData;

        /// <summary>Громкость или «без звука» изменились (в т.ч. из Windows).</summary>
        public event Action VolumeChanged;

        public AudioService(Dispatcher ui)
        {
            _ui = ui;
            Instance = this;
            Attach();

            // следим за сменой устройства вывода (подключили наушники и т.п.)
            _deviceWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _deviceWatch.Tick += (s, e) =>
            {
                try
                {
                    var d = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (d.ID != _deviceId) { Attach(); VolumeChanged?.Invoke(); }
                }
                catch { }
            };
            _deviceWatch.Start();
        }

        void Attach()
        {
            Detach();
            try
            {
                _device = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _deviceId = _device.ID;
                _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;

                _capture = new WasapiLoopbackCapture(_device);
                _capture.DataAvailable += OnData;
                _capture.StartRecording();
            }
            catch (Exception ex) { App.Log(ex); }
        }

        void Detach()
        {
            try
            {
                if (_capture != null)
                {
                    _capture.DataAvailable -= OnData;
                    _capture.StopRecording();
                    _capture.Dispose();
                }
            }
            catch { }
            _capture = null;

            try { if (_device != null) _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; }
            catch { }
            _device = null;
        }

        void OnVolumeNotification(AudioVolumeNotificationData data) =>
            _ui.BeginInvoke(new Action(() => VolumeChanged?.Invoke()));

        void OnData(object sender, WaveInEventArgs e)
        {
            var cap = _capture;
            if (cap == null || e.BytesRecorded <= 0) return;
            var fmt = cap.WaveFormat;
            int bytes = fmt.BitsPerSample / 8;
            if (bytes <= 0) return;

            bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                           (fmt.Encoding == WaveFormatEncoding.Extensible && fmt.BitsPerSample == 32);
            double sum = 0;
            int n = 0;

            for (int i = 0; i + bytes <= e.BytesRecorded; i += bytes)
            {
                double v;
                if (isFloat && bytes == 4) v = BitConverter.ToSingle(e.Buffer, i);
                else if (bytes == 2) v = BitConverter.ToInt16(e.Buffer, i) / 32768.0;
                else if (bytes == 4) v = BitConverter.ToInt32(e.Buffer, i) / 2147483648.0;
                else continue;
                sum += v * v;
                n++;
            }
            if (n == 0) return;
            _rms = Math.Sqrt(sum / n);
            Interlocked.Exchange(ref _lastData, DateTime.UtcNow.Ticks);
        }

        /// <summary>Текущая громкость звучания (RMS, примерно 0…0.5). При тишине — 0.</summary>
        public double Level
        {
            get
            {
                long last = Interlocked.Read(ref _lastData);
                if (DateTime.UtcNow.Ticks - last > TimeSpan.TicksPerMillisecond * 250) return 0;
                return _rms;
            }
        }

        /// <summary>Системная громкость 0…1.</summary>
        public double Volume
        {
            get
            {
                try { return _device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0; }
                catch { return 0; }
            }
            set
            {
                try
                {
                    if (_device == null) return;
                    float v = (float)Math.Max(0, Math.Min(1, value));
                    _device.AudioEndpointVolume.MasterVolumeLevelScalar = v;
                    if (v > 0 && _device.AudioEndpointVolume.Mute) _device.AudioEndpointVolume.Mute = false;
                }
                catch { }
            }
        }

        public bool Muted
        {
            get
            {
                try { return _device?.AudioEndpointVolume.Mute ?? false; }
                catch { return false; }
            }
            set
            {
                try { if (_device != null) _device.AudioEndpointVolume.Mute = value; }
                catch { }
            }
        }

        public static string VolumeGlyph(double v, bool muted) =>
            muted || v <= 0.001 ? "" : v < 0.33 ? "" : v < 0.66 ? "" : "";
    }
}
