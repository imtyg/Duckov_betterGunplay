using System;
using System.IO;
using UnityEngine;

namespace BetterFire.Settings;

internal static class BetterFireSettings
{
    [Serializable]
    public sealed class Data
    {
        public bool InterruptReload = true;
        public bool InterruptUseItem = true;
        public bool InterruptInteract = true;
        public bool ResumeReloadAfterDash = true;
        public bool AutoReloadOnEmpty = true;
        public bool DashInputBuffer = true;
        public bool AutoSwitchFromUnarmed = true;
        public bool DebugLogs = false;
    }

    private static readonly string SettingsPath =
        Path.Combine(Application.persistentDataPath, "BetterFireSettings.json");

    private static Data _data = new();
    private static bool _loaded;

    public static event Action<Data>? OnSettingsChanged;

    public static Data Current => _data;

    public static void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;

        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonUtility.FromJson<Data>(json);
                if (loaded != null)
                {
                    _data = loaded;
                    OnSettingsChanged?.Invoke(_data);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire][Settings] Failed to load settings: {ex.Message}");
        }

        _data = new Data();
        Persist();
    }

    public static void ResetToDefaults()
    {
        _data = new Data();
        _loaded = true;
        Persist();
    }

    public static void SetInterruptReload(bool value) => UpdateIfChanged(ref _data.InterruptReload, value);
    public static void SetInterruptUseItem(bool value) => UpdateIfChanged(ref _data.InterruptUseItem, value);
    public static void SetInterruptInteract(bool value) => UpdateIfChanged(ref _data.InterruptInteract, value);
    public static void SetResumeReloadAfterDash(bool value) => UpdateIfChanged(ref _data.ResumeReloadAfterDash, value);
    public static void SetAutoReloadOnEmpty(bool value) => UpdateIfChanged(ref _data.AutoReloadOnEmpty, value);
    public static void SetDashInputBuffer(bool value) => UpdateIfChanged(ref _data.DashInputBuffer, value);
    public static void SetAutoSwitchFromUnarmed(bool value) => UpdateIfChanged(ref _data.AutoSwitchFromUnarmed, value);
    public static void SetDebugLogs(bool value) => UpdateIfChanged(ref _data.DebugLogs, value);

    private static void UpdateIfChanged(ref bool field, bool value)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        Persist();
    }

    private static void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonUtility.ToJson(_data, true);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BetterFire][Settings] Failed to save settings: {ex.Message}");
        }

        OnSettingsChanged?.Invoke(_data);
    }
}
