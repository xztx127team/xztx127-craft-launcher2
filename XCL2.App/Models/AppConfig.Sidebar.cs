namespace XCL2.App.Models;

public enum SidebarPlacement { Left, Right }

public partial class AppConfig
{
    private SidebarPlacement _sidebarPlacement;
    private int _sidebarExpandedWidth;
    private double _sidebarLabelFontSize;
    private Dictionary<string, string> _sidebarLabels = new(StringComparer.OrdinalIgnoreCase);

    public SidebarPlacement SidebarPlacement
    {
        get => _sidebarPlacement;
        set => _sidebarPlacement = Enum.IsDefined(value) ? value : global::XCL2.App.Models.SidebarPlacement.Left;
    }

    /// <summary>0 follows the original language/theme width.</summary>
    public int SidebarExpandedWidth
    {
        get => _sidebarExpandedWidth;
        set => _sidebarExpandedWidth = value <= 0 ? 0 : Math.Clamp(value, 160, 360);
    }

    /// <summary>0 follows the current theme's navigation font size.</summary>
    public double SidebarLabelFontSize
    {
        get => _sidebarLabelFontSize;
        set => _sidebarLabelFontSize = !double.IsFinite(value) || value <= 0 ? 0 : Math.Clamp(value, 11, 22);
    }

    public bool SidebarShowNavigationIcons { get; set; } = true;
    public bool SidebarStartCollapsed { get; set; }

    public Dictionary<string, string> SidebarLabels
    {
        get => _sidebarLabels;
        set
        {
            // A hand-edited JSON file can contain HOME and home. Accept it deterministically.
            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (value != null)
                foreach (var pair in value)
                    if (!string.IsNullOrWhiteSpace(pair.Key)) labels[pair.Key] = pair.Value ?? "";
            _sidebarLabels = labels;
        }
    }
}
