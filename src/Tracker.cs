using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ObsRecIndicator
{
    /// <summary>Finds the recording files a tracker is watching.</summary>
    public static class FileMatcher
    {
        /// <summary>All matching files, newest first. Empty if the folder does not exist.</summary>
        public static List<FileInfo> Find(string folder, string[] patterns, bool includeSubfolders)
        {
            var dir = folder == null ? null : new DirectoryInfo(folder);
            if (dir == null || !dir.Exists) return new List<FileInfo>();
            var option = includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            return patterns
                .SelectMany(p => dir.EnumerateFiles(p, option))
                .GroupBy(f => f.FullName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderByDescending(f => f.CreationTimeUtc)
                .ThenByDescending(f => f.LastWriteTimeUtc)
                .ToList();
        }
    }

    /// <summary>One watched recording (one Source Record output) and its tray icon.</summary>
    class Tracker : IDisposable
    {
        readonly TrackerConfig _cfg;
        readonly Config _global;
        readonly string _folder;
        readonly NotifyIcon _notifyIcon;
        readonly Icon _activeIcon;
        readonly Icon _idleIcon;
        readonly ToolStripMenuItem _statusItem;

        string _currentPath;
        DateTime _currentCreatedUtc;
        long _lastLength = -1;
        DateTime _lastGrowthUtc = DateTime.MinValue;
        bool? _active;
        string _status = "starting";

        public bool IsActive { get { return _active == true; } }

        public Tracker(TrackerConfig cfg, Config global, Bitmap baseImg, int iconSize,
            Action openSettings, Action reload, Action exit)
        {
            _cfg = cfg;
            _global = global;
            _folder = global.ResolvedFolder(cfg);

            using (var overlay = IconRenderer.LoadImage(global.Resolve(cfg.Overlay), iconSize))
            {
                var overlayText = overlay == null ? cfg.BadgeText : null;
                using (var a = IconRenderer.Compose(iconSize, baseImg, true, global.IndicatorColor, overlay, overlayText, cfg.OverlayColor))
                using (var i = IconRenderer.Compose(iconSize, baseImg, false, global.IndicatorColor, overlay, overlayText, cfg.OverlayColor))
                {
                    _activeIcon = Icon.FromHandle(a.GetHicon());
                    _idleIcon = Icon.FromHandle(i.GetHicon());
                }
            }

            _statusItem = new ToolStripMenuItem { Enabled = false };
            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open recording folder", null, (s, e) => OpenFolder());
            menu.Items.Add("Settings…", null, (s, e) => openSettings());
            menu.Items.Add("Reload", null, (s, e) => reload());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => exit());

            _notifyIcon = new NotifyIcon { Icon = _idleIcon, ContextMenuStrip = menu, Visible = false };
            _notifyIcon.DoubleClick += (s, e) => OpenFolder();
        }

        public void Poll(DateTime nowUtc)
        {
            try
            {
                var newest = FileMatcher.Find(_folder, _cfg.Patterns, _global.ResolvedIncludeSubfolders(_cfg)).FirstOrDefault();
                if (newest == null)
                {
                    _currentPath = null;
                    _lastLength = -1;
                    _status = Directory.Exists(_folder) ? "no matching file" : "folder not found";
                }
                else
                {
                    long len = GetLiveLength(newest);
                    if (!string.Equals(newest.FullName, _currentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        // New file: take a baseline; growth is detected on the next poll.
                        _currentPath = newest.FullName;
                        _currentCreatedUtc = newest.CreationTimeUtc;
                        _lastLength = len;
                    }
                    else if (len != _lastLength)
                    {
                        if (len > _lastLength) _lastGrowthUtc = nowUtc;
                        _lastLength = len;
                    }
                    _status = "idle";
                }
            }
            catch (Exception ex)
            {
                _status = "error: " + ex.Message;
            }

            bool active = _currentPath != null && (nowUtc - _lastGrowthUtc).TotalSeconds < _global.InactiveAfterSeconds;
            if (active)
            {
                var elapsed = nowUtc - _currentCreatedUtc;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                _status = "REC " + ((int)elapsed.TotalHours).ToString("00") + elapsed.ToString(@"\:mm\:ss");
            }

            if (_active != active)
            {
                _active = active;
                _notifyIcon.Icon = active ? _activeIcon : _idleIcon;
                _notifyIcon.Visible = active || !_global.HideWhenIdle;
            }

            var tip = _cfg.Name + ": " + _status;
            if (_currentPath != null) tip += "\n" + Path.GetFileName(_currentPath);
            _notifyIcon.Text = Program.TrimTooltip(tip);
            _statusItem.Text = _cfg.Name + " — " + _status;
        }

        /// <summary>
        /// Directory listings can report a stale size for a file that another process is
        /// still writing, so ask the file itself via an open handle.
        /// </summary>
        static long GetLiveLength(FileInfo file)
        {
            try
            {
                using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                    return fs.Length;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            file.Refresh();
            return file.Length;
        }

        void OpenFolder()
        {
            try
            {
                if (_currentPath != null && File.Exists(_currentPath))
                    Process.Start("explorer.exe", "/select,\"" + _currentPath + "\"");
                else if (Directory.Exists(_folder))
                    Process.Start("explorer.exe", "\"" + _folder + "\"");
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            IconRenderer.DestroyIcon(_activeIcon.Handle);
            IconRenderer.DestroyIcon(_idleIcon.Handle);
            _activeIcon.Dispose();
            _idleIcon.Dispose();
        }
    }
}
