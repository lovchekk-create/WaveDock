using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WaveDock
{
    /// <summary>Все настройки виджета. Любое изменение сразу применяется и через полсекунды сохраняется.</summary>
    public class Settings : INotifyPropertyChanged
    {
        public static readonly string Folder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WaveDock");
        static string FilePath => Path.Combine(Folder, "settings.json");

        static Settings _current;
        public static Settings Current => _current ??= Load();

        /// <summary>Срабатывает при любом изменении настроек.</summary>
        public static event Action Changed;
        public event PropertyChangedEventHandler PropertyChanged;

        static DispatcherTimer _saveTimer;
        static bool _loading;

        // ---------- квадрат ----------
        double _offsetX = 8, _offsetY = 0, _size = 34, _cornerRadius = 10, _bgOpacity = 1, _glow = 0.7;
        bool _border = true;
        public double OffsetX { get => _offsetX; set => Set(ref _offsetX, value); }
        public double OffsetY { get => _offsetY; set => Set(ref _offsetY, value); }
        public double Size { get => _size; set => Set(ref _size, value); }
        public double CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, value); }
        public double BgOpacity { get => _bgOpacity; set => Set(ref _bgOpacity, value); }
        public double Glow { get => _glow; set => Set(ref _glow, value); }
        public bool Border { get => _border; set => Set(ref _border, value); }

        // ---------- волны ----------
        double _waveHeight = 1, _waveSpeed = 1, _waveLayers = 3, _fillLevel = 0.56;
        public double WaveHeight { get => _waveHeight; set => Set(ref _waveHeight, value); }
        public double WaveSpeed { get => _waveSpeed; set => Set(ref _waveSpeed, value); }
        public double WaveLayers { get => _waveLayers; set => Set(ref _waveLayers, value); }
        public double FillLevel { get => _fillLevel; set => Set(ref _fillLevel, value); }

        // ---------- цвета ----------
        bool _autoColors = true;
        string _color1 = "#9945FF", _color2 = "#14F195";
        public bool AutoColors
        {
            get => _autoColors;
            set { if (Set(ref _autoColors, value)) Raise(nameof(CustomColors)); }
        }
        [JsonIgnore] public bool CustomColors { get => !_autoColors; set { if (value) AutoColors = false; } }
        public string Color1 { get => _color1; set => Set(ref _color1, value); }
        public string Color2 { get => _color2; set => Set(ref _color2, value); }

        // ---------- плеер ----------
        bool _blurCover = true, _springAnim = true, _openOnHover = false;
        double _popupOpacity = 1, _animSpeed = 1;
        public bool BlurCover { get => _blurCover; set => Set(ref _blurCover, value); }
        public double PopupOpacity { get => _popupOpacity; set => Set(ref _popupOpacity, value); }
        public double AnimSpeed { get => _animSpeed; set => Set(ref _animSpeed, value); }
        public bool SpringAnim
        {
            get => _springAnim;
            set { if (Set(ref _springAnim, value)) Raise(nameof(SmoothAnim)); }
        }
        [JsonIgnore] public bool SmoothAnim { get => !_springAnim; set { if (value) SpringAnim = false; } }
        public bool OpenOnHover
        {
            get => _openOnHover;
            set { if (Set(ref _openOnHover, value)) Raise(nameof(OpenOnClick)); }
        }
        [JsonIgnore] public bool OpenOnClick { get => !_openOnHover; set { if (value) OpenOnHover = false; } }

        // ---------- поведение ----------
        bool _wheelTracks = true, _middlePause = true, _hideFullscreen = true;
        public bool WheelTracks { get => _wheelTracks; set => Set(ref _wheelTracks, value); }
        public bool MiddlePause { get => _middlePause; set => Set(ref _middlePause, value); }
        public bool HideFullscreen { get => _hideFullscreen; set => Set(ref _hideFullscreen, value); }

        // ---------- звук и уведомления ----------
        bool _audioReactive = true, _trackToast = true;
        double _sensitivity = 1, _toastSeconds = 3.5;
        public bool AudioReactive { get => _audioReactive; set => Set(ref _audioReactive, value); }
        public double Sensitivity { get => _sensitivity; set => Set(ref _sensitivity, value); }
        public bool TrackToast { get => _trackToast; set => Set(ref _trackToast, value); }
        public double ToastSeconds { get => _toastSeconds; set => Set(ref _toastSeconds, value); }

        // ---------- горячие клавиши ----------
        bool _hotkeys = true;
        string _hotkeyMod = "CtrlAlt";
        public bool Hotkeys { get => _hotkeys; set => Set(ref _hotkeys, value); }
        public string HotkeyMod
        {
            get => _hotkeyMod;
            set
            {
                if (Set(ref _hotkeyMod, value))
                {
                    Raise(nameof(ModCtrlAlt)); Raise(nameof(ModCtrlShift)); Raise(nameof(ModAltShift)); Raise(nameof(HotkeyHint));
                }
            }
        }
        [JsonIgnore] public bool ModCtrlAlt { get => _hotkeyMod == "CtrlAlt"; set { if (value) HotkeyMod = "CtrlAlt"; } }
        [JsonIgnore] public bool ModCtrlShift { get => _hotkeyMod == "CtrlShift"; set { if (value) HotkeyMod = "CtrlShift"; } }
        [JsonIgnore] public bool ModAltShift { get => _hotkeyMod == "AltShift"; set { if (value) HotkeyMod = "AltShift"; } }
        [JsonIgnore] public string HotkeyHint
        {
            get
            {
                string m = _hotkeyMod == "CtrlShift" ? "Ctrl+Shift" : _hotkeyMod == "AltShift" ? "Alt+Shift" : "Ctrl+Alt";
                return m + " + Пробел (или P) — пауза\n" + m + " + → / ← — следующий / предыдущий\n" +
                       m + " + ↑ / ↓ — громкость\n" + m + " + L — плеер с текстом песни";
            }
        }

        // ---------- Discord ----------
        bool _discordStatus = true, _discordShowPaused = false;
        public const string DefaultDiscordAppId = "1570021657425354833"; // приложение «WaveDock» в Discord
        string _discordAppId = DefaultDiscordAppId;
        public bool DiscordStatus { get => _discordStatus; set => Set(ref _discordStatus, value); }
        public bool DiscordShowPaused { get => _discordShowPaused; set => Set(ref _discordShowPaused, value); }
        public string DiscordAppId { get => _discordAppId; set => Set(ref _discordAppId, value); }

        // ---------- служебное ----------

        bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            if (!_loading)
            {
                Changed?.Invoke();
                ScheduleSave();
            }
            return true;
        }

        void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        void ScheduleSave()
        {
            if (Application.Current == null) { Save(); return; }
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); Save(); };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        static Settings Load()
        {
            _loading = true;
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
            catch { }
            finally { _loading = false; }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        /// <summary>Вернуть всё по умолчанию (кроме положения на панели).</summary>
        public void ResetToDefaults()
        {
            var d = new Settings();
            foreach (var p in typeof(Settings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanWrite || p.GetCustomAttribute<JsonIgnoreAttribute>() != null) continue;
                if (p.Name == nameof(OffsetX) || p.Name == nameof(OffsetY)) continue;
                p.SetValue(this, p.GetValue(d));
            }
        }

        /// <summary>Итоговые цвета: из обложки или свои.</summary>
        public void GetAccent(TrackInfo t, out Color c1, out Color c2)
        {
            if (AutoColors && t != null) { c1 = t.Accent1; c2 = t.Accent2; }
            else
            {
                c1 = ParseColor(Color1, MediaService.DefaultAccent1);
                c2 = ParseColor(Color2, MediaService.DefaultAccent2);
            }
        }

        public static Color ParseColor(string s, Color fallback)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(s)) return fallback;
                s = s.Trim();
                if (!s.StartsWith("#")) s = "#" + s;
                if (s.Length != 7 && s.Length != 9) return fallback;
                if (ColorConverter.ConvertFromString(s) is Color c) return c;
            }
            catch { }
            return fallback;
        }

        public static double Clamp(double v, double min, double max) =>
            double.IsNaN(v) ? min : Math.Max(min, Math.Min(max, v));
    }

    public static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "WaveDock";

        public static bool IsEnabled
        {
            get
            {
                try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(Name) != null; }
                catch { return false; }
            }
        }

        public static void Set(bool on)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (on) k.SetValue(Name, "\"" + Environment.ProcessPath + "\"");
                else k.DeleteValue(Name, false);
            }
            catch { }
        }
    }
}
