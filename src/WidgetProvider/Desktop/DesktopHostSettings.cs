using System.IO;
using System.Text.Json;

namespace CustomWidgets.WidgetProvider.Desktop;

/// <summary>
/// Persists the desktop host position in local app data. The values are WPF
/// device-independent pixels, so the position survives restarts and DPI changes.
/// </summary>
internal sealed class DesktopHostSettings
{
    public double Left { get; init; }

    public double Top { get; init; }

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CustomWidgets",
        "desktop-host.json");

    public static DesktopHostSettings? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return null;
            }

            DesktopHostSettings? settings = JsonSerializer.Deserialize<DesktopHostSettings>(
                File.ReadAllText(SettingsPath));

            if (settings is null ||
                !double.IsFinite(settings.Left) ||
                !double.IsFinite(settings.Top))
            {
                return null;
            }

            return settings;
        }
        catch (Exception ex)
        {
            Log.Warn($"Desktop position could not be loaded: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static void Save(double left, double top)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top))
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(SettingsPath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(
                    new DesktopHostSettings { Left = left, Top = top },
                    new JsonSerializerOptions { WriteIndented = true }));
            Log.Info($"Desktop position saved: {left:0}, {top:0}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Desktop position could not be saved: {ex.GetType().Name}: {ex.Message}");
        }
    }
}