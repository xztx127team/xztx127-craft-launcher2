using XCL2.App.Services;

var checks = 0;
void Check(bool success, string name)
{
    if (!success) throw new Exception(name);
    checks++;
}
var source = Enumerable.Range(0, 257).ToArray();
var group = new IncrementalGroupEntries<int>(source, true, 50);
Check(group.Visible.Count == 0 && !group.HasMore, "A collapsed group must not materialize file rows");
group.SetExpanded(true);
Check(group.Visible.SequenceEqual(source.Take(20)) && group.HasMore, "First render must stop at 20 even when the configured batch is larger");
group.LoadNext();
Check(group.Visible.SequenceEqual(source.Take(70)), "The next scroll appends exactly one configured batch without duplicates");
group.SetExpanded(false);
Check(group.Visible.Count == 0 && !group.HasMore && group.TotalCount == 257, "Collapsing must release visible rows without losing total metadata");
group.LoadNext();
Check(group.Visible.Count == 0, "A stale scroll must not load a collapsed group");
group.SetExpanded(true);
Check(group.Visible.Count == 20, "Reopening starts with a bounded first batch");
while (group.HasMore) group.LoadNext();
Check(group.Visible.SequenceEqual(source), "Scrolling to the end retains all entries in order");
group.LoadNext();
Check(group.Visible.Count == source.Length, "Extra requests at the end must not duplicate rows");
var small = new IncrementalGroupEntries<int>(source, true, -1);
small.SetExpanded(true);
Check(small.Visible.Count == 10, "Invalid small batch sizes must clamp to 10");
small.LoadNext();
Check(small.Visible.Count == 20, "Subsequent batches honor the lower clamp");
var large = new IncrementalGroupEntries<int>(source, true, int.MaxValue);
large.SetExpanded(true);
large.LoadNext();
Check(large.Visible.Count == 120, "Large settings must clamp subsequent batches to 100");
var disabled = new IncrementalGroupEntries<int>(source, false, 20);
disabled.SetExpanded(true);
Check(disabled.Visible.SequenceEqual(source) && !disabled.HasMore, "Disabling continuous mode retains the complete expanded list");
var empty = new IncrementalGroupEntries<int>(Array.Empty<int>(), true, 20);
empty.SetExpanded(true);
empty.LoadNext();
Check(empty.Visible.Count == 0 && !empty.HasMore, "Empty groups terminate immediately");
var budgeted = new IncrementalGroupEntries<int>(source, true, 50);
budgeted.SetExpanded(true, 0);
Check(budgeted.Visible.Count == 0 && budgeted.HasMore, "Additional initially expanded groups must respect an exhausted shared render budget");
budgeted.LoadNext();
Check(budgeted.Visible.Count == 50, "A budget-deferred expanded group must still load on its next scroll");
Console.WriteLine($"PASS {checks} hierarchical file-list checks");
