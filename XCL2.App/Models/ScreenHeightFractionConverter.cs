using System.Globalization;
using System.Windows.Data;

namespace XCL2.App.Models;

public class ScreenHeightFractionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double screenHeight) return 400d;

        var fraction = 0.55;
        if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            fraction = parsed;

        // 保底：极小屏幕/多显示器异常值时不要算出一个小于 200 的离谱高度，
        // 也不要超过屏幕本身（弹层比屏幕还高没有意义，Popup 也会自己夹住）。
        var result = screenHeight * fraction;
        return result < 200 ? 200d : result;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
