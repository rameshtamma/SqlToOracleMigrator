using System.Text.Json;

namespace SqlToOracleMigrator.Core;

public sealed class JsonListStore
{
    private readonly string _path;

    public JsonListStore(string path) => _path = path;

    public IReadOnlyList<string> LoadStrings()
    {
        if (!File.Exists(_path)) return Array.Empty<string>();
        var json = File.ReadAllText(_path);
        var list = JsonSerializer.Deserialize<List<string>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        // Note: `??` requires the RHS to be convertible to the LHS type. Cast to the method's
        // return type (IReadOnlyList<string>) so Array.Empty<string>() is valid.
        return (IReadOnlyList<string>?)list ?? Array.Empty<string>();
    }
}

public interface ISqlQueryStore
{
    string Get(string key);
    string Format(string key, Dictionary<string, string> replacements);
}

public sealed class JsonSqlQueryStore : ISqlQueryStore
{
    private readonly Dictionary<string, string> _queries;

    public JsonSqlQueryStore(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Missing sqlqueries.json", path);

        var json = File.ReadAllText(path);
        _queries = JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Make key lookup case-insensitive
        _queries = _queries.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
    }

    public string Get(string key)
    {
        if (!_queries.TryGetValue(key, out var q) || string.IsNullOrWhiteSpace(q))
            throw new KeyNotFoundException($"SQL query '{key}' not found in sqlqueries.json");
        return q;
    }

    public string Format(string key, Dictionary<string, string> replacements)
    {
        var q = Get(key);
        foreach (var kvp in replacements)
            q = q.Replace("{{" + kvp.Key + "}}", kvp.Value, StringComparison.OrdinalIgnoreCase);
        return q;
    }
}

public sealed class DataTypeMappingConfig
{
    public Dictionary<string, string> SqlToOracle { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static DataTypeMappingConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Missing datatype_mappings.json", path);

        var json = File.ReadAllText(path);
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return new DataTypeMappingConfig { SqlToOracle = dict.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase) };
    }
}
