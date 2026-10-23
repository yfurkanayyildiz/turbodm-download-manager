using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using TurboDM.Core;

namespace TurboDM
{
    public partial class SettingsWindow : Window
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public SettingsWindow()
        {
            InitializeComponent();
            var s = App.Settings;
            FolderBox.Text = s.DownloadFolder;
            CategoryCheck.IsChecked = s.UseCategoryFolders;
            ConnBox.ItemsSource = new[] { 1, 2, 4, 6, 8, 12, 16, 24, 32 };
            ConnBox.SelectedItem = ConnBox.Items.Cast<int>().OrderBy(v => Math.Abs(v - s.Connections)).First();
            ConcurrentBox.ItemsSource = Enumerable.Range(1, 10).ToArray();
            ConcurrentBox.SelectedItem = Math.Clamp(s.MaxConcurrent, 1, 10);
            LimitBox.Text = s.SpeedLimitKBps.ToString();
            PortBox.Text = s.Port.ToString();
            AskCheck.IsChecked = s.ShowAddDialog;
            TrayCheck.IsChecked = s.MinimizeToTray;
            NotifyCheck.IsChecked = s.NotifyOnComplete;
            YtDlpBox.Text = s.YtDlpPath ?? "";
            FfmpegBox.Text = s.FfmpegPath ?? "";
            FormatBox.ItemsSource = new[] { "bv*+ba/b", "bv*[height<=1080]+ba/b[height<=1080]", "bv*[height<=720]+ba/b[height<=720]", "ba/b (yalnızca ses)" };
            FormatBox.Text = s.YtDlpFormat;
            LangsBox.Text = s.PreferredLanguages;
            SubsCheck.IsChecked = s.DownloadSubtitles;
            AllTracksCheck.IsChecked = s.DownloadAllTracks;
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); StartupCheck.IsChecked = k?.GetValue("TurboDM") != null; } catch { }
            RefreshToolStatus();
        }

        void RefreshToolStatus()
        {
            var yt = Files.FindTool(string.IsNullOrWhiteSpace(YtDlpBox.Text) ? null : YtDlpBox.Text, "yt-dlp.exe");
            var ff = Files.FindTool(string.IsNullOrWhiteSpace(FfmpegBox.Text) ? null : FfmpegBox.Text, "ffmpeg.exe");
            ToolStatus.Text = $"yt-dlp: {(yt != null ? "bulundu" : "yok (ilk video indirmesinde otomatik indirilir)")}\nffmpeg: {(ff != null ? "bulundu" : "yok (ana penceredeki uyarıdan kurabilirsiniz)")}";
        }

        void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var d = new OpenFolderDialog { Title = "İndirme klasörü" };
            if (Directory.Exists(FolderBox.Text)) d.InitialDirectory = FolderBox.Text;
            if (d.ShowDialog(this) == true) FolderBox.Text = d.FolderName;
        }

        void BrowseYtDlp_Click(object sender, RoutedEventArgs e) => BrowseExe(YtDlpBox, "yt-dlp.exe");
        void BrowseFfmpeg_Click(object sender, RoutedEventArgs e) => BrowseExe(FfmpegBox, "ffmpeg.exe");

        void BrowseExe(System.Windows.Controls.TextBox box, string name)
        {
            var d = new OpenFileDialog { Filter = $"{name}|{name}|Programlar|*.exe", Title = name + " seçin" };
            if (d.ShowDialog(this) == true) { box.Text = d.FileName; RefreshToolStatus(); }
        }

        async void UpdateYtDlp_Click(object sender, RoutedEventArgs e)
        {
            UpdateYtDlpButton.IsEnabled = false;
            ToolStatus.Text = "yt-dlp hazırlanıyor…";
            try
            {
                var exe = await YtDlpDownloader.EnsureAsync(App.Settings, CancellationToken.None);
                ToolStatus.Text = "yt-dlp güncelleniyor…";
                var (code, err) = await HlsDownloader.RunProcess(exe, new[] { "-U" }, CancellationToken.None);
                RefreshToolStatus();
                ToolStatus.Text += code == 0 ? "\nyt-dlp güncel." : "\nGüncelleme: " + err.Trim();
            }
            catch (Exception ex) { ToolStatus.Text = "Hata: " + ex.Message; }
            UpdateYtDlpButton.IsEnabled = true;
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PortBox.Text, out var port) || port < 1024 || port > 65535) { MessageBox.Show(this, "Port 1024–65535 arasında olmalı."); return; }
            if (!int.TryParse(LimitBox.Text, out var limit) || limit < 0) { MessageBox.Show(this, "Hız sınırı geçersiz."); return; }
            var fmt = FormatBox.Text.Trim();
            if (fmt.StartsWith("ba/b")) fmt = "ba/b";

            var s = new Settings
            {
                DownloadFolder = string.IsNullOrWhiteSpace(FolderBox.Text) ? new Settings().DownloadFolder : FolderBox.Text.Trim(),
                UseCategoryFolders = CategoryCheck.IsChecked == true,
                Connections = (int)(ConnBox.SelectedItem ?? 8),
                MaxConcurrent = (int)(ConcurrentBox.SelectedItem ?? 3),
                SpeedLimitKBps = limit,
                Port = port,
                ShowAddDialog = AskCheck.IsChecked == true,
                MinimizeToTray = TrayCheck.IsChecked == true,
                NotifyOnComplete = NotifyCheck.IsChecked == true,
                YtDlpPath = string.IsNullOrWhiteSpace(YtDlpBox.Text) ? null : YtDlpBox.Text.Trim(),
                FfmpegPath = string.IsNullOrWhiteSpace(FfmpegBox.Text) ? null : FfmpegBox.Text.Trim(),
                YtDlpFormat = string.IsNullOrWhiteSpace(fmt) ? "bv*+ba/b" : fmt,
                PreferredLanguages = string.Join(",", Langs.ParseList(LangsBox.Text)),
                DownloadSubtitles = SubsCheck.IsChecked == true,
                DownloadAllTracks = AllTracksCheck.IsChecked == true,
            };

            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
                if (StartupCheck.IsChecked == true && exe != null) k.SetValue("TurboDM", $"\"{exe}\" --tray");
                else k.DeleteValue("TurboDM", false);
            }
            catch { }

            App.ApplySettings(s);
            App.MainWin.UpdateFfmpegBanner();
            Close();
        }
    }
}
