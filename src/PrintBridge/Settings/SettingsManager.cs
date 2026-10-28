using System.Text.Json;
using System.Text.Json.Serialization;
using PrintBridge.Models;

namespace PrintBridge.Settings;

/// <summary>
/// Loads and saves application configuration to disk.
/// Automatically migrates and falls back gracefully.
/// </summary>
public sealed class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private AppSettings _current;

    public SettingsManager(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            _filePath = customPath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            _filePath = Path.Combine(appData, "PrintBridge", "settings.json");
        }

        _current = Load();
    }

    public string ConfigFilePath => _filePath;

    public AppSettings Current
    {
        get { lock (_lock) return _current; }
    }

    public AppSettings Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    _current = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
                }
                else
                {
                    _current = new AppSettings();
                }
            }
            catch
            {
                _current = new AppSettings();
            }

            return _current;
        }
    }

    public void Save(AppSettings? settings = null)
    {
        lock (_lock)
        {
            if (settings != null) _current = settings;

            try
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_current, JsonOpts);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }
    }
}
