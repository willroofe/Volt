namespace Volt;

public sealed class LanguageManager
{
    private readonly List<ILanguageService> _services = [];
    private Dictionary<string, ILanguageService> _extensionMap = new(StringComparer.OrdinalIgnoreCase);

    public LanguageManager()
    {
        Register(new JsonLanguageService());
    }

    public void Initialize()
    {
        RebuildExtensionMap();
    }

    public void Register(ILanguageService service)
    {
        _services.Add(service);
        RebuildExtensionMap();
    }

    public ILanguageService? GetService(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        return _extensionMap.TryGetValue(extension, out ILanguageService? service)
            ? service
            : null;
    }

    public ILanguageService? GetServiceByName(string name) =>
        _services.FirstOrDefault(service =>
            string.Equals(service.Name, name, StringComparison.OrdinalIgnoreCase));

    public List<string> GetAvailableLanguages() =>
        _services.Select(service => service.Name).Distinct().OrderBy(name => name).ToList();

    private void RebuildExtensionMap()
    {
        var map = new Dictionary<string, ILanguageService>(StringComparer.OrdinalIgnoreCase);
        foreach (ILanguageService service in _services)
        {
            foreach (string extension in service.Extensions)
                map.TryAdd(extension, service);
        }

        _extensionMap = map;
    }
}
