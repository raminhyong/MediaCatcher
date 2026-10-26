using System.Text.Json;
using System.Text.Json.Serialization;
using MediaCatcher.Core.Utils;

namespace MediaCatcher.Core.Storage;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly string _settingsPath;
    private AppSettings _current;

    public SettingsService(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? new AppPaths().SettingsFile;
        _current = new AppSettings();
    }

    public event EventHandler<AppSettings>? SettingsChanged;

    public string SettingsPath => _settingsPath;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current.Clone();
            }
        }
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        AppSettings loaded;
        try
        {
            if (!File.Exists(_settingsPath))
            {
                loaded = new AppSettings();
            }
            else
            {
                var json = await File.ReadAllTextAsync(_settingsPath, cancellationToken).ConfigureAwait(false);
                loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
            }
        }
        catch (JsonException)
        {
            loaded = new AppSettings();
        }
        catch (IOException)
        {
            loaded = new AppSettings();
        }

        loaded = Migrate(loaded);
        Normalize(loaded);

        lock (_gate)
        {
            _current = loaded;
        }

        return loaded.Clone();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = Migrate(settings.Clone());
        Normalize(normalized);
        var json = JsonSerializer.Serialize(normalized, SerializerOptions);
        await AtomicFile.WriteAllTextAsync(_settingsPath, json, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _current = normalized;
        }

        SettingsChanged?.Invoke(this, normalized.Clone());
    }

    private static AppSettings Migrate(AppSettings settings)
    {
        if (settings.SchemaVersion < 1)
        {
            settings.SchemaVersion = 1;
        }

        return settings;
    }

    public static void Normalize(AppSettings settings)
    {
        settings.Connections = Math.Clamp(settings.Connections, 1, 32);
        settings.MaxConcurrentDownloads = Math.Clamp(settings.MaxConcurrentDownloads, 1, 20);
        settings.PerHostLimit = Math.Clamp(settings.PerHostLimit, 1, 10);
        settings.SpeedLimitBps = Math.Max(0, settings.SpeedLimitBps);
        settings.SubtitleLanguages = string.IsNullOrWhiteSpace(settings.SubtitleLanguages)
            ? "th,en"
            : settings.SubtitleLanguages.Trim();

        if (string.IsNullOrWhiteSpace(settings.DownloadRoot))
        {
            settings.DownloadRoot = AppPaths.DefaultDownloadRoot;
        }

        settings.DownloadRoot = Path.GetFullPath(settings.DownloadRoot);

        if (string.IsNullOrWhiteSpace(settings.Theme))
        {
            settings.Theme = "Dark";
        }

        if (string.IsNullOrWhiteSpace(settings.Language))
        {
            settings.Language = "en";
        }

        foreach (var category in Models.MediaCategories.All)
        {
            if (!settings.CategoryFolders.ContainsKey(category) ||
                string.IsNullOrWhiteSpace(settings.CategoryFolders[category]))
            {
                settings.CategoryFolders[category] = category;
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.ProxyUrl) &&
            !Uri.TryCreate(settings.ProxyUrl, UriKind.Absolute, out _))
        {
            settings.ProxyUrl = null;
        }
    }
}
