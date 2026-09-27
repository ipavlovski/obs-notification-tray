using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace ObsRecIndicator
{
    /// <summary>Edits every option of the config file. Works on a copy; writes only on Save.</summary>
    public class SettingsForm : Form
    {
        readonly Config _cfg;
        readonly bool _backupOnSave;
        readonly float _scale;
        readonly Timer _refreshTimer = new Timer { Interval = 250 };
        readonly Dictionary<string, Bitmap> _baseCache = new Dictionary<string, Bitmap>();
        bool _loading;

        // General
        TextBox _watchFolder, _baseIcon;
        CheckBox _includeSubfolders, _startWithWindows;
        NumericUpDown _pollInterval, _inactiveAfter;
        ComboBox _idleBehavior;
        Button _indicatorColor;

        // Trackers
        ListBox _list;
        Button _remove, _up, _down;
        TableLayoutPanel _editor;
        TextBox _name, _pattern, _folder, _overlayText, _overlayImage;
        ComboBox _trackerSubfolders;
        RadioButton _useBadge, _useImage;
        Button _overlayColor, _overlayImageBrowse;
        PictureBox _preview;
        Label _matches;

        /// <summary>Raised after the config file was written.</summary>
        public event EventHandler Saved;

        /// <param name="backupOnSave">Keep the existing file as .bak (used when it failed to load).</param>
        public SettingsForm(Config cfg, bool backupOnSave)
        {
            _cfg = cfg.Clone();
            _backupOnSave = backupOnSave;

            using (var g = CreateGraphics()) _scale = g.DpiX / 96f;
            AutoScaleMode = AutoScaleMode.None;
            Font = SystemFonts.MessageBoxFont;
            Text = "OBS Rec Indicator — Settings";
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            MinimumSize = new Size(S(720), S(700));
            Size = new Size(S(780), S(820));
            try
            {
                using (var bmp = IconRenderer.AppIcon(32))
                    Icon = Icon.FromHandle(bmp.GetHicon());
            }
            catch (Exception) { }

            BuildUi();
            LoadGeneral();
            foreach (var t in _cfg.Trackers) _list.Items.Add(t);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            UpdateTrackerEditor();

            _refreshTimer.Tick += (s, e) => { _refreshTimer.Stop(); RefreshPreview(); };
            RefreshPreview();
        }

        int S(int px) { return (int)Math.Round(px * _scale); }

        const int EM_SETCUEBANNER = 0x1501;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        // ---- Layout --------------------------------------------------------------

        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(S(10)) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            root.Controls.Add(BuildGeneral(), 0, 0);
            root.Controls.Add(BuildTrackers(), 0, 1);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, S(6), 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(S(88), 0) };
            var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(S(88), 0) };
            save.Click += (s, e) => SaveAndClose();
            cancel.Click += (s, e) => Close();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            root.Controls.Add(buttons, 0, 2);
            AcceptButton = save;
            CancelButton = cancel;
        }

        Control BuildGeneral()
        {
            var group = new GroupBox { Text = "General", Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(S(8)) };
            var t = NewGrid();
            group.Controls.Add(t);

            _watchFolder = new TextBox { Dock = DockStyle.Fill };
            AddRow(t, "Watch folder:", _watchFolder, BrowseFolderButton(_watchFolder));

            _includeSubfolders = new CheckBox { Text = "Include subfolders", AutoSize = true };
            AddRow(t, null, _includeSubfolders, null);

            _pollInterval = new NumericUpDown { Minimum = 200, Maximum = 10000, Increment = 100, Width = S(90) };
            AddRow(t, "Check every (ms):", _pollInterval, null);

            _inactiveAfter = new NumericUpDown { Minimum = 1, Maximum = 600, DecimalPlaces = 1, Increment = 1, Width = S(90) };
            AddRow(t, "Stopped after (s):", WithHint(_inactiveAfter, "a file that hasn't grown for this long is no longer recording"), null);

            _idleBehavior = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(260) };
            _idleBehavior.Items.AddRange(new object[] { "Hide the icon", "Show a grey icon" });
            AddRow(t, "When not recording:", _idleBehavior, null);

            _baseIcon = new TextBox { Dock = DockStyle.Fill };
            _baseIcon.TextChanged += (s, e) => QueueRefresh();
            AddRow(t, "Base icon:", _baseIcon, BrowseFileButton(_baseIcon, "Icons and programs|*.exe;*.dll;*.ico;*.png|All files|*.*"));

            _indicatorColor = ColorButton(c => { _cfg.IndicatorColor = c; QueueRefresh(); });
            AddRow(t, "Recording dot color:", _indicatorColor, null);

            _startWithWindows = new CheckBox { Text = "Start with Windows", AutoSize = true };
            AddRow(t, null, _startWithWindows, null);
            return group;
        }

        Control BuildTrackers()
        {
            var group = new GroupBox { Text = "Recordings to watch (one tray icon each)", Dock = DockStyle.Fill, Padding = new Padding(S(8)) };
            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(190)));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            group.Controls.Add(split);

            // Left: list + buttons.
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, S(8), 0) };
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _list.SelectedIndexChanged += (s, e) => UpdateTrackerEditor();
            left.Controls.Add(_list, 0, 0);

            var listButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            var add = SmallButton("Add", (s, e) => AddTracker());
            _remove = SmallButton("Remove", (s, e) => RemoveTracker());
            _up = SmallButton("▲", (s, e) => MoveTracker(-1));
            _down = SmallButton("▼", (s, e) => MoveTracker(1));
            listButtons.Controls.AddRange(new Control[] { add, _remove, _up, _down });
            left.Controls.Add(listButtons, 0, 1);
            split.Controls.Add(left, 0, 0);

            // Right: editor.
            _editor = NewGrid();
            _editor.Dock = DockStyle.Fill;
            _editor.AutoSize = false;
            _editor.AutoScroll = true;

            _name = new TextBox { Dock = DockStyle.Fill };
            _name.TextChanged += (s, e) => Edit(t => { t.Name = _name.Text; RefreshListItem(); });
            AddRow(_editor, "Name:", _name, null);

            _pattern = new TextBox { Dock = DockStyle.Fill };
            _pattern.TextChanged += (s, e) => Edit(t => t.Pattern = _pattern.Text);
            AddRow(_editor, "File pattern:", _pattern, null);
            AddRow(_editor, null, Hint("Wildcards; separate several with ;  e.g.  Game *.mkv;Game *.mp4"), null);

            _folder = new TextBox { Dock = DockStyle.Fill };
            _folder.TextChanged += (s, e) => Edit(t => t.Folder = NullIfBlank(_folder.Text));
            _folder.HandleCreated += (s, e) => SendMessage(_folder.Handle, EM_SETCUEBANNER, (IntPtr)1, "(same as watch folder)");
            AddRow(_editor, "Folder:", _folder, BrowseFolderButton(_folder));

            _trackerSubfolders = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(200) };
            _trackerSubfolders.Items.AddRange(new object[] { "Same as General", "Yes", "No" });
            _trackerSubfolders.SelectedIndexChanged += (s, e) => Edit(t =>
                t.IncludeSubfolders = _trackerSubfolders.SelectedIndex == 1 ? true : _trackerSubfolders.SelectedIndex == 2 ? (bool?)false : null);
            AddRow(_editor, "Include subfolders:", _trackerSubfolders, null);

            _useBadge = new RadioButton { Text = "Text badge", AutoSize = true };
            _useImage = new RadioButton { Text = "Image", AutoSize = true };
            _useBadge.CheckedChanged += (s, e) => { if (_useBadge.Checked) Edit(t => t.Overlay = null); UpdateOverlayEnabled(); };
            _useImage.CheckedChanged += (s, e) => { if (_useImage.Checked) Edit(t => t.Overlay = NullIfBlank(_overlayImage.Text) ?? ""); UpdateOverlayEnabled(); };
            AddRow(_editor, "Overlay:", Flow(_useBadge, _useImage), null);

            _overlayText = new TextBox { Width = S(60), MaxLength = 3 };
            _overlayText.TextChanged += (s, e) => Edit(t => t.OverlayText = NullIfBlank(_overlayText.Text));
            _overlayColor = ColorButton(c => Edit(t => t.OverlayColor = c));
            AddRow(_editor, "Badge text / color:", Flow(_overlayText, _overlayColor, Hint("empty = first letter of the name")), null);

            _overlayImage = new TextBox { Dock = DockStyle.Fill };
            _overlayImage.TextChanged += (s, e) => { if (_useImage.Checked) Edit(t => t.Overlay = NullIfBlank(_overlayImage.Text) ?? ""); };
            _overlayImageBrowse = BrowseFileButton(_overlayImage, "Images and programs|*.png;*.ico;*.exe;*.dll;*.bmp;*.jpg|All files|*.*");
            AddRow(_editor, "Overlay image:", _overlayImage, _overlayImageBrowse);

            _preview = new PictureBox { SizeMode = PictureBoxSizeMode.AutoSize, Margin = new Padding(0, S(6), 0, S(6)) };
            AddRow(_editor, "Preview:", _preview, null);

            _matches = new Label { AutoSize = true, MaximumSize = new Size(S(420), 0), ForeColor = SystemColors.GrayText };
            AddRow(_editor, "Files found:", _matches, null);
            _editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // absorbs spare height
            _editor.RowCount++;

            split.Controls.Add(_editor, 1, 0);
            return group;
        }

        TableLayoutPanel NewGrid()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }

        static void AddRow(TableLayoutPanel t, string label, Control field, Control button)
        {
            int row = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            if (label != null)
                t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            if (field.Dock != DockStyle.Fill) field.Anchor = AnchorStyles.Left;
            t.Controls.Add(field, 1, row);
            if (button != null)
                t.Controls.Add(button, 2, row);
            else
                t.SetColumnSpan(field, 2);
        }

        Label Hint(string text)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(S(3), 0, 0, S(4)), Anchor = AnchorStyles.Left };
        }

        Control WithHint(Control c, string hint)
        {
            return Flow(c, Hint(hint));
        }

        static FlowLayoutPanel Flow(params Control[] controls)
        {
            var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            foreach (var c in controls)
            {
                c.Anchor = AnchorStyles.Left;
                f.Controls.Add(c);
            }
            return f;
        }

        Button SmallButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(S(40), 0) };
            b.Click += click;
            return b;
        }

        Button BrowseFolderButton(TextBox target)
        {
            return SmallButton("Browse…", (s, e) =>
            {
                using (var dlg = new FolderBrowserDialog { ShowNewFolderButton = true })
                {
                    var current = _cfg.Resolve(target.Text);
                    if (current != null && Directory.Exists(current)) dlg.SelectedPath = current;
                    if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.SelectedPath;
                }
            });
        }

        Button BrowseFileButton(TextBox target, string filter)
        {
            return SmallButton("Browse…", (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = filter })
                {
                    var current = _cfg.Resolve(target.Text);
                    if (current != null && File.Exists(current))
                    {
                        dlg.InitialDirectory = Path.GetDirectoryName(current);
                        dlg.FileName = Path.GetFileName(current);
                    }
                    if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.FileName;
                }
            });
        }

        Button ColorButton(Action<Color> onPick)
        {
            var b = new Button { Width = S(60), Height = S(24), FlatStyle = FlatStyle.Flat, Text = "" };
            b.Click += (s, e) =>
            {
                using (var dlg = new ColorDialog { Color = b.BackColor, FullOpen = true })
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        b.BackColor = dlg.Color;
                        onPick(dlg.Color);
                    }
            };
            return b;
        }

        // ---- Binding -------------------------------------------------------------

        void LoadGeneral()
        {
            _watchFolder.Text = _cfg.WatchFolder ?? "";
            _includeSubfolders.Checked = _cfg.IncludeSubfolders;
            _pollInterval.Value = Clamp(_cfg.PollIntervalMs, _pollInterval);
            _inactiveAfter.Value = Clamp((decimal)_cfg.InactiveAfterSeconds, _inactiveAfter);
            _idleBehavior.SelectedIndex = _cfg.HideWhenIdle ? 0 : 1;
            _baseIcon.Text = _cfg.BaseIcon ?? "";
            _indicatorColor.BackColor = _cfg.IndicatorColor;
            try { _startWithWindows.Checked = Autostart.IsEnabled(); }
            catch (Exception) { _startWithWindows.Enabled = false; }

            _watchFolder.TextChanged += (s, e) => QueueRefresh();
            _includeSubfolders.CheckedChanged += (s, e) => QueueRefresh();
        }

        void StoreGeneral()
        {
            _cfg.WatchFolder = NullIfBlank(_watchFolder.Text);
            _cfg.IncludeSubfolders = _includeSubfolders.Checked;
            _cfg.PollIntervalMs = (int)_pollInterval.Value;
            _cfg.InactiveAfterSeconds = (double)_inactiveAfter.Value;
            _cfg.IdleBehavior = _idleBehavior.SelectedIndex == 1 ? "grey" : "hide";
            _cfg.BaseIcon = NullIfBlank(_baseIcon.Text);
        }

        static decimal Clamp(decimal v, NumericUpDown n)
        {
            return Math.Min(n.Maximum, Math.Max(n.Minimum, v));
        }

        static string NullIfBlank(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        TrackerConfig Selected
        {
            get { return _list.SelectedItem as TrackerConfig; }
        }

        void Edit(Action<TrackerConfig> change)
        {
            if (_loading || Selected == null) return;
            change(Selected);
            QueueRefresh();
        }

        void RefreshListItem()
        {
            int i = _list.SelectedIndex;
            if (i < 0) return;
            _loading = true;
            _list.Items[i] = _list.Items[i];   // re-reads ToString()
            _loading = false;
        }

        void UpdateTrackerEditor()
        {
            if (_loading) return;
            var t = Selected;
            _editor.Enabled = t != null;
            _remove.Enabled = t != null;
            _up.Enabled = t != null && _list.SelectedIndex > 0;
            _down.Enabled = t != null && _list.SelectedIndex < _list.Items.Count - 1;

            _loading = true;
            _name.Text = t != null ? t.Name : "";
            _pattern.Text = t != null ? t.Pattern : "";
            _folder.Text = t != null ? t.Folder ?? "" : "";
            _trackerSubfolders.SelectedIndex = t == null || !t.IncludeSubfolders.HasValue ? 0 : t.IncludeSubfolders.Value ? 1 : 2;
            bool image = t != null && t.Overlay != null;
            _useImage.Checked = image;
            _useBadge.Checked = !image;
            _overlayImage.Text = t != null ? t.Overlay ?? "" : "";
            _overlayText.Text = t != null ? t.OverlayText ?? "" : "";
            _overlayColor.BackColor = t != null ? t.OverlayColor : SystemColors.Control;
            _loading = false;

            UpdateOverlayEnabled();
            RefreshPreview();
        }

        void UpdateOverlayEnabled()
        {
            _overlayText.Enabled = _overlayColor.Enabled = _useBadge.Checked;
            _overlayImage.Enabled = _overlayImageBrowse.Enabled = _useImage.Checked;
        }

        void AddTracker()
        {
            var t = new TrackerConfig { Name = "Recording " + (_list.Items.Count + 1) };
            _cfg.Trackers.Add(t);
            _list.Items.Add(t);
            _list.SelectedIndex = _list.Items.Count - 1;
            _name.Focus();
            _name.SelectAll();
        }

        void RemoveTracker()
        {
            int i = _list.SelectedIndex;
            if (i < 0) return;
            _cfg.Trackers.RemoveAt(i);
            _list.Items.RemoveAt(i);
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(i, _list.Items.Count - 1);
            else UpdateTrackerEditor();
        }

        void MoveTracker(int delta)
        {
            int i = _list.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= _list.Items.Count) return;
            var t = _cfg.Trackers[i];
            _cfg.Trackers.RemoveAt(i);
            _cfg.Trackers.Insert(j, t);
            _loading = true;
            _list.Items.RemoveAt(i);
            _list.Items.Insert(j, t);
            _loading = false;
            _list.SelectedIndex = j;
        }

        // ---- Preview and file matches --------------------------------------------

        void QueueRefresh()
        {
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        Bitmap BaseImage(int size)
        {
            var key = (_baseIcon.Text ?? "") + "|" + size;
            Bitmap bmp;
            if (!_baseCache.TryGetValue(key, out bmp))
            {
                bmp = IconRenderer.LoadImage(_cfg.Resolve(_baseIcon.Text), size) ?? IconRenderer.FallbackBase(size);
                _baseCache[key] = bmp;
            }
            return bmp;
        }

        void RefreshPreview()
        {
            var t = Selected;
            var old = _preview.Image;
            _preview.Image = t == null ? null : RenderPreview(t);
            if (old != null) old.Dispose();
            _matches.Text = t == null ? "" : DescribeMatches(t);
        }

        /// <summary>Recording and idle icons at 16/24/32 px, on a dark and on a light strip.</summary>
        Bitmap RenderPreview(TrackerConfig t)
        {
            var sizes = new[] { 16, 24, 32 };
            const int gap = 8, rowHeight = 32 + 2 * gap;
            int width = gap + 2 * sizes.Sum(sz => sz + gap) + gap;
            var bmp = new Bitmap(width, rowHeight * 2, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var strips = new[] { Color.FromArgb(0x20, 0x20, 0x20), Color.FromArgb(0xEE, 0xEE, 0xEE) };
                for (int strip = 0; strip < 2; strip++)
                {
                    int y = strip * rowHeight;
                    using (var b = new SolidBrush(strips[strip])) g.FillRectangle(b, 0, y, width, rowHeight);
                    int x = gap;
                    foreach (var active in new[] { true, false })
                    {
                        foreach (var size in sizes)
                        {
                            using (var overlay = t.Overlay != null ? IconRenderer.LoadImage(_cfg.Resolve(t.Overlay), size) : null)
                            using (var icon = IconRenderer.Compose(size, BaseImage(size), active, _cfg.IndicatorColor,
                                overlay, overlay == null ? t.BadgeText : null, t.OverlayColor))
                                g.DrawImageUnscaled(icon, x, y + gap + (32 - size) / 2);
                            x += size + gap;
                        }
                        x += gap;
                    }
                }
            }
            return bmp;
        }

        string DescribeMatches(TrackerConfig t)
        {
            if (t.Patterns.Length == 0) return "Enter a file pattern.";
            var tmp = _cfg.Clone();
            tmp.WatchFolder = NullIfBlank(_watchFolder.Text);
            tmp.IncludeSubfolders = _includeSubfolders.Checked;
            var folder = tmp.ResolvedFolder(t);
            if (folder == null) return "No folder set.";
            if (!Directory.Exists(folder)) return "Folder not found: " + folder;
            try
            {
                var files = FileMatcher.Find(folder, t.Patterns, tmp.ResolvedIncludeSubfolders(t));
                if (files.Count == 0) return "No matching files yet in " + folder;
                return files.Count + (files.Count == 1 ? " file" : " files") + ", newest: " + files[0].Name;
            }
            catch (Exception ex)
            {
                return "Error: " + ex.Message;
            }
        }

        // ---- Saving --------------------------------------------------------------

        void SaveAndClose()
        {
            StoreGeneral();
            var error = _cfg.Validate();
            if (error != null)
            {
                for (int i = 0; i < _cfg.Trackers.Count; i++)
                {
                    var t = _cfg.Trackers[i];
                    if (string.IsNullOrWhiteSpace(t.Name) || t.Patterns.Length == 0 ||
                        (string.IsNullOrWhiteSpace(t.Folder) && _cfg.WatchFolder == null))
                    {
                        _list.SelectedIndex = i;
                        (string.IsNullOrWhiteSpace(t.Name) ? _name : t.Patterns.Length == 0 ? _pattern : (Control)_watchFolder).Focus();
                        break;
                    }
                }
                MessageBox.Show(this, error, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                if (_backupOnSave && File.Exists(_cfg.ConfigPath))
                    File.Copy(_cfg.ConfigPath, _cfg.ConfigPath + ".bak", true);
                _cfg.Save(_cfg.ConfigPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save the config:\n\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                if (_startWithWindows.Enabled && _startWithWindows.Checked != Autostart.IsEnabled())
                    Autostart.Set(_startWithWindows.Checked);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not change the start-with-Windows setting:\n\n" + ex.Message, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            Close();
            if (Saved != null) Saved(this, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refreshTimer.Dispose();
                foreach (var b in _baseCache.Values) b.Dispose();
                if (_preview != null && _preview.Image != null) _preview.Image.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
