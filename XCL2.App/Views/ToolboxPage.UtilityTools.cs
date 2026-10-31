using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using XCL2.App.Services;
using XCL2.App.Views.Tools;

namespace XCL2.App.Views;

public partial class ToolboxPage
{
    private void InitializeUtilityTools()
    {
        // Existing 0–36 IDs stay unchanged because pinned/favorite tools persist their indices.
        if (ToolboxTabControl.Items.Count != UtilityToolCatalog.FirstTabIndex)
            throw new InvalidOperationException("百宝箱工具索引与搜索目录不一致，请同步 UtilityToolCatalog.FirstTabIndex。");
        foreach (var definition in UtilityToolCatalog.All)
        {
            var tab = new TabItem { Header = definition.Title, ToolTip = definition.Description };
            Selector.AddSelectedHandler(tab, (_, e) =>
            {
                if (ReferenceEquals(e.OriginalSource, tab) && tab.Content == null)
                    tab.Content = new UtilityToolPanel(definition);
            });
            ToolboxTabControl.Items.Add(tab);
        }
    }
}
