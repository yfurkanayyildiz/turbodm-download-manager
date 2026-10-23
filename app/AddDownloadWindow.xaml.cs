using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using TurboDM.Core;

namespace TurboDM
{
    public partial class AddDownloadWindow : Window
    {
        readonly DownloadRequest _req;
        DownloadItem? _item;
        bool _nameEdited, _dirEdited, _settingText, _probed;
        string? _lastUrl;
        CancellationTokenSource? _cts, _trackCts;
        List<MediaTrack>? _tracks;
        DownloadItem? _tracksFor;

        public AddDownloadWindow(DownloadRequest? r)
        {
            InitializeComponent();
            _req = r ?? new DownloadRequest();
            UrlBox.Text = _req.Url;
            Loaded += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_req.Url)) UrlBox.Focus();
                else { Analyze(); StartButton.Focus(); }
            };
            Closed += (_, _) => { _cts?.Cancel(); _trackCts?.Cancel(); };
        }

        static bool ValidUrl(string s) => Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");

        void SetName(string v) { _settingText = true; NameBox.Text = v; _settingText = false; }
        void SetDir(string v) { _settingText = true; DirBox.Text = v; _settingText = false; }
        void Name_Changed(object s, System.Windows.Controls.TextChangedEventArgs e) { if (!_settingText) _nameEdited = true; }
        void Dir_Changed(object s, System.Windows.Controls.TextChangedEventArgs e) { if (!_settingText) _dirEdited = true; }
        void Url_LostFocus(object s, RoutedEventArgs e) => Analyze();
        void Url_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { Analyze(); e.Handled = true; } }

        async void Analyze()
        {
            var url = UrlBox.Text.Trim();
            if (!ValidUrl(url)) { KindText.Text = "Geçerli bir http(s) adresi girin"; return; }
            if (url == _lastUrl) return;
            _lastUrl = url;
            _probed = false;
            _req.Url = url;
            _item = App.Manager.Create(_req);
            if (!_nameEdited) SetName(_item.FileName);
            if (!_dirEdited) SetDir(_item.SaveDir);
            ShowKind();
            SizeText.Text = "—"; ResumeText.Text = "—";
            ClearTracks();

            if (_item.Kind != DownloadKind.Direct) { AnalyzeTracks(_item); return; }

            _cts?.Cancel();
            _cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var item = _item;
            SizeText.Text = "Kontrol ediliyor…";
            try
            {
                var p = await DirectDownloader.ProbeAsync(item, _cts.Token);
                if (item != _item) return;
                if (p.ContentType != null && p.ContentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
                {
                    item.Kind = DownloadKind.Hls;
                    item.FileName = Path.ChangeExtension(item.FileName, ".mp4");
                    if (!_nameEdited) SetName(item.FileName);
                    ShowKind();
                    SizeText.Text = "Yayın (parça sayısına göre)";
                    AnalyzeTracks(item);
                    return;
                }
                if (string.IsNullOrEmpty(_req.FileName) && p.FileName != null) item.FileName = p.FileName;
                if (!_nameEdited) SetName(item.FileName);
                if (!_dirEdited) SetDir(App.Settings.FolderFor(item.Category));
                SizeText.Text = p.Size > 0 ? $"{Fmt.Bytes(p.Size)}  ({p.Size:N0} bayt)" : "Bilinmiyor";
                ResumeText.Text = p.Resumable ? $"Evet — {App.Settings.Connections} bağlantı ile hızlandırılacak" : "Hayır (tek bağlantı)";
                ResumeText.Foreground = p.Resumable ? System.Windows.Media.Brushes.ForestGreen : System.Windows.Media.Brushes.DarkOrange;
                _probed = true;
                AnalyzeTracks(item);   // sayfadan yakalanan altyazılar varsa göster
            }
            catch (OperationCanceledException) { if (item == _item) SizeText.Text = "Bilinmiyor (sunucu yanıt vermedi)"; }
            catch (Exception ex) { if (item == _item) SizeText.Text = "Bilinmiyor — " + ex.Message; }
        }

        void ShowKind()
        {
            if (_item == null) return;
            KindText.Text = _item.Kind switch
            {
                DownloadKind.Hls => "HLS video akışı (m3u8) — en yüksek kalite seçilir",
                DownloadKind.YtDlp => "Video sitesi — yt-dlp ile en iyi kalitede indirilir",
                _ => "Doğrudan dosya · " + _item.Category
            };
        }

        void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Kayıt klasörünü seçin" };
            if (Directory.Exists(DirBox.Text)) dlg.InitialDirectory = DirBox.Text;
            if (dlg.ShowDialog(this) == true) DirBox.Text = dlg.FolderName;
        }

        void Commit(bool startNow)
        {
            var url = UrlBox.Text.Trim();
            if (!ValidUrl(url)) { MessageBox.Show(this, "Geçerli bir adres girin.", "TurboDM"); return; }
            if (_item == null || url != _lastUrl) { _req.Url = url; _item = App.Manager.Create(_req); }

            var name = Files.Sanitize(NameBox.Text.Trim());
            if (_item.Kind == DownloadKind.YtDlp) _item.NameLocked = _nameEdited;
            else _item.NameLocked = _nameEdited || _probed || !string.IsNullOrEmpty(_req.FileName);
            _item.FileName = name;
            if (!string.IsNullOrWhiteSpace(DirBox.Text))
            {
                _item.SaveDir = DirBox.Text.Trim();
                _item.DirLocked = _dirEdited || _probed || _item.Kind != DownloadKind.Direct;
            }

            if (ModePanel.Visibility == Visibility.Visible) _item.AudioOnly = AudioMode;
            if (_tracks != null && _tracksFor == _item)
            {
                var audio = Checked(AudioPanel);
                bool hasAudioChoice = _tracks.Count(t => t.Kind == TrackKind.Audio) > 1;
                _item.AudioTracks = hasAudioChoice ? audio : null;
                _item.SubTracks = _item.AudioOnly ? new List<string>() : Checked(SubPanel);
            }

            if (DontAsk.IsChecked == true)
            {
                App.Settings.ShowAddDialog = false;
                App.Settings.Save();
            }
            App.Manager.Add(_item, startNow);
            Close();
        }

        // ---------------- Dublaj / altyazı ----------------

        void ClearTracks()
        {
            _trackCts?.Cancel();
            _tracks = null; _tracksFor = null;
            AudioPanel.Children.Clear(); SubPanel.Children.Clear();
            TracksPanel.Visibility = Visibility.Collapsed;
        }

        async void AnalyzeTracks(DownloadItem item)
        {
            var external = MediaTrack.FromExternal(item.ExternalSubs);
            if (item.Kind == DownloadKind.Direct)
            {
                // doğrudan video dosyası: yalnızca sayfadan yakalanan altyazılar seçilebilir
                if (external.Count == 0 || item.AudioOnly) return;
                ShowTracks(item, external, false);
                return;
            }
            _trackCts?.Cancel();
            _trackCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var token = _trackCts.Token;
            TracksPanel.Visibility = Visibility.Visible;
            AudioHeader.Visibility = SubHeader.Visibility = MkvHint.Visibility = Visibility.Collapsed;
            AudioPanel.Children.Clear(); SubPanel.Children.Clear();
            ModePanel.Visibility = Visibility.Collapsed;
            TracksStatus.Text = "Dublaj ve altyazı seçenekleri aranıyor…";
            try
            {
                var tracks = item.Kind == DownloadKind.Hls
                    ? await HlsDownloader.AnalyzeAsync(item, token)
                    : await YtDlpDownloader.AnalyzeAsync(item, App.Settings, token);
                if (item != _item) return;
                ShowTracks(item, tracks.Concat(external).ToList(), true);
            }
            catch (Exception ex)
            {
                if (item != _item) return;
                if (external.Count > 0) { ShowTracks(item, external, true); TracksStatus.Text += " (yayın bilgisi alınamadı: " + ex.Message + ")"; return; }
                ShowModes(item.AudioOnly);
                TracksStatus.Text = token.IsCancellationRequested
                    ? "Seçenekler alınamadı (zaman aşımı). Ayarlardaki varsayılan diller kullanılacak."
                    : "Seçenekler alınamadı: " + ex.Message + " — ayarlardaki varsayılan diller kullanılacak.";
            }
        }

        void ShowTracks(DownloadItem item, List<MediaTrack> tracks, bool modes)
        {
            TracksPanel.Visibility = Visibility.Visible;
            AudioPanel.Children.Clear(); SubPanel.Children.Clear();
            AudioHeader.Visibility = SubHeader.Visibility = Visibility.Collapsed;
            _tracks = tracks; _tracksFor = item;
            var audio = tracks.Where(t => t.Kind == TrackKind.Audio).ToList();
            var subs = tracks.Where(t => t.Kind == TrackKind.Subtitle).ToList();
            if (modes) ShowModes(item.AudioOnly); else ModePanel.Visibility = Visibility.Collapsed;
            MkvHint.Visibility = Visibility.Visible;
            if (audio.Count <= 1 && subs.Count == 0)
            {
                TracksStatus.Text = "Bu videoda ek dublaj veya altyazı yok. (Oynatıcıda altyazıyı açıp videoyu tekrar gönderirsen yakalanır.)";
                UpdateMkvHint();
                return;
            }
            var defA = TrackSelection.Resolve(tracks, null, App.Settings, TrackKind.Audio).Select(t => t.Key).ToHashSet();
            var defS = TrackSelection.Resolve(tracks.Where(t => !t.IsExternal).ToList(), null, App.Settings, TrackKind.Subtitle)
                .Concat(TrackSelection.Resolve(tracks.Where(t => t.IsExternal).ToList(), null, App.Settings, TrackKind.Subtitle))
                .Select(t => t.Key).ToHashSet();
            if (audio.Count > 1)
            {
                AudioHeader.Visibility = Visibility.Visible;
                foreach (var t in audio) AudioPanel.Children.Add(MakeCheck(t, defA.Contains(t.Key)));
            }
            if (subs.Count > 0)
            {
                SubHeader.Visibility = Visibility.Visible;
                foreach (var t in subs) SubPanel.Children.Add(MakeCheck(t, defS.Contains(t.Key)));
            }
            int ext = subs.Count(t => t.IsExternal);
            TracksStatus.Text = $"{audio.Count} ses, {subs.Count} altyazı bulundu" + (ext > 0 ? $" ({ext} tanesi sayfadan yakalanan dosya)" : "") +
                                $". Tercih ettiğin diller ({App.Settings.PreferredLanguages}) işaretlendi.";
            UpdateMkvHint();
        }

        void ShowModes(bool audioOnly)
        {
            ModePanel.Visibility = Visibility.Visible;
            if (audioOnly) ModeAudio.IsChecked = true; else ModeVideo.IsChecked = true;
            ApplyMode();
        }

        bool AudioMode => ModePanel.Visibility == Visibility.Visible && ModeAudio.IsChecked == true;

        void Mode_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            ApplyMode();
            if (!_dirEdited && _item != null)
                SetDir(AudioMode ? App.Settings.FolderFor(Categories.Music) : App.Settings.FolderFor(_item.Category));
        }

        void ApplyMode()
        {
            bool audio = AudioMode;
            SubHeader.Opacity = SubScroll.Opacity = audio ? 0.4 : 1;
            SubScroll.IsEnabled = !audio;
            UpdateMkvHint();
        }

        CheckBox MakeCheck(MediaTrack t, bool isChecked)
        {
            var c = new CheckBox { Content = t.Display, Tag = t.Key, IsChecked = isChecked, Margin = new Thickness(0, 3, 16, 3), ToolTip = t.Key };
            c.Checked += (_, _) => UpdateMkvHint();
            c.Unchecked += (_, _) => UpdateMkvHint();
            return c;
        }

        static List<string> Checked(Panel p) =>
            p.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag).ToList();

        void UpdateMkvHint()
        {
            int a = Math.Max(1, Checked(AudioPanel).Count), s = Checked(SubPanel).Count;
            if (AudioMode)
            {
                MkvHint.Text = a > 1
                    ? $"Seçilen {a} dublaj ayrı ses dosyaları olarak kaydedilecek (ör. ad.tr.m4a, ad.en.m4a). Görüntü indirilmez."
                    : "Yalnızca ses kaydedilecek (HLS'de .m4a, YouTube'da .mp3). Görüntü indirilmez.";
                return;
            }
            MkvHint.Text = a > 1 || s > 0
                ? $"Tek MKV dosyası oluşturulacak: {a} ses izi, {s} altyazı. (ffmpeg gerekir)"
                : "Yalnızca video ve tek ses indirilecek (MP4).";
        }

        void SetAll(bool value)
        {
            foreach (var c in AudioPanel.Children.OfType<CheckBox>().Concat(SubPanel.Children.OfType<CheckBox>())) c.IsChecked = value;
        }

        void TracksAll_Click(object sender, RoutedEventArgs e) => SetAll(true);
        void TracksNone_Click(object sender, RoutedEventArgs e) => SetAll(false);

        void Start_Click(object sender, RoutedEventArgs e) => Commit(true);
        void Later_Click(object sender, RoutedEventArgs e) => Commit(false);
        void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
