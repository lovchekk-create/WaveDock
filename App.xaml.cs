using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace WaveDock
{
    public partial class App : Application
    {
        static Mutex _mutex;
        public static bool Exiting;
        MediaService _media;
        MiniWindow _mini;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _mutex = new Mutex(true, "WaveDock.SingleInstance", out bool created);
            if (!created) { Log("already running - exit"); Shutdown(); return; }

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DispatcherUnhandledException += (s, a) => { Log(a.Exception); a.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Log("FATAL " + a.ExceptionObject);
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, a) => { Log(a.Exception); a.SetObserved(); };
            Log("start v1.3");

            _media = new MediaService(Dispatcher);
            try { new AudioService(Dispatcher); } catch (Exception ex) { Log(ex); }
            WaveView.AudioLevel = () => AudioService.Instance?.Level ?? 0;
            try
            {
                _mini = new MiniWindow(_media);
                _mini.Show();
                Log("mini shown");
            }
            catch (Exception ex)
            {
                Log(ex);
                MessageBox.Show("WaveDock не смог запуститься:\n" + ex.Message, "WaveDock");
                Shutdown();
                return;
            }

            try { new DiscordPresence(_media); } catch (Exception ex) { Log(ex); }

            try { await _media.InitAsync(); }
            catch (Exception ex)
            {
                Log(ex);
                MessageBox.Show("Не удалось подключиться к медиа-сессиям Windows:\n" + ex.Message, "WaveDock");
            }
        }

        public static void Log(Exception ex) => Log(ex.ToString());

        /// <summary>Пишем и в %AppData%\WaveDock, и рядом с программой — так проще найти.</summary>
        public static void Log(string text)
        {
            string line = DateTime.Now + "  " + text + Environment.NewLine;
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.AppendAllText(Path.Combine(Settings.Folder, "error.log"), line);
            }
            catch { }
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "wavedock.log"), line); }
            catch { }
        }
    }
}
