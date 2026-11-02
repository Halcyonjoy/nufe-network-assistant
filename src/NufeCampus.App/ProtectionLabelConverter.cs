using System.Globalization;
using System.Windows.Data;
using NufeCampus.SelfService;
namespace NufeCampus.App;

public sealed class ProtectionLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string source || QuotaRecovery.StrictMac(source) is not { } mac) return "MAC 不可用";
        return values[1] is IEnumerable<string> protectedMacs && protectedMacs.Any(m => QuotaRecovery.StrictMac(m) == mac) ? "白名单" : "未加入";
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
