using System;
using System.Globalization;
using System.Windows.Data;

namespace PanelRemote.Infrastructure;

/// <summary>
/// Конвертер для привязки enum к RadioButton.
/// Используется в AdminWindow для выбора роли устройства.
/// </summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return false;

        var enumValue = value.ToString();
        var targetValue = parameter.ToString();
        
        return string.Equals(enumValue, targetValue, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (parameter == null || value is not bool boolValue || !boolValue)
            return Binding.DoNothing;

        var parameterString = parameter.ToString();
        
        if (targetType.IsEnum && Enum.TryParse(targetType, parameterString, true, out var result))
        {
            return result;
        }

        return Binding.DoNothing;
    }
}
