using System.Text.Json;

namespace WorkTiles.Services;

public sealed record WeatherCity(string Name, string Code)
{
    // Code 仅用于请求天气接口，地区选择界面不应向用户暴露内部编码。
    public override string ToString() => Name;
}

public sealed class WeatherCityCatalog
{
    private readonly object _gate = new();
    private Lazy<Task<IReadOnlyList<WeatherCity>>>? _load;

    public async Task<IReadOnlyList<WeatherCity>> SearchAsync(string query, int limit = 100)
    {
        Lazy<Task<IReadOnlyList<WeatherCity>>> load;
        lock (_gate) load = _load ??= new Lazy<Task<IReadOnlyList<WeatherCity>>>(LoadAsync);
        IReadOnlyList<WeatherCity> cities;
        try { cities = await load.Value; }
        catch
        {
            // 读取失败不要缓存成永久失败；下一次搜索重新尝试。
            lock (_gate) { if (ReferenceEquals(_load, load)) _load = null; }
            throw;
        }
        string keyword = query.Trim();
        return cities.Where(city => keyword.Length == 0 || city.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Take(limit).ToList();
    }

    private static async Task<IReadOnlyList<WeatherCity>> LoadAsync()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "xiaomi_weather_cities.json");
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<WeatherCity>>(stream) ?? [];
    }
}
