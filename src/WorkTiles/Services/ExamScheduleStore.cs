using System.Text.Json;
using WorkTiles.Shared;

namespace WorkTiles.Services;

/// <summary>
/// 考试安排的本地缓存：断网或重启后仍能按时间表显示。按班级隔离，换班后旧缓存失效。
/// 缓存的 ServerTime 是上一次拉取的时刻，恢复时只用于显示，不参与校时。
/// </summary>
public sealed class ExamScheduleStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string FilePath { get; } = System.IO.Path.Combine(directory, "exam-schedule.json");

    public sealed record Cache(Guid ClassId, ExamScheduleDto Schedule);

    public ExamScheduleDto? Load(Guid classId)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            Cache? cache = JsonSerializer.Deserialize<Cache>(File.ReadAllText(FilePath), Json);
            return cache?.ClassId == classId && cache.Schedule is not null ? cache.Schedule : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void Save(Guid classId, ExamScheduleDto schedule)
    {
        try
        {
            // 临时文件写入后整体替换：写入中途断电或崩溃不会留下损坏的缓存。
            ProjectStore.AtomicWrite(FilePath, stream => JsonSerializer.Serialize(stream, new Cache(classId, schedule), Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
