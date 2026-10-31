using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace XCL2.App.Services.Workbench;

public sealed class WorkStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string Evidence { get; set; } = "";
}
public sealed class WorkTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Goal { get; set; } = "";
    public string ProjectRoot { get; set; } = "";
    public string Mode { get; set; } = "Plan";
    public string Status { get; set; } = "paused";
    public string NextAction { get; set; } = "";
    public string Notes { get; set; } = "";
    public List<WorkStep> Steps { get; set; } = new();
    public List<string> ChangeIds { get; set; } = new();
    public List<string> Checkpoints { get; set; } = new();
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public long Revision { get; set; }
}
public sealed class TaskStore(string directory)
{
    private static readonly object Gate = new();
    private string PathFor(string id) => Regex.IsMatch(id, "^[a-f0-9]{32}$") ? Path.Combine(directory, id + ".json") : throw new ArgumentException("任务 ID 无效。");
    public WorkTask Get(string id)
    {
        lock (Gate)
        {
            var task = JsonSerializer.Deserialize<WorkTask>(File.ReadAllBytes(PathFor(id)))
                ?? throw new InvalidDataException("任务记录为空。");
            if (!string.Equals(task.Id, id, StringComparison.Ordinal))
                throw new InvalidDataException("任务记录 ID 与文件名不一致，不能读取或更新。");
            try { Validate(task); }
            catch (ArgumentException ex) { throw new InvalidDataException("任务记录损坏：" + ex.Message, ex); }
            return task;
        }
    }
    public WorkTask? TryGet(string id)
    {
        lock (Gate)
        {
            try { return Get(id); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
    }
    public IReadOnlyList<WorkTask> List()
    {
        lock (Gate)
        {
            var list = new List<WorkTask>();
            if (Directory.Exists(directory)) foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
                try { list.Add(Get(Path.GetFileNameWithoutExtension(path))); } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException) { }
            return list.OrderByDescending(t => t.UpdatedUtc).ToArray();
        }
    }
    public WorkTask Create(string title, string goal, string projectRoot = "")
    { var task = new WorkTask { Title = title, Goal = goal, ProjectRoot = projectRoot }; Save(task); return task; }
    public void Save(WorkTask task)
    {
        lock (Gate)
        {
            Validate(task);
            var path = PathFor(task.Id);
            // A snapshot of a deleted record must never recreate it via a late save.
            if (!File.Exists(path) && task.Revision > 0) throw new IOException("任务已被删除，不能重新保存。请创建新任务。");
            if (File.Exists(path) && Get(task.Id).Revision != task.Revision) throw new IOException("任务已被其他会话更新，请重新加载。");
            task.Checkpoints = task.Checkpoints.TakeLast(100).ToList(); task.ChangeIds = task.ChangeIds.Distinct().TakeLast(1000).ToList();
            var oldRevision = task.Revision; task.UpdatedUtc = DateTime.UtcNow; task.Revision++;
            try { ProjectFiles.AtomicWrite(path, JsonSerializer.SerializeToUtf8Bytes(task)); } catch { task.Revision = oldRevision; throw; }
        }
    }
    private static void Validate(WorkTask task)
    {
        if (task.Id == null || !Regex.IsMatch(task.Id, "^[a-f0-9]{32}$")) throw new ArgumentException("任务 ID 无效。");
        if (task.Title == null || task.Goal == null || task.ProjectRoot == null || task.Notes == null || task.NextAction == null
            || task.Steps == null || task.ChangeIds == null || task.Checkpoints == null
            || task.ChangeIds.Any(id => id == null) || task.Checkpoints.Any(text => text == null))
            throw new ArgumentException("任务文本和集合不能为空值。");
        if (task.Steps.Any(s => s == null || string.IsNullOrWhiteSpace(s.Id) || s.Id.Length > 128 || s.Title == null || s.Evidence == null)
            || task.Steps.Select(s => s.Id).Distinct().Count() != task.Steps.Count)
            throw new ArgumentException("步骤 ID 必须唯一且非空，步骤文本不能为空值。");
        if (task.Steps.Count > 100 || task.Steps.Count(s => s.Status == "in_progress") > 1
            || task.Steps.Any(s => s.Status is not ("pending" or "in_progress" or "completed" or "blocked")))
            throw new ArgumentException("任务步骤状态无效，最多一个步骤可处于进行中。");
        if (task.Mode is not ("Plan" or "Build" or "Agent") || task.Status is not ("paused" or "running" or "completed" or "interrupted" or "archived") || task.Revision < 0)
            throw new ArgumentException("任务状态或版本无效。");
        if (task.Title.Length > 300 || task.Goal.Length > 20000 || task.Notes.Length > 20000 || task.NextAction.Length > 10000
            || task.Steps.Any(s => s.Title.Length > 1000 || s.Evidence.Length > 4000)) throw new ArgumentException("任务文本过长。");
    }
    public WorkTask Update(string id, Action<WorkTask> edit)
    { lock (Gate) { var task = Get(id); edit(task); Save(task); return task; } }
    public WorkTask Checkpoint(string id, string text) => Update(id, t => t.Checkpoints.Add(DateTime.UtcNow.ToString("O") + " " + text[..Math.Min(text.Length, 4000)]));
    public void DeleteCompleted(string id)
    {
        lock (Gate)
        {
            var task = Get(id);
            if (task.Status != "completed") throw new InvalidOperationException("只能删除已完成的任务。");
            File.Delete(PathFor(id));
        }
    }
}
