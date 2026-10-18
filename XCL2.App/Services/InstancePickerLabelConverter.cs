using System.Globalization;
using System.Windows.Data;
using XCL2.App.Models;

namespace XCL2.App.Services;

public sealed class InstancePickerLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not GameFolder folder) return value?.ToString() ?? "";
        var cfg = ConfigService.Active?.Config;
        return InstanceExperienceService.FolderLabel(folder, cfg?.Folders ?? new(), cfg?.Ux50DisambiguateFolderNames != false);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
