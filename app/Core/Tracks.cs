using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TurboDM.Core
{
    public enum TrackKind { Audio, Subtitle }

    /// <summary>Bir videonun ek ses (dublaj) ya da altyazı izi.</summary>
    public class MediaTrack
    {
        public TrackKind Kind { get; init; }
        public string Key { get; init; } = "";        // seçim anahtarı (kalıcı)
        public string Language { get; init; } = "";
        public string Name { get; init; } = "";
        public bool IsDefault { get; init; }
        public bool IsAuto { get; init; }             // otomatik (makine) altyazı
        public string? Uri { get; init; }             // HLS: alt playlist adresi (null = ana yayının içinde)
        public bool IsExternal => Key.StartsWith("ext:");
        public ExternalSub? Source { get; init; }  // sayfadan yakalanan ayrı altyazı dosyası

        /// <summary>Sayfadan yakalanan harici altyazıları iz listesine çevirir.</summary>
        public static List<MediaTrack> FromExternal(IEnumerable<ExternalSub>? subs) =>
            (subs ?? Enumerable.Empty<ExternalSub>()).Select(s =>
            {
                var lang = Langs.Normalize(s.Lang);
                if (lang.Length == 0) lang = Langs.Guess(s.Name + " " + s.Url);
                var name = !string.IsNullOrWhiteSpace(s.Name) ? s.Name! : lang.Length > 0 ? Langs.DisplayName(lang) : Files.NameFromUrl(s.Url);
                return new MediaTrack { Kind = TrackKind.Subtitle, Key = "ext:" + s.Url, Uri = s.Url, Language = lang, Name = name, Source = s };
            }).ToList();

        public string Display
        {
            get
            {
                var lang = Langs.DisplayName(Language);
                var n = string.IsNullOrWhiteSpace(Name) || string.Equals(Name, Language, StringComparison.OrdinalIgnoreCase) || string.Equals(Name, lang, StringComparison.CurrentCultureIgnoreCase) ? lang : $"{Name} ({lang})";
                if (IsAuto) n += " · otomatik";
                if (IsDefault && Kind == TrackKind.Audio) n += " · varsayılan";
                return n;
            }
        }
    }

    public static class Langs
    {
        // ISO 639-1 ↔ 639-2 (sık kullanılanlar); eşleştirmede ikisi de kabul edilir
        static readonly Dictionary<string, string> Two2Three = new(StringComparer.OrdinalIgnoreCase)
        {
            ["tr"] = "tur", ["en"] = "eng", ["de"] = "ger", ["fr"] = "fre", ["es"] = "spa", ["it"] = "ita", ["pt"] = "por",
            ["ru"] = "rus", ["ar"] = "ara", ["ja"] = "jpn", ["ko"] = "kor", ["zh"] = "chi", ["nl"] = "dut", ["pl"] = "pol",
            ["sv"] = "swe", ["no"] = "nor", ["da"] = "dan", ["fi"] = "fin", ["el"] = "gre", ["hi"] = "hin", ["fa"] = "per",
            ["az"] = "aze", ["uk"] = "ukr", ["he"] = "heb", ["id"] = "ind", ["th"] = "tha", ["vi"] = "vie", ["hu"] = "hun",
            ["cs"] = "cze", ["ro"] = "rum", ["bg"] = "bul", ["ku"] = "kur",
        };
        static readonly Dictionary<string, string> AltThree = new(StringComparer.OrdinalIgnoreCase)
        { ["deu"] = "ger", ["fra"] = "fre", ["zho"] = "chi", ["nld"] = "dut", ["ell"] = "gre", ["fas"] = "per", ["ces"] = "cze", ["ron"] = "rum" };

        /// <summary>"tr-TR", "tur", "TR" → "tr" ; bilinmiyorsa küçük harfli ilk parça.</summary>
        public static string Normalize(string? lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) return "";
            var p = lang.Trim().Split('-', '_', '.')[0].ToLowerInvariant();
            if (AltThree.TryGetValue(p, out var t)) p = t;
            foreach (var kv in Two2Three) if (kv.Value == p) return kv.Key;
            return p;
        }

        public static bool Matches(string? lang, IEnumerable<string> preferred)
        {
            var n = Normalize(lang);
            return n.Length > 0 && preferred.Any(p => Normalize(p) == n);
        }

        /// <summary>Matroska için ISO 639-2 kodu.</summary>
        public static string ToIso3(string? lang)
        {
            var n = Normalize(lang);
            return Two2Three.TryGetValue(n, out var t) ? t : (n.Length == 3 ? n : "und");
        }

        public static string DisplayName(string? lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) return "Bilinmeyen dil";
            try
            {
                var c = CultureInfo.GetCultureInfo(Normalize(lang));
                if (!string.IsNullOrEmpty(c.NativeName) && !c.NativeName.StartsWith("Unknown")) return char.ToUpper(c.NativeName[0]) + c.NativeName[1..];
            }
            catch { }
            return lang!;
        }

        static readonly (string code, string[] words)[] Words =
        {
            ("tr", new[] { "tr", "tur", "turkce", "türkçe", "turkish", "trk" }),
            ("en", new[] { "en", "eng", "english", "ingilizce", "İngilizce" }),
            ("de", new[] { "de", "ger", "deu", "german", "deutsch", "almanca" }),
            ("fr", new[] { "fr", "fre", "fra", "french", "francais", "français", "fransizca", "fransızca" }),
            ("es", new[] { "es", "spa", "spanish", "espanol", "español", "ispanyolca" }),
            ("it", new[] { "it", "ita", "italian", "italiano", "italyanca" }),
            ("ar", new[] { "ar", "ara", "arabic", "arapca", "arapça" }),
            ("ru", new[] { "ru", "rus", "russian", "rusca", "rusça" }),
            ("pt", new[] { "pt", "por", "portuguese", "portekizce" }),
            ("ja", new[] { "ja", "jpn", "japanese", "japonca" }),
            ("ko", new[] { "ko", "kor", "korean", "korece" }),
            ("az", new[] { "az", "aze", "azerbaijani", "azerice" }),
        };

        /// <summary>Etiket / dosya adı / adresten dil tahmini ("Türkçe", "film.tur.srt", ".../tr/sub.vtt" → "tr").</summary>
        public static string Guess(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var tokens = System.Text.RegularExpressions.Regex.Split(Uri.UnescapeDataString(text).ToLowerInvariant(), @"[^\p{L}]+")
                .Where(t => t.Length > 0).ToHashSet();
            foreach (var (code, words) in Words)
                if (words.Any(w => tokens.Contains(w.ToLowerInvariant()))) return code;
            return "";
        }

        public static List<string> ParseList(string? csv) =>
            (csv ?? "").Split(',', ';', ' ').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    public static class TrackSelection
    {
        /// <summary>
        /// Hangi izlerin indirileceğine karar verir. Kullanıcı seçtiyse o; seçmediyse ayarlar:
        /// "tümü" açıksa hepsi, değilse tercih edilen diller (+ varsayılan ses).
        /// </summary>
        public static List<MediaTrack> Resolve(IReadOnlyList<MediaTrack> available, List<string>? selectedKeys, Settings s, TrackKind kind)
        {
            var list = available.Where(t => t.Kind == kind).ToList();
            if (list.Count == 0) return list;
            List<MediaTrack> result;
            if (selectedKeys != null) result = list.Where(t => selectedKeys.Contains(t.Key)).ToList();
            else if (s.DownloadAllTracks) result = list.Where(t => !t.IsAuto).ToList();
            else
            {
                var pref = Langs.ParseList(s.PreferredLanguages);
                if (kind == TrackKind.Subtitle && !s.DownloadSubtitles) return new();
                // dili bilinmeyen harici altyazılar da alınır (kullanıcı oynatıcıda açtığı için yakalandı)
                result = list.Where(t => Langs.Matches(t.Language, pref) || (t.IsExternal && t.Language.Length == 0)).ToList();
                // aynı dilde hem normal hem otomatik altyazı varsa normal olanı al
                if (kind == TrackKind.Subtitle)
                    result = result.GroupBy(t => t.IsExternal && t.Language.Length == 0 ? t.Key : Langs.Normalize(t.Language)).Select(g => g.FirstOrDefault(t => !t.IsAuto) ?? g.First()).ToList();
            }
            if (kind == TrackKind.Audio && result.Count == 0)
                result.Add(list.FirstOrDefault(t => t.IsDefault) ?? list[0]); // en az bir ses izi
            return result;
        }
    }
}
