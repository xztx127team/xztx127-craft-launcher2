using System.Collections.ObjectModel;

namespace XCL2.App.Services;

/// <summary>Keeps collapsed groups free of item containers and expands visible files in bounded batches.</summary>
internal sealed class IncrementalGroupEntries<T>
{
    private readonly IReadOnlyList<T> _source;
    private readonly bool _continuous;
    private readonly int _batchSize;
    public ObservableCollection<T> Visible { get; } = new();
    public int TotalCount => _source.Count;
    public bool IsExpanded { get; private set; }
    public bool HasMore => IsExpanded && Visible.Count < _source.Count;

    public IncrementalGroupEntries(IReadOnlyList<T> source, bool continuous, int batchSize)
    {
        _source = source;
        _continuous = continuous;
        _batchSize = DownloadListBatchPolicy.Normalize(batchSize);
    }

    public void SetExpanded(bool expanded, int? initialLimit = null)
    {
        if (IsExpanded == expanded) return;
        IsExpanded = expanded;
        Visible.Clear();
        if (expanded)
        {
            var initial = DownloadListBatchPolicy.Initial(_batchSize);
            if (initialLimit.HasValue) initial = Math.Clamp(initialLimit.Value, 0, initial);
            Append(_continuous ? initial : _source.Count);
        }
    }

    public void LoadNext()
    {
        if (HasMore) Append(_continuous ? _batchSize : _source.Count);
    }

    private void Append(int count)
    {
        var end = Visible.Count + Math.Min(count, _source.Count - Visible.Count);
        for (var index = Visible.Count; index < end; index++) Visible.Add(_source[index]);
    }
}
