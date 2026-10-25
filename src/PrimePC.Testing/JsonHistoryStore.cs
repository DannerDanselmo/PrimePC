using System.Text.Json;

namespace PrimePC.Testing;

/// <summary>Histórico local (JSON) de resultados, para comparar execuções anteriores.</summary>
public sealed class JsonHistoryStore<T>
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _path;
    private readonly int _maxItems;

    public JsonHistoryStore(string path, int maxItems = 200)
    {
        _path = path;
        _maxItems = maxItems;
    }

    public IReadOnlyList<T> Load()
    {
        lock (_sync)
        {
            try
            {
                return File.Exists(_path)
                    ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(_path), Json) ?? new List<T>()
                    : new List<T>();
            }
            catch
            {
                return new List<T>();
            }
        }
    }

    public void Add(T item)
    {
        lock (_sync)
        {
            var list = Load().ToList();
            list.Add(item);
            if (list.Count > _maxItems)
                list.RemoveRange(0, list.Count - _maxItems);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, Json));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    /// <summary>Substitui todo o histórico (ex.: depois de apagar um item).</summary>
    public void Save(IEnumerable<T> items)
    {
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items.ToList(), Json));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
    }
}
