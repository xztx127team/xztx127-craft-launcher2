using System.Windows;
using System.Windows.Controls;
using XCL2.App.Services;

namespace XCL2.App.Views;

// A lightweight host for the production input/capture implementation. Search navigation is unrelated.
public partial class FeatureSearchBox : UserControl
{
    internal readonly Button SearchEntry = new() { Content = "Hold to edit search", Height = 36 };
    private readonly Border LayoutEditFrame = new();
    private readonly Border EmptyClickHint = new();
    private SearchOwner? _owner;
    private bool _floating;
    internal bool Floating = true;
    internal FeatureSearchLayout CurrentLayout = FeatureSearchLayout.Default;
    internal int CompletedEdits;

    public FeatureSearchBox()
    {
        Content = SearchEntry;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        InitializeLayoutEditing();
        LayoutEdited += (layout, complete) =>
        {
            CurrentLayout = layout;
            Apply((FrameworkElement)Parent);
            if (complete) CompletedEdits++;
        };
    }

    internal void Apply(FrameworkElement host)
    {
        _owner ??= new SearchOwner { Resized = () => { CancelEditGesture(); Apply(host); } };
        Grid.SetRowSpan(this, Floating ? 2 : 1);
        var placement = FeatureSearchLayoutPolicy.Resolve(CurrentLayout, Floating, host.ActualWidth, host.ActualHeight);
        Width = placement.Width;
        SearchEntry.Height = placement.Height;
        Margin = new Thickness(placement.X, placement.Y, 0, 0);
        UpdateLayoutGeometry(CurrentLayout, placement, host, Floating);
    }

    public void Close() { }
    private sealed class SearchOwner
    {
        internal Action? Resized;
        public void RefreshFeatureSearchForHostResize() => Resized?.Invoke();
    }
}
