using System.Windows;
using System.Windows.Media;

namespace ShiftDrop.UI;

internal static class Theme
{
    public static readonly Color AccentA = Color.FromRgb(0xFF, 0xA2, 0x3D);
    public static readonly Color AccentB = Color.FromRgb(0xFF, 0x5E, 0x3A);

    public static readonly Brush Accent = Freeze(new LinearGradientBrush(AccentA, AccentB, new Point(0, 0), new Point(1, 1)));
    public static readonly Brush AccentSolid = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x86, 0x3C)));
    public static readonly Brush Panel = Freeze(new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x1F, 0x24)));
    public static readonly Brush Sector = Freeze(new SolidColorBrush(Color.FromArgb(0xEB, 0x26, 0x27, 0x2D)));
    public static readonly Brush SectorStroke = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
    public static readonly Brush Hub = Freeze(new SolidColorBrush(Color.FromArgb(0xF5, 0x16, 0x16, 0x1A)));
    public static readonly Brush Text = Brushes.White;
    public static readonly Brush TextDim = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
    public static readonly Brush Error = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A)));

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
