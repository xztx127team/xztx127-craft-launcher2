using System.Windows;

namespace XCL2.App.Services;

public sealed record LauncherReminder(string Key, string Title, string Description,
    string? ActionLabel, Func<Task>? Action, DateTime CreatedAt, bool IsRead = false,
    string? SecondaryLabel = null, Func<Task>? SecondaryAction = null);

/// <summary>建议只进入收件箱；发布不会打开窗口、导航页面或执行操作。</summary>
public static class ReminderCenterService
{
    private static readonly List<LauncherReminder> Messages = new();
    private static readonly HashSet<string> Dismissed = new(StringComparer.Ordinal);
    public static event Action? Changed;
    public static IReadOnlyList<LauncherReminder> Items => Messages.ToArray();
    public static int UnreadCount => Messages.Count(message => !message.IsRead);

    public static void Publish(string key, string title, string description,
        string? actionLabel = null, Func<Task>? action = null,
        string? secondaryLabel = null, Func<Task>? secondaryAction = null, bool initiallyRead = false) => OnUi(() =>
    {
        if (string.IsNullOrWhiteSpace(key) || Dismissed.Contains(key)) return;
        var index = Messages.FindIndex(message => message.Key == key);
        if (index >= 0 && Messages[index].Title == title && Messages[index].Description == description) return;
        if (index >= 0) Messages.RemoveAt(index);
        Messages.Insert(0, new(key, title, description, actionLabel, action, DateTime.Now, initiallyRead, secondaryLabel, secondaryAction));
        while (Messages.Count > 40) Messages.RemoveAt(Messages.Count - 1);
        Changed?.Invoke();
    });

    public static void MarkRead() => OnUi(() =>
    {
        for (var i = 0; i < Messages.Count; i++) Messages[i] = Messages[i] with { IsRead = true };
        Changed?.Invoke();
    });

    public static void Dismiss(string key) => OnUi(() =>
    {
        Dismissed.Add(key);
        Messages.RemoveAll(message => message.Key == key);
        Changed?.Invoke();
    });

    /// <summary>Remove a replaceable status without treating it as a permanent user dismissal.</summary>
    public static void Remove(string key) => OnUi(() =>
    {
        Messages.RemoveAll(message => message.Key == key);
        Changed?.Invoke();
    });

    public static void AllowAgain(string key) => OnUi(() => Dismissed.Remove(key));

    public static void ClearRead() => OnUi(() =>
    {
        foreach (var message in Messages.Where(message => message.IsRead)) Dismissed.Add(message.Key);
        Messages.RemoveAll(message => message.IsRead);
        Changed?.Invoke();
    });

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action);
    }
}
