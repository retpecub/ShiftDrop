using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ShiftDrop.Core;

namespace ShiftDrop.UI;

/// <summary>Маленькое уведомление в правом нижнем углу: прогресс задачи и результат.</summary>
public sealed class ToastWindow : Window
{
    private const double ToastWidth = 340;
    private static readonly List<ToastWindow> Open = new();

    private readonly CancellationTokenSource? _cts;
    private readonly TextBlock _title;
    private readonly TextBlock _status;
    private readonly ProgressBar _bar;
    private readonly Button _showButton;
    private readonly Button _closeButton;
    private IReadOnlyList<string> _outputs = Array.Empty<string>();
    private bool _finished;

    public ToastWindow(string title, CancellationTokenSource? cts)
    {
        _cts = cts;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = ToastWidth;
        SizeToContent = SizeToContent.Height;
        FontFamily = Theme.Font;

        _title = new TextBlock { Text = title, Foreground = Theme.Text, FontSize = 14, FontWeight = FontWeights.SemiBold };
        _status = new TextBlock { Foreground = Theme.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MaxHeight = 120 };
        _bar = new ProgressBar { Height = 4, Margin = new Thickness(0, 10, 0, 0), IsIndeterminate = true, Foreground = Theme.AccentSolid, Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0), Minimum = 0, Maximum = 1 };

        _showButton = MakeButton("Показать");
        _showButton.Visibility = Visibility.Collapsed;
        _showButton.Click += (_, _) => ShowInExplorer();

        _closeButton = MakeButton(cts != null ? "Отмена" : "Закрыть");
        _closeButton.Click += (_, _) =>
        {
            if (!_finished) _cts?.Cancel();
            else Close();
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_showButton);
        buttons.Children.Add(_closeButton);

        var stack = new StackPanel();
        stack.Children.Add(_title);
        stack.Children.Add(_status);
        stack.Children.Add(_bar);
        stack.Children.Add(buttons);

        Content = new Border
        {
            Background = Theme.Panel,
            CornerRadius = new CornerRadius(12),
            BorderBrush = Theme.SectorStroke,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(8),
            Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, Opacity = 0.45, ShadowDepth = 2 },
        };

        Loaded += (_, _) => Reposition();
        SizeChanged += (_, _) => Reposition();
        Closed += (_, _) =>
        {
            Open.Remove(this);
            foreach (var t in Open) t.Reposition();
        };
        Open.Add(this);
    }

    private static Button MakeButton(string text)
    {
        return new Button
        {
            Content = text,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 4, 12, 4),
            Foreground = Theme.Text,
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom;
        foreach (var t in Open)
        {
            if (t == this) break;
            bottom -= t.ActualHeight;
        }
        Left = area.Right - Width;
        Top = bottom - ActualHeight;
    }

    private void Ui(Action a)
    {
        if (Dispatcher.CheckAccess()) a();
        else Dispatcher.BeginInvoke(a);
    }

    public void SetStatus(string text) => Ui(() => _status.Text = text);

    public void SetProgress(double? value) => Ui(() =>
    {
        if (value is null)
        {
            _bar.IsIndeterminate = true;
        }
        else
        {
            _bar.IsIndeterminate = false;
            _bar.Value = value.Value;
        }
    });

    public void Done(IReadOnlyList<string> outputs, IReadOnlyList<string> errors) => Ui(() =>
    {
        _finished = true;
        _outputs = outputs.ToList();
        _bar.Visibility = Visibility.Collapsed;
        _closeButton.Content = "Закрыть";

        var parts = new List<string>();
        if (outputs.Count == 1) parts.Add("Готово: " + Path.GetFileName(outputs[0]));
        else if (outputs.Count > 1) parts.Add($"Готово: {outputs.Count} {PathUtil.Plural(outputs.Count, "файл", "файла", "файлов")}");
        if (errors.Count > 0)
        {
            parts.Add("Ошибки:\n" + string.Join("\n", errors.Take(4)) + (errors.Count > 4 ? $"\n…и ещё {errors.Count - 4}" : ""));
            _status.Foreground = Theme.Error;
        }
        if (parts.Count == 0) parts.Add("Нечего делать");
        _status.Text = string.Join("\n", parts);
        _showButton.Visibility = outputs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        AutoClose(errors.Count > 0 ? 15 : 6);
    });

    public void Cancelled(IReadOnlyList<string> outputs) => Ui(() =>
    {
        _finished = true;
        _outputs = outputs.ToList();
        _bar.Visibility = Visibility.Collapsed;
        _closeButton.Content = "Закрыть";
        _status.Text = outputs.Count > 0 ? $"Отменено. Успело: {outputs.Count}" : "Отменено";
        _showButton.Visibility = outputs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutoClose(4);
    });

    /// <summary>Простое информационное уведомление.</summary>
    public static void Info(string title, string text, int seconds = 5)
    {
        var t = new ToastWindow(title, null);
        t.Show();
        t._finished = true;
        t._bar.Visibility = Visibility.Collapsed;
        t._status.Text = text;
        t.AutoClose(seconds);
    }

    private void AutoClose(int seconds)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (IsMouseOver) { timer.Start(); return; } // не закрываем, пока над ним курсор
            Close();
        };
        timer.Start();
    }

    private void ShowInExplorer()
    {
        var first = _outputs.FirstOrDefault(File.Exists);
        if (first == null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{first}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("explorer: " + ex.Message);
        }
        Close();
    }
}
