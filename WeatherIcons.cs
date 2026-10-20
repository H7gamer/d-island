// D-Island - Copyright 2026 h7 (ibuildrigs) and contributors.
// Licensed under the D-Island License 1.0; see LICENSE.txt and NOTICE.txt.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using DIsland.Services;

namespace DIsland;

/// <summary>
/// Small vector weather icons on a 24×24 grid (WPF can't draw color emoji, and the icon font has no
/// proper weather set). Sun, moon and cloud are combined per condition.
/// </summary>
public static class WeatherIcons
{
    private static readonly Brush Sun = Frozen(Color.FromRgb(0xFF, 0xD6, 0x0A));
    private static readonly Brush Moon = Frozen(Color.FromRgb(0xE5, 0xE5, 0xEA));
    private static readonly Brush CloudLight = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF7));
    private static readonly Brush CloudGray = Frozen(Color.FromRgb(0xC7, 0xC7, 0xCC));
    private static readonly Brush Water = Frozen(Color.FromRgb(0x64, 0xD2, 0xFF));

    private const string CloudPath = "M7,19 H17.5 A4.5,4.5 0 0 0 17.5,10 A6,6 0 0 0 6.2,11.6 A3.75,3.75 0 0 0 7,19 Z";
    private const string SunRays = "M12,1.5 V4 M12,20 V22.5 M1.5,12 H4 M20,12 H22.5 M4.6,4.6 L6.3,6.3 M17.7,17.7 L19.4,19.4 " +
                                   "M4.6,19.4 L6.3,17.7 M17.7,6.3 L19.4,4.6";

    public static FrameworkElement Create(WeatherKind kind, bool isDay, double size)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        switch (kind)
        {
            case WeatherKind.Clear:
                AddSky(canvas, isDay, 1, 0, 0);
                break;
            case WeatherKind.PartlyCloudy:
                AddSky(canvas, isDay, 0.62, -3.2, -3.4);
                Add(canvas, Fill(CloudPath, CloudLight), 1.5, 1.5);
                break;
            case WeatherKind.Cloudy:
                Add(canvas, Fill(CloudPath, CloudGray), 0, -1);
                break;
            case WeatherKind.Fog:
                canvas.Children.Add(Stroke("M4,9 H20 M6,13 H18 M4,17 H20", CloudGray, 2));
                break;
            case WeatherKind.Drizzle:
                Add(canvas, Fill(CloudPath, CloudGray), 0, -4);
                canvas.Children.Add(Stroke("M9,18.5 L8.5,20 M15,18.5 L14.5,20 M12,20.5 L11.5,22", Water, 1.6));
                break;
            case WeatherKind.Rain:
                Add(canvas, Fill(CloudPath, CloudGray), 0, -4);
                canvas.Children.Add(Stroke("M8.5,18 L7.3,21.5 M12.5,18 L11.3,21.5 M16.5,18 L15.3,21.5", Water, 1.7));
                break;
            case WeatherKind.Snow:
                Add(canvas, Fill(CloudPath, CloudGray), 0, -4);
                canvas.Children.Add(Fill("M8,19 m-1.3,0 a1.3,1.3 0 1 0 2.6,0 a1.3,1.3 0 1 0 -2.6,0 " +
                                         "M12,21.5 m-1.3,0 a1.3,1.3 0 1 0 2.6,0 a1.3,1.3 0 1 0 -2.6,0 " +
                                         "M16,19 m-1.3,0 a1.3,1.3 0 1 0 2.6,0 a1.3,1.3 0 1 0 -2.6,0", Brushes.White));
                break;
            case WeatherKind.Thunder:
                Add(canvas, Fill(CloudPath, CloudGray), 0, -4);
                canvas.Children.Add(Fill("M12.8,15 L9.6,19.6 H12 L10.6,23.2 L15.2,17.8 H12.6 L14.2,15 Z", Sun));
                break;
        }
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    /// <summary>Sun by day, crescent moon by night, scaled about the center and offset.</summary>
    private static void AddSky(Canvas canvas, bool isDay, double scale, double dx, double dy)
    {
        var group = new Canvas { Width = 24, Height = 24, RenderTransformOrigin = new Point(0.5, 0.5) };
        if (isDay)
        {
            group.Children.Add(Fill("M12,7 A5,5 0 1 1 11.99,7 Z", Sun));
            group.Children.Add(Stroke(SunRays, Sun, 1.8));
        }
        else
        {
            var moon = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(12, 12), 8, 8), new EllipseGeometry(new Point(16.5, 8.5), 7, 7));
            moon.Freeze();
            group.Children.Add(new Path { Data = moon, Fill = Moon });
        }
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(scale, scale));
        transform.Children.Add(new TranslateTransform(dx, dy));
        group.RenderTransform = transform;
        canvas.Children.Add(group);
    }

    private static void Add(Canvas canvas, UIElement element, double dx, double dy)
    {
        element.RenderTransform = new TranslateTransform(dx, dy);
        canvas.Children.Add(element);
    }

    private static Path Fill(string data, Brush brush) => new() { Data = Geometry.Parse(data), Fill = brush };

    private static Path Stroke(string data, Brush brush, double thickness) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = brush,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    };

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
