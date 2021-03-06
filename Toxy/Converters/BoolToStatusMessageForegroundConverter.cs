﻿using System;
using System.Windows;
using System.Windows.Data;

namespace Toxy.Converters
{
    public class BoolToStatusMessageForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var dic = new ResourceDictionary();
            dic.Source = new Uri("pack://application:,,,/Toxy;component/Resources/Colors.xaml");

            bool? selected = value as bool?;
            if (selected == null)
                return dic["ToxLightGreyBrush"];

            if (selected == true)
                return dic["ToxMediumGreyBrush"];

            return dic["ToxLightGreyBrush"];
        }

        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return null;
        }
    }
}
