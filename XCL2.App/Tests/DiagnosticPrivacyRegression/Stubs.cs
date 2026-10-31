using System.Collections.ObjectModel;
using System.ComponentModel;
namespace XCL2.App.Services;
public enum DownloadQueueItemStatus { Downloading, Paused, Completed, Failed, Canceled }
public class DownloadQueueItem : INotifyPropertyChanged
{
    public DownloadQueueItemStatus Status { get; set; }
    public bool IsRunning { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void ChangeStatus(DownloadQueueItemStatus status) { Status = status; PropertyChanged?.Invoke(this, new(nameof(Status))); }
    public void Complete(DownloadQueueItemStatus status) { Status = status; IsRunning = false; PropertyChanged?.Invoke(this, new(nameof(IsRunning))); }
}
public class DownloadQueueService
{
    public static DownloadQueueService Instance { get; } = new();
    public ObservableCollection<DownloadQueueItem> Items { get; } = new();
}
