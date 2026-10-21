using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ShiftDrop.UI;

/// <summary>Окно обрезки: выделить прямоугольник мышью, Enter — применить, Esc — отмена.</summary>
public sealed class CropWindow : Window
{
    private readonly int _pw, _ph, _ow, _oh;
    private readonly Canvas _canvas;
    private readonly Rectangle _sel;
    private readonly TextBlock _info;
    private Point? _start;

    public Int32Rect? Result { get; private set; }

    public CropWindow(byte[] previewPng, int pw, int ph, int ow, int oh, string fileName)
    {
        _pw = pw; _ph = ph; _ow = ow; _oh = oh;
        Title = "Обрезка — " + fileName;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Background = Theme.Panel;
        FontFamily = Theme.Font;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(previewPng);
        bmp.EndInit();
        bmp.Freeze();

        _canvas = new Canvas { Width = pw, Height = ph, Background = Brushes.Black, Cursor = Cursors.Cross, ClipToBounds = true };
        _canvas.Children.Add(new Image { Source = bmp, Width = pw, Height = ph, Stretch = Stretch.Fill });
        _sel = new Rectangle
        {
            Stroke = Theme.AccentSolid,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0x86, 0x3C)),
            Visibility = Visibility.Collapsed,
        };
        _canvas.Children.Add(_sel);
        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += (_, _) => { _start = null; _canvas.ReleaseMouseCapture(); };

        _info = new TextBlock { Foreground = Theme.TextDim, VerticalAlignment = VerticalAlignment.Center, Text = "Выделите область мышью" };

        var ok = new Button { Content = "Обрезать", Padding = new Thickness(16, 6, 16, 6), IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => Apply();
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(16, 6, 16, 6), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };

        var bottom = new DockPanel { Margin = new Thickness(12, 10, 12, 12), LastChildFill = true };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        DockPanel.SetDock(btns, Dock.Right);
        bottom.Children.Add(btns);
        bottom.Children.Add(_info);

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new Border { Margin = new Thickness(12, 12, 12, 0), Child = _canvas });
        Content = root;

        Loaded += (_, _) => Activate();
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(_canvas);
        _canvas.CaptureMouse();
        _sel.Visibility = Visibility.Visible;
        UpdateRect(_start.Value);
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start == null || e.LeftButton != MouseButtonState.Pressed) return;
        UpdateRect(e.GetPosition(_canvas));
    }

    private void UpdateRect(Point p)
    {
        var s = _start!.Value;
        double x1 = Math.Clamp(Math.Min(s.X, p.X), 0, _pw), y1 = Math.Clamp(Math.Min(s.Y, p.Y), 0, _ph);
        double x2 = Math.Clamp(Math.Max(s.X, p.X), 0, _pw), y2 = Math.Clamp(Math.Max(s.Y, p.Y), 0, _ph);
        Canvas.SetLeft(_sel, x1);
        Canvas.SetTop(_sel, y1);
        _sel.Width = x2 - x1;
        _sel.Height = y2 - y1;
        var r = ToOriginal();
        _info.Text = r is { } rr ? $"{rr.Width} × {rr.Height} px" : "Выделите область мышью";
    }

    private Int32Rect? ToOriginal()
    {
        if (_sel.Visibility != Visibility.Visible || _sel.Width < 2 || _sel.Height < 2) return null;
        double kx = (double)_ow / _pw, ky = (double)_oh / _ph;
        int x = (int)Math.Round(Canvas.GetLeft(_sel) * kx);
        int y = (int)Math.Round(Canvas.GetTop(_sel) * ky);
        int w = (int)Math.Round(_sel.Width * kx);
        int h = (int)Math.Round(_sel.Height * ky);
        w = Math.Clamp(w, 1, _ow - x);
        h = Math.Clamp(h, 1, _oh - y);
        return new Int32Rect(x, y, w, h);
    }

    private void Apply()
    {
        Result = ToOriginal();
        if (Result == null)
        {
            _info.Text = "Сначала выделите область";
            return;
        }
        DialogResult = true;
    }
}
