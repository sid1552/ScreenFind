using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ScreenFind
{
    public class Settings
    {
        public bool EnhanceOcr { get; set; } = false;
        public bool UsePaddleOcr { get; set; } = false;
        public bool DragToSelect { get; set; } = true;

        // Monitor exclusion — stores DeviceNames of unchecked monitors
        // Empty list = all monitors captured (new monitors auto-included)
        public List<string> ExcludedMonitors { get; set; } = new();

        // Hotkey config — defaults to Ctrl+Shift (0x0006) + F (0x46)
        public uint HotkeyModifiers { get; set; } = 0x0006;
        public uint HotkeyKey { get; set; } = 0x46;

        private static readonly string SettingsDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenFind");

        private static readonly string SettingsPath =
            Path.Combine(SettingsDir, "settings.json");

        public static Settings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
                }
            }
            catch { /* corrupt file → return defaults */ }

            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json);
            }
            catch { /* best effort */ }
        }
    }
}
