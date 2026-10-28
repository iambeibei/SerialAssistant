using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SerialAssistant.Converters
{
    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isOpen)// 这里的value就是bool值
            {
                string? colorType = parameter as string;

                // 使用传统的switch语句
                switch (colorType)
                {
                    case "open":
                        return isOpen ? Brushes.Red : Brushes.Green;
                    case "send":
                        return Brushes.LightBlue;
                    case "clear":
                        return Brushes.LightYellow;
                    case "refresh":
                        return Brushes.LightGreen;
                    default:
                        return Brushes.LightGray;
                }
            }
            return Brushes.LightGray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
