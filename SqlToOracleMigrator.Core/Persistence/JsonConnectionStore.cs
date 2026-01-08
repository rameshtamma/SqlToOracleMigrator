using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlToOracleMigrator.Core;

public sealed class JsonConnectionStore
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonConnectionStore(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    public IReadOnlyList<ConnectionDefinition> LoadAll()
    {
        _paths.EnsureCreated();

        var files = Directory.EnumerateFiles(_paths.ConnectionsDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<ConnectionDefinition>();
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var item = JsonSerializer.Deserialize<ConnectionDefinition>(json, _jsonOptions);
                if (item is null) continue;

                // Never load passwords into memory automatically
                item.RuntimePassword = null;
                results.Add(item);
            }
            catch
            {
                // Ignore corrupt file; user can delete it manually
            }
        }

        return results;
    }

    public string GetFilePath(ConnectionDefinition def)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));
        var safeName = MakeSafeFileName(def.Name);
        return Path.Combine(_paths.ConnectionsDirectory, $"{safeName}.json");
    }

    public void Save(ConnectionDefinition def)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));
        _paths.EnsureCreated();

        var filePath = GetFilePath(def);
        var tempPath = filePath + ".tmp";

        var json = JsonSerializer.Serialize(def, _jsonOptions);
        File.WriteAllText(tempPath, json);

        if (File.Exists(filePath))
            File.Delete(filePath);

        File.Move(tempPath, filePath);
    }

    public void Delete(ConnectionDefinition def)
    {
        var filePath = GetFilePath(def);
        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return safe.Trim();
    }
}
