using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ObsRecIndicator
{
    public static class Program
    {
        public const string AppName = "OBS Rec Indicator";
        const string MutexName = @"Local\ObsRecIndicator.Instance";
        const string ReloadEventName = @"Local\ObsRecIndicator.Reload";
        const string QuitEventName = @"Local\ObsRecIndicator.Quit";

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var flags = args.Where(a => a.StartsWith("--")).Select(a => a.ToLowerInvariant()).ToList();
            var positional = args.Where(a => !a.StartsWith("--")).ToList();

            if (flags.Contains("--make-ico"))
            {
                IconRenderer.WriteIco(positional.Count > 0 ? positional[0] : "app.ico",
                    new[] { 16, 20, 24, 32, 40, 48, 64, 256 }, IconRenderer.AppIcon);
                return 0;
            }

            var configPath = Config.DefaultPath(positional.FirstOrDefault());

            if (flags.Contains("--render-icons"))
                return RenderIcons(configPath, positional.Count > 1 ? positional[1] : "icon-preview");

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    SignalRunningInstance(flags);
                    return 0;
                }
                if (flags.Contains("--quit") || flags.Contains("--reload"))
                    return 0; // Nothing running to signal.

                bool firstRun = !File.Exists(configPath);
                string loadError = null;
                Config cfg;
                if (firstRun)
                {
                    cfg = Config.CreateDefault(configPath);
                    try { cfg.Save(configPath); }
                    catch (Exception) { }
                }
                else
                {
                    try { cfg = Config.Load(configPath); }
                    catch (Exception ex)
                    {
                        loadError = ex.Message;
                        cfg = Config.CreateDefault(configPath);
                    }
                }

                bool restart;
                using (var app = new IndicatorApp(cfg, firstRun, loadError))
                {
                    Application.Run(app);
                    restart = app.RestartRequested;
                }
                mutex.ReleaseMutex();

                if (restart)
                    Process.Start(Application.ExecutablePath, string.Join(" ", args.Select(a => "\"" + a + "\"")));
            }
            return 0;
        }

        /// <summary>NotifyIcon tooltips are limited to 63 characters.</summary>
        public static string TrimTooltip(string tip)
        {
            return tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
        }

        static void SignalRunningInstance(List<string> flags)
        {
            string eventName;
            if (flags.Contains("--quit")) eventName = QuitEventName;
            else if (flags.Contains("--reload")) eventName = ReloadEventName;
            else
            {
                var answer = MessageBox.Show(
                    AppName + " is already running.\n\nYes = reload config\nNo = quit it\nCancel = leave it running",
                    AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes) eventName = ReloadEventName;
                else if (answer == DialogResult.No) eventName = QuitEventName;
                else return;
            }
            EventWaitHandle handle;
            if (EventWaitHandle.TryOpenExisting(eventName, out handle))
                using (handle) handle.Set();
        }

        /// <summary>Debug aid: writes the active/idle icons of every tracker as PNGs.</summary>
        static int RenderIcons(string configPath, string outDir)
        {
            var cfg = Config.Load(configPath);
            Directory.CreateDirectory(outDir);
            foreach (var size in new[] { 16, 20, 24, 32 })
            {
                using (var baseImg = IconRenderer.LoadImage(cfg.Resolve(cfg.BaseIcon), size) ?? IconRenderer.FallbackBase(size))
                {
                    using (var main = IconRenderer.Compose(size, baseImg, false, cfg.IndicatorColor, null, null, Color.Empty))
                        main.Save(Path.Combine(outDir, "_main-" + size + ".png"), ImageFormat.Png);
                    foreach (var t in cfg.Trackers)
                        using (var overlay = IconRenderer.LoadImage(cfg.Resolve(t.Overlay), size))
                            foreach (var active in new[] { true, false })
                                using (var bmp = IconRenderer.Compose(size, baseImg, active, cfg.IndicatorColor, overlay,
                                    overlay == null ? t.BadgeText : null, t.OverlayColor))
                                {
                                    var name = string.Join("_", t.Name.Split(Path.GetInvalidFileNameChars()));
                                    bmp.Save(Path.Combine(outDir, name + "-" + size + (active ? "-active" : "-idle") + ".png"), ImageFormat.Png);
                                }
                }
            }
            return 0;
        }

        class IndicatorApp : ApplicationContext
        {
            readonly Config _cfg;
            readonly string _loadError;
            readonly NotifyIcon _mainIcon;
            readonly Icon _mainIconImage;
            readonly List<Tracker> _trackers = new List<Tracker>();
            readonly System.Windows.Forms.Timer _timer;
            readonly EventWaitHandle _reloadEvent;
            readonly EventWaitHandle _quitEvent;
            SettingsForm _settings;

            public bool RestartRequested { get; private set; }

            public IndicatorApp(Config cfg, bool firstRun, string loadError)
            {
                _cfg = cfg;
                _loadError = loadError;
                _reloadEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ReloadEventName);
                _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);

                int size = SystemInformation.SmallIconSize.Width;
                using (var baseImg = IconRenderer.LoadImage(cfg.Resolve(cfg.BaseIcon), size) ?? IconRenderer.FallbackBase(size))
                {
                    // The main icon is created first so it always gets the same icon id,
                    // which keeps Windows' "show on taskbar" choice for it.
                    using (var bmp = IconRenderer.Compose(size, baseImg, false, cfg.IndicatorColor, null, null, Color.Empty))
                        _mainIconImage = Icon.FromHandle(bmp.GetHicon());
                    _mainIcon = new NotifyIcon { Icon = _mainIconImage, ContextMenuStrip = BuildMainMenu(), Text = AppName, Visible = true };
                    _mainIcon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenSettings(); };

                    foreach (var tc in cfg.Trackers)
                        _trackers.Add(new Tracker(tc, cfg, baseImg, size, OpenSettings, Reload, ExitThread));
                }

                _timer = new System.Windows.Forms.Timer { Interval = cfg.PollIntervalMs };
                _timer.Tick += (s, e) => Tick();
                _timer.Start();
                Tick();

                if (loadError != null)
                {
                    var answer = MessageBox.Show(
                        "Could not load the config file:\n\n" + loadError + "\n\n" + cfg.ConfigPath +
                        "\n\nOpen it in Notepad to fix it? (Choose Reload from the tray icon afterwards.)",
                        AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (answer == DialogResult.Yes) EditConfigFile();
                }
                else if (firstRun)
                {
                    OpenSettings();
                }
            }

            ContextMenuStrip BuildMainMenu()
            {
                var menu = new ContextMenuStrip();
                menu.Items.Add("Settings…", null, (s, e) => OpenSettings()).Font =
                    new Font(menu.Font, FontStyle.Bold);
                menu.Items.Add("Open config folder", null, (s, e) => OpenConfigFolder());
                menu.Items.Add("Reload", null, (s, e) => Reload());
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Exit", null, (s, e) => ExitThread());
                return menu;
            }

            void Tick()
            {
                if (_quitEvent.WaitOne(0)) { ExitThread(); return; }
                if (_reloadEvent.WaitOne(0)) { Reload(); return; }

                var now = DateTime.UtcNow;
                foreach (var t in _trackers)
                    t.Poll(now);

                string status;
                int recording = _trackers.Count(t => t.IsActive);
                if (_loadError != null) status = "config error";
                else if (_trackers.Count == 0) status = "click to set up";
                else if (recording == 0) status = "not recording";
                else status = recording + " recording";
                _mainIcon.Text = TrimTooltip(AppName + " — " + status);
            }

            void OpenSettings()
            {
                if (_settings != null && !_settings.IsDisposed)
                {
                    if (_settings.WindowState == FormWindowState.Minimized)
                        _settings.WindowState = FormWindowState.Normal;
                    _settings.Activate();
                    return;
                }
                _settings = new SettingsForm(_cfg, _loadError != null);
                _settings.Saved += (s, e) => Reload();
                _settings.FormClosed += (s, e) => _settings = null;
                _settings.Show();
                _settings.Activate();
            }

            void EditConfigFile()
            {
                try { Process.Start("notepad.exe", "\"" + _cfg.ConfigPath + "\""); }
                catch (Exception) { }
            }

            void OpenConfigFolder()
            {
                try { Process.Start("explorer.exe", "/select,\"" + _cfg.ConfigPath + "\""); }
                catch (Exception) { }
            }

            void Reload()
            {
                // Validate first so a broken edit doesn't kill the running instance.
                try { Config.Load(_cfg.ConfigPath); }
                catch (Exception ex)
                {
                    MessageBox.Show("Config has errors, keeping the current settings:\n\n" + ex.Message,
                        AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                RestartRequested = true;
                ExitThread();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _timer.Dispose();
                    if (_settings != null) _settings.Dispose();
                    foreach (var t in _trackers) t.Dispose();
                    _mainIcon.Visible = false;
                    _mainIcon.Dispose();
                    IconRenderer.DestroyIcon(_mainIconImage.Handle);
                    _mainIconImage.Dispose();
                    _reloadEvent.Dispose();
                    _quitEvent.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
