using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ObsRecIndicator
{
    /// <summary>The per-user "Run" registry value; the installer writes the same one.</summary>
    public static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "ObsRecIndicator";

        public static bool IsEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(ValueName) != null;
        }

        public static void Set(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled)
                    key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
                else if (key.GetValue(ValueName) != null)
                    key.DeleteValue(ValueName);
            }
        }
    }
}
