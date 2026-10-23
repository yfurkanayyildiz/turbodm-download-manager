using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using TurboDM.Core;
using WF = System.Windows.Forms;

namespace TurboDM
{
    public partial class MainWindow : Window
    {
        const string CatAll = "Tümü", CatActive = "İndirilenler", CatDone = "Tamamlananlar", CatPaused = "Bekleyenler";

        readonly DownloadManager _m = App.Manager;
        readonly ListCollectionView _view;
        readonly DispatcherTimer _timer;
        WF.NotifyIcon? _tray;
        string _category = CatAll;
        bool _trayHintShown;

        public MainWindow()
        {
            InitializeComponent();

            _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_m.Items);
            _view.Filter = FilterItem;
            _view.IsLiveFiltering = true;
            _view.LiveFilteringProperties.Add(nameof(DownloadItem.Status));
            _view.LiveFilteringProperties.Add(nameof(DownloadItem.FileName));
            DownloadsGrid.ItemsSource = _view;

            CategoryList.ItemsSource = new[] { CatAll, CatActive, CatPaused, CatDone }.Concat(Categories.All).ToArray();
            CategoryList.SelectedIndex = 0;

            _m.ItemCompleted += OnCompleted;
            _m.ItemFailed += it => _tray?.ShowBalloonTip(4000, "İndirme başarısız", $"{it.FileName}\n{it.ErrorMessage}", WF.ToolTipIcon.Error);
            _m.Items.CollectionChanged += (_, _) => UpdateEmpty();
            UpdateEmpty();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();

            InputBindings.Add(new KeyBinding(new RelayCommand(() => Add_Click(this, new RoutedEventArgs())), Key.N, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(new RelayCommand(PasteUrl), Key.V, ModifierKeys.Control));

            SetupTray();
            UpdateFfmpegBanner();
        }

        // ---------------- Filtre ----------------

        bool FilterItem(object o)
        {
            var it = (DownloadItem)o;
            var q = SearchBox?.Text?.Trim();
            if (!string.IsNullOrEmpty(q) && !it.FileName.Contains(q, StringComparison.OrdinalIgnoreCase) && !it.Url.Contains(q, StringComparison.OrdinalIgnoreCase))
                return false;
            return _category switch
            {
                CatAll => true,
                CatActive => it.Status is DownloadStatus.Downloading or DownloadStatus.Processing or DownloadStatus.Queued,
                CatPaused => it.Status is DownloadStatus.Paused or DownloadStatus.Error,
                CatDone => it.Status == DownloadStatus.Completed,
                _ => it.Category == _category
            };
        }

        void Category_Changed(object sender, SelectionChangedEventArgs e)
        {
            _category = CategoryList.SelectedItem as string ?? CatAll;
            _view?.Refresh();
        }

        void Search_Changed(object sender, TextChangedEventArgs e) => _view?.Refresh();

        void UpdateEmpty() => EmptyHint.Visibility = _m.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // ---------------- Zamanlayıcı ----------------

        void Tick()
        {
            double speed = _m.Tick();
            int active = _m.ActiveCount;
            int queued = _m.Items.Count(i => i.Status == DownloadStatus.Queued);
            StatusLine.Text = active > 0
                ? $"{active} indirme aktif{(queued > 0 ? $", {queued} sırada" : "")}  •  Toplam hız: {Fmt.Bytes((long)speed)}/s" + (App.Settings.SpeedLimitKBps > 0 ? $"  (sınır {App.Settings.SpeedLimitKBps} KB/s)" : "")
                : $"{_m.Items.Count} indirme  •  {_m.Items.Count(i => i.Status == DownloadStatus.Completed)} tamamlandı";
            Title = active > 0 ? $"TurboDM — {Fmt.Bytes((long)speed)}/s" : "TurboDM";
            if (_tray != null)
            {
                var t = active > 0 ? $"TurboDM — {active} aktif, {Fmt.Bytes((long)speed)}/s" : "TurboDM";
                _tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
            }
        }

        public void UpdateServerStatus()
        {
            bool ok = App.Server.Running;
            ServerDot.Fill = new System.Windows.Media.SolidColorBrush(ok ? System.Windows.Media.Color.FromRgb(0x16, 0xA3, 0x4A) : System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
            ServerText.Text = ok ? $"Tarayıcı köprüsü açık (port {App.Server.Port})" : "Tarayıcı köprüsü kapalı";
        }

        public void UpdateFfmpegBanner() =>
            FfmpegBanner.Visibility = Tools.FindFfmpeg(App.Settings) == null ? Visibility.Visible : Visibility.Collapsed;

        void OnCompleted(DownloadItem it)
        {
            if (App.Settings.NotifyOnComplete)
                _tray?.ShowBalloonTip(3000, "İndirme tamamlandı", it.FileName, WF.ToolTipIcon.Info);
        }

        // ---------------- Seçim yardımcıları ----------------

        DownloadItem[] Selected() => DownloadsGrid.SelectedItems.Cast<DownloadItem>().ToArray();

        void Add_Click(object sender, RoutedEventArgs e)
        {
            string? clip = null;
            try { var c = Clipboard.GetText()?.Trim(); if (c != null && (c.StartsWith("http://") || c.StartsWith("https://"))) clip = c; } catch { }
            new AddDownloadWindow(clip == null ? null : new DownloadRequest { Url = clip }) { Owner = this }.Show();
        }

        void PasteUrl()
        {
            if (Keyboard.FocusedElement is TextBox) return;
            Add_Click(this, new RoutedEventArgs());
        }

        void Resume_Click(object sender, RoutedEventArgs e) { foreach (var it in Selected()) _m.Resume(it); }
        void Pause_Click(object sender, RoutedEventArgs e) { foreach (var it in Selected()) _m.Pause(it); }
        void ResumeAll_Click(object sender, RoutedEventArgs e) => _m.ResumeAll();
        void PauseAll_Click(object sender, RoutedEventArgs e) => _m.PauseAll();
        void Restart_Click(object sender, RoutedEventArgs e) { foreach (var it in Selected()) _m.Restart(it); }
        void ClearCompleted_Click(object sender, RoutedEventArgs e) => _m.RemoveCompleted();

        void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected(false);
        void DeleteWithFile_Click(object sender, RoutedEventArgs e) => DeleteSelected(true);

        void DeleteSelected(bool withFile)
        {
            var sel = Selected();
            if (sel.Length == 0) return;
            var msg = withFile ? $"{sel.Length} indirme ve diskteki dosyaları silinsin mi?" : $"{sel.Length} indirme listeden kaldırılsın mı?\n(İndirilmiş dosyalar silinmez.)";
            if (MessageBox.Show(this, msg, "TurboDM", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            foreach (var it in sel) _m.Remove(it, withFile);
        }

        void CopyUrl_Click(object sender, RoutedEventArgs e)
        {
            var it = Selected().FirstOrDefault();
            if (it != null) try { Clipboard.SetText(it.Url); } catch { }
        }

        void OpenFile_Click(object sender, RoutedEventArgs e) { if (Selected().FirstOrDefault() is { } it) OpenFile(it); }

        void ShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Selected().FirstOrDefault() is not { } it) return;
            if (File.Exists(it.FullPath)) Process.Start("explorer.exe", $"/select,\"{it.FullPath}\"");
            else if (Directory.Exists(it.SaveDir)) Process.Start("explorer.exe", $"\"{it.SaveDir}\"");
        }

        void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var dir = Selected().FirstOrDefault()?.SaveDir ?? App.Settings.DownloadFolder;
            Directory.CreateDirectory(dir);
            Process.Start("explorer.exe", $"\"{dir}\"");
        }

        void OpenFile(DownloadItem it)
        {
            if (it.Status == DownloadStatus.Completed && File.Exists(it.FullPath))
                try { Process.Start(new ProcessStartInfo(it.FullPath) { UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show(this, ex.Message); }
        }

        void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DownloadsGrid.SelectedItem is not DownloadItem it) return;
            if (it.Status == DownloadStatus.Completed) OpenFile(it);
            else if (it.Status is DownloadStatus.Paused or DownloadStatus.Error) _m.Resume(it);
        }

        void Grid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete) { DeleteSelected(Keyboard.Modifiers == ModifierKeys.Shift); e.Handled = true; }
            else if (e.Key == Key.Space)
            {
                foreach (var it in Selected()) { if (_m.IsRunning(it) || it.Status == DownloadStatus.Queued) _m.Pause(it); else _m.Resume(it); }
                e.Handled = true;
            }
        }

        void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow { Owner = this }.ShowDialog();

        async void InstallFfmpeg_Click(object sender, RoutedEventArgs e)
        {
            FfmpegButton.IsEnabled = false;
            try
            {
                var p = new Progress<double>(v => FfmpegText.Text = $"ffmpeg indiriliyor… %{v:0}");
                var path = await Tools.InstallFfmpegAsync(p, CancellationToken.None);
                FfmpegText.Text = "ffmpeg kuruldu: " + path;
                await System.Threading.Tasks.Task.Delay(2500);
                UpdateFfmpegBanner();
            }
            catch (Exception ex)
            {
                FfmpegText.Text = "ffmpeg indirilemedi: " + ex.Message;
                FfmpegButton.IsEnabled = true;
            }
        }

        // ---------------- Sistem tepsisi ----------------

        void SetupTray()
        {
            try
            {
                var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
                _tray = new WF.NotifyIcon
                {
                    Icon = s != null ? new System.Drawing.Icon(s) : System.Drawing.SystemIcons.Application,
                    Text = "TurboDM",
                    Visible = true,
                };
                var menu = new WF.ContextMenuStrip();
                menu.Items.Add("TurboDM'i göster", null, (_, _) => ShowFromTray());
                menu.Items.Add("URL ekle…", null, (_, _) => { ShowFromTray(); Add_Click(this, new RoutedEventArgs()); });
                menu.Items.Add("Hepsini durdur", null, (_, _) => _m.PauseAll());
                menu.Items.Add(new WF.ToolStripSeparator());
                menu.Items.Add("Çıkış", null, (_, _) => App.ExitApp());
                _tray.ContextMenuStrip = menu;
                _tray.MouseClick += (_, a) => { if (a.Button == WF.MouseButtons.Left) ShowFromTray(); };
            }
            catch { _tray = null; }
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (App.Settings.MinimizeToTray && _tray != null)
            {
                e.Cancel = true;
                Hide();
                if (!_trayHintShown)
                {
                    _tray.ShowBalloonTip(2500, "TurboDM arka planda çalışıyor", "Tarayıcıdan gelen indirmeleri yakalamaya devam ediyor. Çıkmak için tepsi simgesine sağ tıklayın.", WF.ToolTipIcon.Info);
                    _trayHintShown = true;
                }
                return;
            }
            base.OnClosing(e);
            App.ExitApp();
        }

        public void DisposeTray()
        {
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        }
    }

    public class RelayCommand : ICommand
    {
        readonly Action _a;
        public RelayCommand(Action a) => _a = a;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => _a();
    }
}
