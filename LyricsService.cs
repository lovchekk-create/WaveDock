using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WaveDock
{
    public sealed class LyricsLine
    {
        public TimeSpan Time;
        public string Text;
    }

    public sealed class LyricsResult
    {
        public bool Instrumental;
        public bool Synced;
        public List<LyricsLine> Lines = new List<LyricsLine>();
    }

    /// <summary>Тексты песен с lrclib.net — бесплатно и без ключа. Синхронизированные, если есть.</summary>
    public static class LyricsService
    {
        static readonly HttpClient Http = CreateClient();
        static readonly Dictionary<string, LyricsResult> Cache = new Dictionary<string, LyricsResult>();
        static readonly Regex TimeTag = new Regex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);
        static readonly Regex Junk = new Regex(
            @"\s*[\(\[](official|lyric|lyrics|audio|video|music video|mv|hd|hq|4k|visualizer|премьера|клип|official video|official audio)[^\)\]]*[\)\]]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("WaveDock/1.2 (Windows taskbar music widget)");
            return c;
        }

        public static async Task<LyricsResult> GetAsync(string title, string artist, string album, TimeSpan duration)
        {
            title = (title ?? "").Trim();
            artist = (artist ?? "").Trim();
            if (title.Length == 0) return null;

            string key = artist + "|" + title;
            if (Cache.TryGetValue(key, out var cached)) return cached;

            string cleanTitle = Junk.Replace(title, "").Trim();
            string cleanArtist = artist.Replace(" - Topic", "").Trim();

            // варианты «исполнитель / название»: у YouTube в заголовке часто «Исполнитель - Название»
            var variants = new List<(string Artist, string Title)>();
            int dash = cleanTitle.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) variants.Add((cleanTitle.Substring(0, dash).Trim(), cleanTitle.Substring(dash + 3).Trim()));
            if (cleanArtist.Length > 0) variants.Add((cleanArtist, cleanTitle));
            // несколько исполнителей («A, B & C feat. D») — пробуем по первому
            var first = Regex.Split(cleanArtist, @"\s*(?:,|&|;|\bfeat\.?|\bft\.?|\bx\b|\bи\b)\s*", RegexOptions.IgnoreCase)[0].Trim();
            if (first.Length > 0 && first != cleanArtist) variants.Add((first, cleanTitle));

            LyricsResult result = null;
            try
            {
                foreach (var v in variants)
                {
                    string q = "track_name=" + U(v.Title) + "&artist_name=" + U(v.Artist);
                    string exact = q;
                    if (!string.IsNullOrWhiteSpace(album)) exact += "&album_name=" + U(album);
                    if (duration.TotalSeconds > 1) exact += "&duration=" + (int)Math.Round(duration.TotalSeconds);

                    result = ParseOne(await TryGet("https://lrclib.net/api/get?" + exact))
                          ?? PickBest(await TryGet("https://lrclib.net/api/search?" + q), duration);
                    if (result != null) break;
                }
                if (result == null)
                    result = PickBest(await TryGet("https://lrclib.net/api/search?q=" + U((cleanArtist + " " + cleanTitle).Trim())), duration);
            }
            catch (Exception ex) { App.Log(ex); }

            if (Cache.Count > 100) Cache.Clear();
            Cache[key] = result;
            return result;
        }

        static string U(string s) => Uri.EscapeDataString(s ?? "");

        static async Task<string> TryGet(string url)
        {
            try
            {
                using var resp = await Http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsStringAsync();
            }
            catch { return null; }
        }

        static LyricsResult ParseOne(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                return FromElement(doc.RootElement);
            }
            catch { return null; }
        }

        static LyricsResult PickBest(string json, TimeSpan duration)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

                LyricsResult best = null;
                int bestScore = int.MinValue;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var r = FromElement(el);
                    if (r == null) continue;
                    int score = r.Synced ? 10 : r.Instrumental ? 1 : 5;
                    if (duration.TotalSeconds > 1 && el.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number)
                    {
                        double diff = Math.Abs(d.GetDouble() - duration.TotalSeconds);
                        score += diff < 3 ? 6 : diff < 10 ? 2 : -4;
                    }
                    if (score > bestScore) { bestScore = score; best = r; }
                }
                return best;
            }
            catch { return null; }
        }

        static LyricsResult FromElement(JsonElement el)
        {
            string synced = Str(el, "syncedLyrics");
            string plain = Str(el, "plainLyrics");
            bool instrumental = el.TryGetProperty("instrumental", out var i) && i.ValueKind == JsonValueKind.True;

            var r = new LyricsResult { Instrumental = instrumental };
            if (!string.IsNullOrWhiteSpace(synced))
            {
                r.Synced = true;
                foreach (var raw in synced.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    var matches = TimeTag.Matches(line);
                    if (matches.Count == 0) continue;
                    string text = line.Substring(matches[matches.Count - 1].Index + matches[matches.Count - 1].Length).Trim();
                    foreach (Match m in matches)
                    {
                        int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                        double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                        r.Lines.Add(new LyricsLine { Time = TimeSpan.FromSeconds(min * 60 + sec), Text = text.Length == 0 ? "♪" : text });
                    }
                }
                r.Lines.Sort((a, b) => a.Time.CompareTo(b.Time));
            }
            else if (!string.IsNullOrWhiteSpace(plain))
            {
                foreach (var raw in plain.Split('\n'))
                    r.Lines.Add(new LyricsLine { Text = raw.TrimEnd('\r') });
            }
            else if (!instrumental) return null;

            return r;
        }

        static string Str(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
