using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using TurboDM.Core;

namespace TurboDM
{
    public partial class App : Application
    {
        public static Settings Settings { get; private set; } = new();
        public static DownloadManager Manager { get; private set; } = null!;
        public static LocalServer Server { get; private set; } = null!;
        public static MainWindow MainWin { get; private set; } = null!;

        Mutex? _mutex;
        EventWaitHandle? _showSignal;
        const string MutexName = "TurboDM.SingleInstance.v1";
        const string ShowEventName = "TurboDM.ShowWindow.v1";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _mutex = new Mutex(true, MutexName, out bool first);
            if (!first)
            {
                // Zaten çalışıyor: mevcut pencereyi öne getir ve çık
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                Shutdown();
                return;
            }
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(() =>
            {
                while (_showSignal.WaitOne()) Dispatcher.Invoke(() => MainWin?.ShowFromTray());
            }) { IsBackground = true }.Start();

            DispatcherUnhandledException += (_, ex) =>
            {
                MessageBox.Show(ex.Exception.Message, "TurboDM – beklenmeyen hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                ex.Handled = true;
            };

            Settings = Settings.Load();
            Manager = new DownloadManager(Settings, a => Dispatcher.Invoke(a));
            Manager.Load();
            Server = new LocalServer(OnExtensionRequest);

            MainWin = new MainWindow();
            bool startHidden = Array.Exists(e.Args, a => a == "--tray");
            if (!startHidden) MainWin.Show();

            if (!Server.Start(Settings.Port))
                MessageBox.Show($"Tarayıcı köprüsü {Settings.Port} portunda başlatılamadı:\n{Server.LastError}\n\nAyarlar'dan başka bir port seçebilirsiniz.",
                    "TurboDM", MessageBoxButton.OK, MessageBoxImage.Warning);
            MainWin.UpdateServerStatus();
        }

        /// <summary>Eklentiden gelen indirme isteği (arka plan iş parçacığından).</summary>
        Task OnExtensionRequest(DownloadRequest r)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (Settings.ShowAddDialog && !r.Silent)
                {
                    var w = new AddDownloadWindow(r) { Topmost = true };
                    w.Show();
                    w.Activate();
                }
                else
                {
                    var it = Manager.Create(r);
                    Manager.Add(it, true);
                }
            });
            return Task.CompletedTask;
        }

        public static void ApplySettings(Settings s)
        {
            bool portChanged = s.Port != Settings.Port || !Server.Running;
            Settings = s;
            s.Save();
            Manager.ApplySettings(s);
            if (portChanged && !Server.Start(s.Port))
                MessageBox.Show($"Port {s.Port} kullanılamıyor: {Server.LastError}", "TurboDM", MessageBoxButton.OK, MessageBoxImage.Warning);
            MainWin.UpdateServerStatus();
        }

        public static void ExitApp()
        {
            Manager.FlushSync();
            Server.Stop();
            MainWin.DisposeTray();
            Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _mutex?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }
    }
}
