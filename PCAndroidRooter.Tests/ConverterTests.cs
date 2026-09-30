using System.Globalization;
using System.Windows;
using PCAndroidRooter.Converters;
using Xunit;

namespace PCAndroidRooter.Tests;

public class ConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(true, null, Visibility.Visible)]
    [InlineData(false, null, Visibility.Collapsed)]
    [InlineData(true, "Invert", Visibility.Collapsed)]
    [InlineData(false, "Invert", Visibility.Visible)]
    public void BoolToVisibility_Converts(bool value, string? param, Visibility expected)
    {
        var c = new BoolToVisibilityConverter();
        var actual = c.Convert(value, typeof(Visibility), param, Culture);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BoolToVisibility_NonBool_ReturnsCollapsed()
    {
        var c = new BoolToVisibilityConverter();
        Assert.Equal(Visibility.Collapsed, c.Convert("x", typeof(Visibility), null, Culture));
    }

    [Theory]
    [InlineData("Alto", 0xEF, 0x53, 0x50)]
    [InlineData("Medio", 0xFF, 0xA7, 0x26)]
    [InlineData("Bajo", 0x2E, 0x7D, 0x32)]
    public void RiskToColor_MapsRiskLevels(string risk, byte r, byte g, byte b)
    {
        var c = new RiskToColorConverter();
        var brush = Assert.IsAssignableFrom<System.Windows.Media.Brush>(c.Convert(risk, typeof(object), null, Culture));
        var color = ((System.Windows.Media.SolidColorBrush)brush).Color;
        Assert.Equal(System.Windows.Media.Color.FromRgb(r, g, b), color);
    }

    [Theory]
    [InlineData("Conectado", 0x66, 0xBB, 0x6A)]
    [InlineData("Desconectado", 0xEF, 0x53, 0x50)]
    [InlineData("Otro", 0xFF, 0xA7, 0x26)]
    public void StatusToColor_MapsStatusText(string status, byte r, byte g, byte b)
    {
        var c = new StatusToColorConverter();
        var brush = Assert.IsAssignableFrom<System.Windows.Media.Brush>(c.Convert(status, typeof(object), null, Culture));
        var color = ((System.Windows.Media.SolidColorBrush)brush).Color;
        Assert.Equal(System.Windows.Media.Color.FromRgb(r, g, b), color);
    }
}
