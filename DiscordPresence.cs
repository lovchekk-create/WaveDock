using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace WaveDock
{
    /// <summary>
    /// Статус в Discord: «Слушает WaveDock» + трек, исполнитель, обложка и полоска времени.
    /// Работает через локальный канал Discord (discord-ipc), без сторонних библиотек.
    /// </summary>
    public sealed class DiscordPresence
    {
        readonly MediaService _media;
        readonly DispatcherTimer _debounce;
        readonly DispatcherTimer _retry;
        readonly SemaphoreSlim _io = new SemaphoreSlim(1, 1);
        NamedPipeClientStream _pipe;
        string _connectedId;
        string _lastSig;
        bool _cleared = true;

        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        static readonly Dictionary<string, string> ArtCache = new Dictionary<string, string>();

        public DiscordPresence(MediaService media)
        {
            _media = media;
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _debounce.Tick += async (s, e) => { _debounce.Stop(); await PushAsync(); };
            _retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _retry.Tick += async (s, e) => { _lastSig = null; await PushAsync(); };
            _retry.Start();

            _media.Changed += Schedule;
            Settings.Changed += () => { _lastSig = null; Schedule(); };
            Schedule();
        }

        void Schedule() { _debounce.Stop(); _debounce.Start(); }

        async Task PushAsync()
        {
            var st = Settings.Current;
            var t = _media.Current;
            string appId = (st.DiscordAppId ?? "").Trim();
            if (appId.Length == 0) appId = Settings.DefaultDiscordAppId;

            bool show = st.DiscordStatus && appId.Length > 0 && t.HasSession && !string.IsNullOrWhiteSpace(t.Title)
                        && (t.Playing || st.DiscordShowPaused);

            if (!show)
            {
                if (!_cleared && _pipe != null) { await SendActivityAsync(appId, null); _cleared = true; }
                _lastSig = null;
                return;
            }

            // не дёргаем Discord без причины (у него лимит ~5 обновлений за 20 секунд)
            var pos = t.LivePosition;
            long startSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)pos.TotalSeconds;
            string sig = appId + "|" + t.Key + "|" + t.Playing + "|" + (startSec / 3);
            if (sig == _lastSig) return;
            _lastSig = sig;

            string art = await FindArtworkAsync(t.Artist, t.Title);

            string details = Fit(t.Title, "WaveDock");
            string state = Fit(string.IsNullOrWhiteSpace(t.Artist) ? "WaveDock" : t.Artist, "WaveDock");
            if (!t.Playing) state = Fit("⏸ " + state, "На паузе");

            var assets = new Dictionary<string, object>
            {
                ["large_image"] = art ?? "wavedock",
                ["large_text"] = Fit(string.IsNullOrWhiteSpace(t.Album) ? "WaveDock" : t.Album + " · WaveDock", "WaveDock"),
                ["small_image"] = "wavedock",
                ["small_text"] = "WaveDock — музыкальный виджет для Windows"
            };

            var activity = new Dictionary<string, object>
            {
                ["type"] = 2,                  // «Слушает»
                ["status_display_type"] = 2,   // в списке участников видно название трека
                ["details"] = details,
                ["state"] = state,
                ["assets"] = assets,
                ["buttons"] = new[]
                {
                    new Dictionary<string, string>
                    {
                        ["label"] = "Найти на YouTube",
                        ["url"] = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString((t.Artist + " " + t.Title).Trim())
                    }
                }
            };
            if (t.Playing && t.Duration.TotalSeconds > 1)
            {
                activity["timestamps"] = new Dictionary<string, long>
                {
                    ["start"] = startSec,
                    ["end"] = startSec + (long)t.Duration.TotalSeconds
                };
            }

            if (await SendActivityAsync(appId, activity)) _cleared = false;
            else _lastSig = null; // попробуем ещё раз позже
        }

        static string Fit(string s, string fallback)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) s = fallback;
            if (s.Length < 2) s += " ";
            return s.Length > 128 ? s.Substring(0, 127) + "…" : s;
        }

        // ---------- обложка по ссылке (Discord принимает https-картинки) ----------

        static async Task<string> FindArtworkAsync(string artist, string title)
        {
            string key = (artist + "|" + title).ToLowerInvariant();
            if (ArtCache.TryGetValue(key, out var cached)) return cached;
            string url = null;
            try
            {
                string term = Uri.EscapeDataString((artist + " " + title).Trim());
                string json = await Http.GetStringAsync("https://itunes.apple.com/search?entity=song&limit=1&term=" + term);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("results", out var arr) && arr.GetArrayLength() > 0 &&
                    arr[0].TryGetProperty("artworkUrl100", out var a) && a.ValueKind == JsonValueKind.String)
                    url = a.GetString().Replace("100x100bb", "512x512bb");
            }
            catch { }
            if (ArtCache.Count > 200) ArtCache.Clear();
            ArtCache[key] = url;
            return url;
        }

        // ---------- протокол Discord IPC ----------

        async Task<bool> SendActivityAsync(string appId, Dictionary<string, object> activity)
        {
            await _io.WaitAsync();
            try
            {
                if (!await EnsureConnectedAsync(appId)) return false;
                var payload = new Dictionary<string, object>
                {
                    ["cmd"] = "SET_ACTIVITY",
                    ["args"] = new Dictionary<string, object> { ["pid"] = Environment.ProcessId, ["activity"] = activity },
                    ["nonce"] = Guid.NewGuid().ToString()
                };
                await WriteFrameAsync(1, JsonSerializer.Serialize(payload));
                var (op, json) = await ReadFrameAsync();
                if (op != 1 || json.Contains("\"evt\":\"ERROR\""))
                {
                    App.Log("Discord: " + json);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                App.Log("Discord disconnected: " + ex.Message);
                Disconnect();
                return false;
            }
            finally { _io.Release(); }
        }

        async Task<bool> EnsureConnectedAsync(string appId)
        {
            if (_pipe != null && _pipe.IsConnected && _connectedId == appId) return true;
            Disconnect();

            for (int i = 0; i < 10; i++)
            {
                var p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await p.ConnectAsync(250); _pipe = p; break; }
                catch { p.Dispose(); }
            }
            if (_pipe == null) return false; // Discord не запущен

            await WriteFrameAsync(0, JsonSerializer.Serialize(new Dictionary<string, object> { ["v"] = 1, ["client_id"] = appId }));
            var (op, json) = await ReadFrameAsync();
            if (op != 1 || !json.Contains("READY"))
            {
                App.Log("Discord handshake failed: " + json);
                Disconnect();
                return false;
            }
            _connectedId = appId;
            App.Log("Discord connected");
            return true;
        }

        void Disconnect()
        {
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
            _connectedId = null;
            _cleared = true;
        }

        async Task WriteFrameAsync(int op, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] frame = new byte[8 + body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), op);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), body.Length);
            body.CopyTo(frame, 8);
            await _pipe.WriteAsync(frame, 0, frame.Length);
            await _pipe.FlushAsync();
        }

        async Task<(int op, string json)> ReadFrameAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            byte[] header = new byte[8];
            await ReadExactAsync(header, cts.Token);
            int op = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
            int len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4, 4));
            if (len < 0 || len > 1 << 20) throw new InvalidOperationException("bad frame");
            byte[] body = new byte[len];
            await ReadExactAsync(body, cts.Token);
            return (op, Encoding.UTF8.GetString(body));
        }

        async Task ReadExactAsync(byte[] buf, CancellationToken ct)
        {
            int read = 0;
            while (read < buf.Length)
            {
                int n = await _pipe.ReadAsync(buf, read, buf.Length - read, ct);
                if (n <= 0) throw new System.IO.EndOfStreamException();
                read += n;
            }
        }
    }
}
