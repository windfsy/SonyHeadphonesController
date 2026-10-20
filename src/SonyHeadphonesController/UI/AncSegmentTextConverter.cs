using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SonyCtrl.UI;

/// <summary>Selected ANC segment label turns white (on the accent-coloured knob);
/// unselected segments stay the panel's label gray.</summary>
public sealed class AncSegmentTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
