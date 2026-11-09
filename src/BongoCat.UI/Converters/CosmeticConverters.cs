using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using BongoCat.App.Inventory;
using BongoCat.Core.Cosmetics;
using BongoCat.UI.Cosmetics;
using BongoCat.UI.ViewModels;

namespace BongoCat.UI.Converters;

/// <summary>品质 HEX（#RRGGBB）→ SolidColorBrush（冻结，零通知开销）。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value as string ?? "#9E9E9E"));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Gray;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>品质枚举 → 品质色 Brush（图鉴品质点用）。</summary>
public sealed class QualityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not BongoCat.Core.Cosmetics.ItemQuality quality)
        {
            return Brushes.Gray;
        }

        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                BongoCat.Core.Cosmetics.QualityPalette.GetHex(quality)));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Gray;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 物品 → 48×48 缩略图标（复用 CosmeticArt 同源几何）。
/// 绑定源可为 InventoryItemCard（已拥有）或 CollectopediaEntry（按 Owned 决定彩色/灰剪影）。
/// </summary>
public sealed class CosmeticIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        switch (value)
        {
            case InventoryItemCard card:
                return CosmeticArt.CreateItemIcon(card.Item, true);
            case CollectopediaEntry entry:
                return CosmeticArt.CreateItemIcon(entry.Item, entry.Owned);
            default:
                return DependencyProperty.UnsetValue;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>装备态 + 品质色 → 卡片边框 Brush（已装备=品质色高亮，否则米色低调）。</summary>
public sealed class EquipBorderConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not bool equipped || values[1] is not string hex)
        {
            return Brushes.Transparent;
        }

        if (!equipped)
        {
            return new SolidColorBrush(Color.FromRgb(0xE8, 0xDC, 0xC8));
        }

        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Gray;
        }
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
