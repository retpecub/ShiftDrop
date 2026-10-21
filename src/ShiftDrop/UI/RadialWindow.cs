using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using ShiftDrop.Core;

namespace ShiftDrop.UI;

/// <summary>
/// Круговое меню.
/// Drag-режим: живёт постоянно, появляется под курсором при Shift+перетаскивании и принимает drop.
/// Click-режим: открывается из контекстного меню Проводника, выбор кликом.
/// </summary>
public sealed class RadialWindow : Window
{
    public enum RadialMode { Drag, Click }

    private const double SizeDip = 400;
    private const double ROuter = 172;
    private const double RInner = 62;
    private const double C = SizeDip / 2;
    private const double GapDeg = 1.6;

    private readonly RadialMode _mode;
    private readonly Canvas _ring = new() { Width = SizeDip, Height = SizeDip, IsHitTestVisible = false };
    private readonly Grid _visual = new() { Width = SizeDip, Height = SizeDip };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TextBlock _hubTitle;
    private readonly TextBlock _hubHint;

    private readonly List<(Path path, TextBlock label)> _sectors = new();
    private List<ActionDef> _items = new();
    private IReadOnlyList<string> _files = Array.Empty<string>();
    private IReadOnlyList<string> _all = Array.Empty<string>();
    private FileKind _kind;
    private bool _tools;
    private int _hover = -2;

    private IntPtr _hwnd;
    private double _dpi = 1;
    private int _cx, _cy;
    private bool _closing;

    /// <summary>Drag-режим: окно сейчас «вооружено» (показано под курсором).</summary>
    public bool IsArmed { get; private set; }

    /// <summary>Drag-режим: в окно пришли файлы, меню видно.</summary>
    public bool HasFiles { get; private set; }

    public RadialWindow(RadialMode mode)
    {
        _mode = mode;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = Height = SizeDip;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -20000;
        Top = -20000;
        ShowActivated = mode == RadialMode.Click;
        AllowDrop = mode == RadialMode.Drag;
        FontFamily = Theme.Font;
        UseLayoutRounding = true;

        var root = new Grid { Width = SizeDip, Height = SizeDip };

        // Почти прозрачный круг: окно должно «ловить» мышь и drop даже когда меню ещё не видно.
        root.Children.Add(new Ellipse
        {
            Width = (ROuter + 14) * 2,
            Height = (ROuter + 14) * 2,
            Fill = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        _visual.Children.Add(_ring);
        _visual.Children.Add(new Ellipse
        {
            Width = RInner * 2 - 8,
            Height = RInner * 2 - 8,
            Fill = Theme.Hub,
            Stroke = Theme.AccentSolid,
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        });

        _hubTitle = new TextBlock { Foreground = Theme.Text, FontSize = 13, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _hubHint = new TextBlock { Foreground = Theme.TextDim, FontSize = 10.5, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        var hubText = new StackPanel
        {
            Width = RInner * 2 - 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        hubText.Children.Add(_hubTitle);
        hubText.Children.Add(_hubHint);
        _visual.Children.Add(hubText);

        _visual.RenderTransformOrigin = new Point(0.5, 0.5);
        _visual.RenderTransform = _scale;
        _visual.Effect = new DropShadowEffect { BlurRadius = 22, Opacity = 0.5, ShadowDepth = 3 };
        _visual.IsHitTestVisible = false;
        root.Children.Add(_visual);
        Content = root;

        SourceInitialized += (_, _) => ApplyExStyle();

        if (mode == RadialMode.Click)
        {
            MouseMove += (_, e) => SetHover(HitTest(e.GetPosition(this)));
            MouseLeftButtonUp += OnClickUp;
            KeyDown += OnKey;
            Deactivated += (_, _) => SafeClose();
        }
    }

    // ---------------- окно ----------------

    private void ApplyExStyle()
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
        if (_mode == RadialMode.Drag) ex |= Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Поставить центр окна в физическую точку экрана (px) с учётом DPI монитора.</summary>
    private void PlaceCenterAt(int px, int py)
    {
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, px, py, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        uint dpi = Native.GetDpiForWindow(_hwnd);
        _dpi = dpi == 0 ? 1.0 : dpi / 96.0;
        int half = (int)Math.Round(SizeDip * _dpi / 2);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, px - half, py - half, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _cx = px;
        _cy = py;
    }

    // ---------------- Drag-режим ----------------

    /// <summary>Показать невидимую «ловушку» под курсором; меню проявится, когда придут файлы.</summary>
    public void Arm(int px, int py)
    {
        IsArmed = true;
        HasFiles = false;
        _tools = false;
        _files = Array.Empty<string>();
        _all = Array.Empty<string>();
        _items = new List<ActionDef>();
        _hover = -2;
        StopAnimations();
        _visual.Opacity = 0;
        _ring.Children.Clear();
        _sectors.Clear();

        if (!IsVisible) Show();
        PlaceCenterAt(px, py);
    }

    public void Disarm()
    {
        if (!IsArmed) return;
        IsArmed = false;
        HasFiles = false;
        Hide();
    }

    /// <summary>Курсор (физ. px) от хука: если ушёл далеко от меню — убираемся и не мешаем обычному drop.</summary>
    public void OnHookMove(int px, int py)
    {
        if (!IsArmed) return;
        double dx = (px - _cx) / _dpi, dy = (py - _cy) / _dpi;
        if (Math.Sqrt(dx * dx + dy * dy) > ROuter + 36) Disarm();
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (_mode != RadialMode.Drag || !IsArmed)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (!HasFiles && e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            HasFiles = true;
            var (kind, files, all) = FileKinds.Classify(paths);
            _kind = kind;
            _files = files;
            _all = all;
            _tools = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
            Rebuild();
            AnimateIn();
        }
        UpdateDrag(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        UpdateDrag(e);
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        SetHover(-2);
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        e.Handled = true;
        if (!IsArmed || !HasFiles)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        int idx = HitTest(e.GetPosition(this));
        if (idx >= 0 && idx < _items.Count)
        {
            e.Effects = DragDropEffects.Copy; // никогда Move — исходник остаётся на месте
            var action = _items[idx];
            var files = action.UsesAllItems ? _all : _files;
            Disarm();
            Dispatcher.InvokeAsync(() => Jobs.Start(action, files)); // отпускаем OLE-вызов сразу
        }
        else
        {
            e.Effects = DragDropEffects.None;
            Disarm();
        }
    }

    private void UpdateDrag(DragEventArgs e)
    {
        e.Handled = true;
        if (!IsArmed || !HasFiles)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        bool ctrl = (e.KeyStates & DragDropKeyStates.ControlKey) != 0;
        if (ctrl != _tools)
        {
            _tools = ctrl;
            Rebuild();
        }

        int idx = HitTest(e.GetPosition(this));
        SetHover(idx);
        e.Effects = idx >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    // ---------------- Click-режим ----------------

    public void ShowForClick(IReadOnlyList<string> paths, int px, int py)
    {
        var (kind, files, all) = FileKinds.Classify(paths);
        _kind = kind;
        _files = files;
        _all = all;
        _tools = false;
        Rebuild();
        Show();
        PlaceCenterAt(px, py);
        Activate();
        Native.SetForegroundWindow(_hwnd);
        Focus();
        AnimateIn();
    }

    private void OnClickUp(object sender, MouseButtonEventArgs e)
    {
        int idx = HitTest(e.GetPosition(this));
        if (idx == -1)
        {
            _tools = !_tools; // клик по центру — переключить форматы/инструменты
            Rebuild();
            return;
        }
        if (idx >= 0 && idx < _items.Count)
        {
            var action = _items[idx];
            var files = action.UsesAllItems ? _all : _files;
            SafeClose();
            Jobs.Start(action, files);
            return;
        }
        SafeClose();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) SafeClose();
        else if (e.Key is Key.Tab or Key.LeftCtrl or Key.RightCtrl)
        {
            _tools = !_tools;
            Rebuild();
            e.Handled = true;
        }
    }

    /// <summary>Клик мимо меню (из хука), физические px.</summary>
    public void OnOutsideClick(int px, int py)
    {
        if (_mode != RadialMode.Click || _closing || !IsVisible) return;
        double dx = (px - _cx) / _dpi, dy = (py - _cy) / _dpi;
        if (Math.Sqrt(dx * dx + dy * dy) > ROuter + 14) SafeClose();
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Dispatcher.InvokeAsync(Close);
    }

    // ---------------- построение меню ----------------

    private void Rebuild()
    {
        _items = _kind == FileKind.Unknown ? new List<ActionDef>() : Menus.Get(_kind, _tools, _files, _all);
        _ring.Children.Clear();
        _sectors.Clear();
        _hover = -2;

        int n = _items.Count;
        for (int i = 0; i < n; i++)
        {
            var path = new Path
            {
                Data = SectorGeometry(i, n),
                Fill = Theme.Sector,
                Stroke = Theme.SectorStroke,
                StrokeThickness = 1,
            };
            _ring.Children.Add(path);

            var label = new TextBlock
            {
                Text = _items[i].Label,
                Foreground = Theme.Text,
                FontSize = n > 8 ? 12.5 : 13.5,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Width = 84,
            };
            double mid = SectorMidAngle(i, n) * Math.PI / 180;
            double rm = (ROuter + RInner) / 2 + 4;
            label.Measure(new Size(84, double.PositiveInfinity));
            Canvas.SetLeft(label, C + rm * Math.Cos(mid) - 42);
            Canvas.SetTop(label, C + rm * Math.Sin(mid) - label.DesiredSize.Height / 2);
            _ring.Children.Add(label);

            _sectors.Add((path, label));
        }

        UpdateHub();
    }

    private void UpdateHub()
    {
        if (_kind == FileKind.Unknown)
        {
            _hubTitle.Text = "Не поддерживается";
            _hubHint.Text = "";
            return;
        }

        if (_hover >= 0 && _hover < _items.Count)
        {
            _hubTitle.Text = _items[_hover].Label;
            _hubHint.Text = _items[_hover].Tip ?? "";
            return;
        }

        var count = _kind == FileKind.Other ? _all.Count : _files.Count;
        var title = _kind == FileKind.Other && count == 1
            ? (System.IO.Directory.Exists(_all[0]) ? "Папка" : "Файл")
            : FileKinds.Title(_kind);
        _hubTitle.Text = count > 1 ? $"{title} ×{count}" : title;

        if (_items.Count == 0)
        {
            _hubHint.Text = _tools ? "Нет инструментов" :
                _kind is FileKind.Document or FileKind.Spreadsheet or FileKind.Presentation ? "Нужен MS Office или LibreOffice" : "Нет форматов";
        }
        else if (_mode == RadialMode.Drag)
        {
            _hubHint.Text = _tools ? "Ctrl отпустить — форматы" : "Ctrl — инструменты";
        }
        else
        {
            _hubHint.Text = _tools ? "Клик — форматы" : "Клик — инструменты";
        }
    }

    private void SetHover(int idx)
    {
        if (idx == _hover) return;
        if (_hover >= 0 && _hover < _sectors.Count)
            _sectors[_hover].path.Fill = Theme.Sector;
        _hover = idx;
        if (_hover >= 0 && _hover < _sectors.Count)
            _sectors[_hover].path.Fill = Theme.Accent;
        UpdateHub();
    }

    /// <summary>-1 — центр, -2 — мимо, иначе индекс сектора. Точка — в DIP относительно окна.</summary>
    private int HitTest(Point p)
    {
        double dx = p.X - C, dy = p.Y - C;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d < RInner) return -1;
        int n = _items.Count;
        if (d > ROuter + 10 || n == 0) return -2;

        double step = 360.0 / n;
        double angle = Math.Atan2(dy, dx) * 180 / Math.PI; // 0 — вправо, 90 — вниз
        double rel = angle + 90 + step / 2;
        rel = ((rel % 360) + 360) % 360;
        return Math.Min(n - 1, (int)(rel / step));
    }

    private static double SectorMidAngle(int i, int n) => -90 + i * (360.0 / n);

    private static Geometry SectorGeometry(int i, int n)
    {
        if (n == 1)
        {
            var ring = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(C, C), ROuter, ROuter),
                new EllipseGeometry(new Point(C, C), RInner, RInner));
            ring.Freeze();
            return ring;
        }

        double step = 360.0 / n;
        double a0 = SectorMidAngle(i, n) - step / 2 + GapDeg / 2;
        double a1 = a0 + step - GapDeg;
        bool large = (a1 - a0) > 180;

        var fig = new PathFigure { StartPoint = Polar(ROuter, a0), IsClosed = true };
        fig.Segments.Add(new ArcSegment(Polar(ROuter, a1), new Size(ROuter, ROuter), 0, large, SweepDirection.Clockwise, true));
        fig.Segments.Add(new LineSegment(Polar(RInner, a1), true));
        fig.Segments.Add(new ArcSegment(Polar(RInner, a0), new Size(RInner, RInner), 0, large, SweepDirection.Counterclockwise, true));

        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    private static Point Polar(double r, double deg)
    {
        double a = deg * Math.PI / 180;
        return new Point(C + r * Math.Cos(a), C + r * Math.Sin(a));
    }

    // ---------------- анимация ----------------

    private void StopAnimations()
    {
        _visual.BeginAnimation(OpacityProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    private void AnimateIn()
    {
        StopAnimations();
        var dur = TimeSpan.FromMilliseconds(140);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        _visual.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
        var grow = new DoubleAnimation(0.82, 1, dur) { EasingFunction = ease };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }
}
