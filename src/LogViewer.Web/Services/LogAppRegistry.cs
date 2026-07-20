using System.Text.Json;
using LogViewer.Web.Models;

namespace LogViewer.Web.Services;

/// <summary>
/// Registered applications are added from the Admin page (not appsettings.json)
/// and persisted to a single JSON file, App_Data/applications.json. This is
/// still "no database" per the PRD - it's a flat file the app owns, read
/// once at startup and rewritten whenever an app is added or removed.
/// </summary>
public class LogAppRegistry
{
    private readonly string _storePath;
    private readonly object _lock = new();
    private readonly ILogger<LogAppRegistry> _logger;
    private List<LogApplicationConfig> _apps;

    public LogAppRegistry(IWebHostEnvironment env, ILogger<LogAppRegistry> logger)
    {
        _logger = logger;
        var dataDir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDir);
        _storePath = Path.Combine(dataDir, "applications.json");
        _apps = Load();
    }

    public IReadOnlyList<LogApplicationConfig> All
    {
        get { lock (_lock) { return _apps.ToList(); } }
    }

    public LogApplicationConfig? Get(string name)
    {
        lock (_lock)
        {
            return _apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    public (bool Success, string? Error) Add(string name, List<string> rootPaths)
    {
        name = (name ?? "").Trim();
        var paths = (rootPaths ?? new List<string>())
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (name.Length == 0) return (false, "Application name is required.");
        if (paths.Count == 0) return (false, "At least one log folder path is required.");

        lock (_lock)
        {
            if (_apps.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, $"An application named '{name}' is already registered.");
            }

            _apps.Add(new LogApplicationConfig { Name = name, RootPaths = paths });
            Save();
        }

        return (true, null);
    }

    public bool Remove(string name)
    {
        lock (_lock)
        {
            var removed = _apps.RemoveAll(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) Save();
            return removed;
        }
    }

    private List<LogApplicationConfig> Load()
    {
        if (!File.Exists(_storePath)) return new List<LogApplicationConfig>();

        try
        {
            var json = File.ReadAllText(_storePath);
            return JsonSerializer.Deserialize<List<LogApplicationConfig>>(json) ?? new List<LogApplicationConfig>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read {Path} - starting with no registered applications", _storePath);
            return new List<LogApplicationConfig>();
        }
    }

    // Caller must hold _lock.
    private void Save()
    {
        var json = JsonSerializer.Serialize(_apps, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_storePath, json);
    }
}
