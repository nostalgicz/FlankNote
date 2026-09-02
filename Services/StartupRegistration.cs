using Microsoft.Win32;

namespace FlankNote;

/// <summary>Per-user Windows sign-in registration; no elevation required.</summary>
static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return HasValue(key, AppIdentity.StartupValueName);
            }
            catch (Exception ex)
            {
                App.ReportError($"Startup registration read failed: {ex}");
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (!enabled)
            {
                key.DeleteValue(AppIdentity.StartupValueName, throwOnMissingValue: false);
                return;
            }

            var executable = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                key.SetValue(AppIdentity.StartupValueName, $"\"{executable}\"");
            }
        }
        catch (Exception ex)
        {
            // Registry policy can deny writes; keep the app usable and surface
            // the failure through the debug log and tray notification.
            App.ReportError($"Startup registration write failed: {ex}");
        }
    }

    static bool HasValue(RegistryKey? key, string name) =>
        key?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value);
}
