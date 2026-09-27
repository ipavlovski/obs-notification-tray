using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ObsRecIndicator
{
    /// <summary>
    /// One watched recording. Values are kept as written in the config file
    /// (unexpanded, possibly relative); <see cref="Config"/> resolves them.
    /// </summary>
    public class TrackerConfig
    {
        public string Name = "";
        public string Pattern = "";
        public string Folder;               // null/empty = use Config.WatchFolder
        public bool? IncludeSubfolders;     // null = use Config.IncludeSubfolders
        public string Overlay;              // image path; null = text badge
        public string OverlayText;          // null = first letter of Name
        public Color OverlayColor = Config.DefaultOverlayColor;

        public string[] Patterns
        {
            get { return (Pattern ?? "").Split(';').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray(); }
        }

        public string BadgeText
        {
            get
            {
                if (!string.IsNullOrEmpty(OverlayText)) return OverlayText;
                var n = (Name ?? "").Trim();
                return n.Length > 0 ? n.Substring(0, 1).ToUpperInvariant() : "?";
            }
        }

        public TrackerConfig Clone()
        {
            return (TrackerConfig)MemberwiseClone();
        }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name;
        }
    }

    public class Config
    {
        public const string DefaultObsExe = @"%ProgramFiles%\obs-studio\bin\64bit\obs64.exe";
        public const string DefaultWatchFolder = @"%USERPROFILE%\Videos";
        public static readonly Color DefaultIndicatorColor = Color.FromArgb(0xE8, 0x11, 0x23);
        public static readonly Color DefaultOverlayColor = Color.FromArgb(0x2D, 0x7D, 0xE0);

        public string ConfigPath;
        public string WatchFolder = DefaultWatchFolder;
        public bool IncludeSubfolders;
        public int PollIntervalMs = 1000;
        public double InactiveAfterSeconds = 5;
        public string IdleBehavior = "hide";   // "hide" | "grey"
        public string BaseIcon = DefaultObsExe;
        public Color IndicatorColor = DefaultIndicatorColor;
        public List<TrackerConfig> Trackers = new List<TrackerConfig>();

        public bool HideWhenIdle
        {
            get { return !string.Equals(IdleBehavior, "grey", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// The config file to use: the given argument, else config.json next to the exe
        /// (portable use), else %APPDATA%\ObsRecIndicator\config.json.
        /// </summary>
        public static string DefaultPath(string argument)
        {
            if (!string.IsNullOrWhiteSpace(argument))
                return Path.GetFullPath(argument);
            var portable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
            if (File.Exists(portable))
                return portable;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ObsRecIndicator", "config.json");
        }

        public static Config CreateDefault(string path)
        {
            return new Config { ConfigPath = Path.GetFullPath(path) };
        }

        public Config Clone()
        {
            var c = (Config)MemberwiseClone();
            c.Trackers = Trackers.Select(t => t.Clone()).ToList();
            return c;
        }

        // ---- Resolving raw values ------------------------------------------------

        string BaseDir
        {
            get { return ConfigPath != null ? Path.GetDirectoryName(ConfigPath) : AppDomain.CurrentDomain.BaseDirectory; }
        }

        /// <summary>Expands environment variables and makes the path absolute relative to the config file.</summary>
        public string Resolve(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var p = Environment.ExpandEnvironmentVariables(raw.Trim());
            try { return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(BaseDir, p)); }
            catch (Exception) { return p; }
        }

        public string ResolvedFolder(TrackerConfig t)
        {
            return Resolve(string.IsNullOrWhiteSpace(t.Folder) ? WatchFolder : t.Folder);
        }

        public bool ResolvedIncludeSubfolders(TrackerConfig t)
        {
            return t.IncludeSubfolders ?? IncludeSubfolders;
        }

        // ---- Loading -------------------------------------------------------------

        public static Config Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Config file not found: " + path);

            // Allow whole-line // comments in the config file.
            var lines = File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//"));
            var root = new JavaScriptSerializer().DeserializeObject(string.Join("\n", lines)) as Dictionary<string, object>;
            if (root == null)
                throw new FormatException("Config root must be a JSON object.");

            var cfg = new Config();
            cfg.ConfigPath = Path.GetFullPath(path);
            cfg.WatchFolder = GetString(root, "watchFolder", null);
            cfg.IncludeSubfolders = GetBool(root, "includeSubfolders") ?? false;
            cfg.PollIntervalMs = Math.Max(200, GetInt(root, "pollIntervalMs", 1000));
            cfg.InactiveAfterSeconds = Math.Max(1, GetDouble(root, "inactiveAfterSeconds", 5));
            cfg.IdleBehavior = GetString(root, "idleBehavior", "hide");
            cfg.BaseIcon = GetString(root, "baseIcon", DefaultObsExe);
            cfg.IndicatorColor = GetColor(root, "indicatorColor", DefaultIndicatorColor);

            object trackersObj;
            var trackers = root.TryGetValue("trackers", out trackersObj) ? trackersObj as IEnumerable : null;
            if (trackersObj != null && trackers == null)
                throw new FormatException("\"trackers\" must be an array.");

            foreach (var item in trackers ?? new object[0])
            {
                var t = item as Dictionary<string, object>;
                if (t == null)
                    throw new FormatException("Each entry in \"trackers\" must be an object.");

                var tc = new TrackerConfig();
                tc.Name = GetString(t, "name", "");
                tc.Pattern = GetString(t, "pattern", "");
                tc.Folder = GetString(t, "folder", null);
                tc.IncludeSubfolders = GetBool(t, "includeSubfolders");
                tc.Overlay = GetString(t, "overlay", null);
                tc.OverlayText = GetString(t, "overlayText", null);
                tc.OverlayColor = GetColor(t, "overlayColor", DefaultOverlayColor);
                cfg.Trackers.Add(tc);
            }

            var error = cfg.Validate();
            if (error != null)
                throw new FormatException(error);
            return cfg;
        }

        /// <summary>Returns a description of the first problem, or null if the config is usable.</summary>
        public string Validate()
        {
            for (int i = 0; i < Trackers.Count; i++)
            {
                var t = Trackers[i];
                var label = string.IsNullOrWhiteSpace(t.Name) ? "Tracker #" + (i + 1) : "Tracker \"" + t.Name + "\"";
                if (string.IsNullOrWhiteSpace(t.Name))
                    return label + " needs a name.";
                if (t.Patterns.Length == 0)
                    return label + " needs a file pattern.";
                if (string.IsNullOrWhiteSpace(t.Folder) && string.IsNullOrWhiteSpace(WatchFolder))
                    return label + " has no folder and no watch folder is set.";
            }
            return null;
        }

        // ---- Saving --------------------------------------------------------------

        public void Save(string path)
        {
            var root = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(WatchFolder)) root["watchFolder"] = WatchFolder;
            root["includeSubfolders"] = IncludeSubfolders;
            root["pollIntervalMs"] = PollIntervalMs;
            root["inactiveAfterSeconds"] = InactiveAfterSeconds;
            root["idleBehavior"] = HideWhenIdle ? "hide" : "grey";
            if (!string.IsNullOrWhiteSpace(BaseIcon)) root["baseIcon"] = BaseIcon;
            root["indicatorColor"] = ToHex(IndicatorColor);

            var trackers = new List<object>();
            foreach (var t in Trackers)
            {
                var d = new Dictionary<string, object>();
                d["name"] = t.Name;
                d["pattern"] = t.Pattern;
                if (!string.IsNullOrWhiteSpace(t.Folder)) d["folder"] = t.Folder;
                if (t.IncludeSubfolders.HasValue) d["includeSubfolders"] = t.IncludeSubfolders.Value;
                if (!string.IsNullOrWhiteSpace(t.Overlay)) d["overlay"] = t.Overlay;
                if (!string.IsNullOrEmpty(t.OverlayText)) d["overlayText"] = t.OverlayText;
                d["overlayColor"] = ToHex(t.OverlayColor);
                trackers.Add(d);
            }
            root["trackers"] = trackers;

            var json = Indent(new JavaScriptSerializer().Serialize(root));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json + Environment.NewLine, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        static string ToHex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        /// <summary>Pretty-prints compact JSON (as produced by JavaScriptSerializer).</summary>
        static string Indent(string json)
        {
            var sb = new StringBuilder();
            int depth = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\') sb.Append(json[++i]);
                    else if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"':
                        inString = true;
                        sb.Append(c);
                        break;
                    case '{':
                    case '[':
                        char close = c == '{' ? '}' : ']';
                        if (i + 1 < json.Length && json[i + 1] == close)
                        {
                            sb.Append(c).Append(close);
                            i++;
                            break;
                        }
                        sb.Append(c).AppendLine().Append(' ', ++depth * 2);
                        break;
                    case '}':
                    case ']':
                        sb.AppendLine().Append(' ', --depth * 2).Append(c);
                        break;
                    case ',':
                        sb.Append(c).AppendLine().Append(' ', depth * 2);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // ---- JSON helpers --------------------------------------------------------

        static string GetString(Dictionary<string, object> d, string key, string def)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : def;
        }

        static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : def;
        }

        static double GetDouble(Dictionary<string, object> d, string key, double def)
        {
            object v;
            return d.TryGetValue(key, out v) && v != null ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : def;
        }

        static bool? GetBool(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is bool ? (bool?)(bool)v : null;
        }

        static Color GetColor(Dictionary<string, object> d, string key, Color def)
        {
            var s = GetString(d, key, null);
            if (string.IsNullOrWhiteSpace(s)) return def;
            try { return ColorTranslator.FromHtml(s); }
            catch (Exception) { throw new FormatException("Invalid color for \"" + key + "\": " + s); }
        }
    }
}
