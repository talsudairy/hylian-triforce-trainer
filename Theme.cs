using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Tozot;

/// <summary>Forest grove, liquid glass: the code-built pieces that read the styles in Glass.xaml.</summary>
public static class Theme
{
    public static readonly Brush Bg = Hex("#0B1610"), Text = Hex("#F1EEDD"), Dim = Hex("#A9B8A2"), Gold = Hex("#D9B649"),
        Good = Hex("#8FE08A"), Bad = Hex("#F5978A"), Heart = Hex("#E8574A"), HeartEdge = Hex("#FFD2C8"), Gem = Hex("#4FD67A");
    public static readonly FontFamily Serif = new("Georgia, Cambria, serif");

    static Brush Hex(string s)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s));
        b.Freeze();
        return b;
    }

    public static Button Button(string text, bool accent = false)
    {
        var b = new Button { Content = text };
        if (accent) b.Style = (Style)Application.Current.FindResource("GoldButton");
        return b;
    }

    public static TextBox Box(string text = "", double width = 90) => new() { Text = text, Width = width };

    public static TextBlock Label(string text, double width = 0, bool dim = false)
    {
        var t = new TextBlock { Text = text, Foreground = dim ? Dim : Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), TextWrapping = TextWrapping.Wrap };
        if (width > 0) t.Width = width;
        return t;
    }

    public static TextBlock Heading(string text) => new()
    {
        Text = text, FontFamily = Serif, FontSize = 18, Foreground = Gold, Margin = new Thickness(0, 0, 0, 8),
    };

    public static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static ComboBox Combo(IEnumerable<string> items, double width = 160)
    {
        var c = new ComboBox { Width = width };
        foreach (var i in items) c.Items.Add(i);
        if (c.Items.Count > 0) c.SelectedIndex = 0;
        return c;
    }

    /// <summary>A glass card holding a heading and its rows.</summary>
    public static Border Card(string heading, params UIElement[] rows)
    {
        var p = new StackPanel();
        if (heading.Length > 0) p.Children.Add(Heading(heading));
        foreach (var r in rows) p.Children.Add(r);
        return new Border { Style = (Style)Application.Current.FindResource("Card"), Child = p };
    }

    // ---------------------------------------------------------------- hearts and the rupee gem (drawn shapes)

    static readonly Geometry HeartShape = Geometry.Parse("M10,17.5 C4,13 0,9.6 0,5.6 C0,2.5 2.4,0 5.3,0 C7.3,0 9,1.1 10,2.8 C11,1.1 12.7,0 14.7,0 C17.6,0 20,2.5 20,5.6 C20,9.6 16,13 10,17.5 Z");

    /// <summary>Heart containers, each filled by quarters like the game counts them.</summary>
    public static UIElement Hearts(uint life, uint max)
    {
        var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300 };
        for (uint i = 0; i < Math.Min(max / 4, 20); i++)
        {
            double fill = Math.Clamp((life - Math.Min(life, i * 4)) / 4.0, 0, 1);
            var g = new Grid { Width = 20, Height = 18, Margin = new Thickness(0, 1, 3, 1) };
            g.Children.Add(new Path { Data = HeartShape, Fill = Hex("#33000000"), Stroke = HeartEdge, StrokeThickness = 1.2 });
            if (fill > 0)
                g.Children.Add(new Path
                {
                    Data = HeartShape, Fill = Heart,
                    Clip = new RectangleGeometry(new Rect(0, 0, 20 * fill, 18)),
                });
            wrap.Children.Add(g);
        }
        return wrap;
    }

    /// <summary>A green gem and the rupee count.</summary>
    public static UIElement Rupees(uint n)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var gem = new Grid { Width = 14, Height = 22, Margin = new Thickness(0, 0, 8, 0) };
        gem.Children.Add(new Polygon { Points = PointCollection.Parse("7,0 14,6 14,16 7,22 0,16 0,6"), Fill = Gem, Stroke = Hex("#D8FFE0"), StrokeThickness = 1 });
        gem.Children.Add(new Polygon { Points = PointCollection.Parse("7,3 11,7 11,15 7,19 3,15 3,7"), Fill = Hex("#66FFFFFF") });
        p.Children.Add(gem);
        p.Children.Add(new TextBlock { Text = n.ToString(), FontFamily = Serif, FontSize = 20, Foreground = Text, VerticalAlignment = VerticalAlignment.Center });
        return p;
    }

    // ---------------------------------------------------------------- the backdrop

    /// <summary>Moss-dark backdrop with three soft glows drifting slowly behind the glass (the Prism recipe).</summary>
    public static UIElement Backdrop()
    {
        var g = new Grid { IsHitTestVisible = false, ClipToBounds = true };
        g.Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#0F2116"), (Color)ColorConverter.ConvertFromString("#08120C"), 90);
        (string color, double size, HorizontalAlignment h, VerticalAlignment v, Thickness m, double dx, double dy, double secs)[] orbs =
        {
            ("#7A2F8F55", 900, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(-300, -320, 0, 0), 90, 60, 22),
            ("#5C7FB341", 780, HorizontalAlignment.Right, VerticalAlignment.Center, new Thickness(0, 0, -280, 0), -100, -70, 26),
            ("#4AD9B649", 700, HorizontalAlignment.Center, VerticalAlignment.Bottom, new Thickness(160, 0, 0, -340), 80, -40, 30),
        };
        foreach (var o in orbs)
        {
            var t = new TranslateTransform();
            var e = new Ellipse
            {
                Width = o.size, Height = o.size, HorizontalAlignment = o.h, VerticalAlignment = o.v, Margin = o.m, RenderTransform = t,
                Fill = new RadialGradientBrush((Color)ColorConverter.ConvertFromString(o.color), Colors.Transparent),
            };
            g.Children.Add(e);
            var dur = TimeSpan.FromSeconds(o.secs);
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-o.dx / 2, o.dx, dur) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-o.dy / 2, o.dy, dur) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        }
        return g;
    }

    /// <summary>Dark title bar in the forest colour (Windows 11).</summary>
    public static void DarkTitleBar(Window w)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        int on = 1, caption = 0x00162114;   // COLORREF 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);
        DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
