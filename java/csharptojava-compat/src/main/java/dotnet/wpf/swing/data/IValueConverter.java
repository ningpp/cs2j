package dotnet.wpf.swing.data;

import io.github.ningpp.compat.CultureInfo;

/**
 * System.Windows.Data.IValueConverter compatibility interface.
 */
public interface IValueConverter {

    Object convert(Object value, Class<?> targetType, Object parameter, CultureInfo culture);

    Object convertBack(Object value, Class<?> targetType, Object parameter, CultureInfo culture);
}
