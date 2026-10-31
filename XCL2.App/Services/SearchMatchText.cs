using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace XCL2.App.Services;

/// <summary>One text layer only: runs replace text, without shadows, duplicate labels or overlays.</summary>
public static class SearchMatchText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(SearchMatchText), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty QueryProperty = DependencyProperty.RegisterAttached("Query", typeof(string), typeof(SearchMatchText), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SearchMatchText), new PropertyMetadata(true, Changed));
    public static string GetText(DependencyObject obj) => (string)obj.GetValue(TextProperty);
    public static void SetText(DependencyObject obj, string value) => obj.SetValue(TextProperty, value);
    public static string GetQuery(DependencyObject obj) => (string)obj.GetValue(QueryProperty);
    public static void SetQuery(DependencyObject obj, string value) => obj.SetValue(QueryProperty, value);
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);
    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        if (obj is not TextBlock block) return;
        string text = GetText(block) ?? "";
        block.Inlines.Clear();
        int offset = 0;
        if (GetEnabled(block))
        {
            foreach (var range in ExperienceSearchState.HighlightRanges(text, GetQuery(block)))
            {
                if (range.Start > offset) block.Inlines.Add(new Run(text[offset..range.Start]));
                var run = new Run(text.Substring(range.Start, range.Length)) { FontWeight = FontWeights.Bold };
                run.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
                block.Inlines.Add(run);
                offset = range.Start + range.Length;
            }
        }
        if (offset < text.Length) block.Inlines.Add(new Run(text[offset..]));
    }
}
