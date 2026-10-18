using System.Globalization;
using System.Windows.Data;
using XCL2.App.Services;

namespace XCL2.App.Models;

public sealed class AccountLoginStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => AccountLoginStatus.Describe(value as Account, DateTime.UtcNow);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
