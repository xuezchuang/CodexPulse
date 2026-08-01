using System;
using System.ComponentModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace CodexPulse;

public sealed class MainWindow : Window
{
    private readonly UsageReader _reader;
    private readonly SystemMonitorReader _systemMonitorReader;
    private readonly WidgetSettings _settings;
    private readonly DispatcherTimer _usageRefreshTimer;
    private readonly DispatcherTimer _systemRefreshTimer;
    private readonly TextBlock _titleText;
    private readonly TextBlock _percentText;
    private readonly TextBlock _detailText;
    private readonly Path _progressArc;
    private readonly TextBlock _uploadText;
    private readonly TextBlock _downloadText;
    private readonly TextBlock _cpuText;
    private readonly TextBlock _cpuTemperatureText;
    private readonly TextBlock _memoryText;
    private readonly Border _memoryTile;
    private readonly MenuItem _startupMenuItem;
    private bool _isRefreshing;
    private bool _allowClose;
    private double _remainingFraction;

    public event Action<double?, string>? SnapshotChanged;
    public event Action<bool>? VisibilityChangedByUser;
    public event Action? StartupToggleRequested;
    public event Action? ExitRequested;

    public MainWindow(UsageReader reader, WidgetSettings settings)
    {
        _reader = reader;
        _systemMonitorReader = new SystemMonitorReader();
        _settings = settings;

        Title = "Codex Pulse";
        Width = 360;
        Height = 133;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        UseLayoutRounding = true;

        var panelBackground = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        panelBackground.GradientStops.Add(
            new GradientStop(Color.FromRgb(8, 14, 19), 0));
        panelBackground.GradientStops.Add(
            new GradientStop(Color.FromRgb(17, 25, 31), 0.58));
        panelBackground.GradientStops.Add(
            new GradientStop(Color.FromRgb(21, 29, 35), 1));

        var container = new Border
        {
            Width = 450,
            Height = 166,
            CornerRadius = new CornerRadius(16),
            Background = panelBackground,
            BorderBrush = new SolidColorBrush(Color.FromRgb(47, 58, 66)),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18,
                Color = Colors.Black,
                Opacity = 0.5,
                ShadowDepth = 4,
                Direction = 270
            }
        };

        var rootLayout = new Grid
        {
            Margin = new Thickness(18, 14, 18, 14)
        };
        rootLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        rootLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var ringLayout = new Grid
        {
            Width = 112,
            Height = 112,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };
        ringLayout.Children.Add(new Ellipse
        {
            Width = 104,
            Height = 104,
            Stroke = new SolidColorBrush(Color.FromRgb(47, 51, 49)),
            StrokeThickness = 7
        });

        _progressArc = new Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(255, 159, 10)),
            StrokeThickness = 7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Effect = new DropShadowEffect
            {
                BlurRadius = 8,
                Color = Color.FromRgb(255, 159, 10),
                Opacity = 0.35,
                ShadowDepth = 0
            }
        };
        ringLayout.Children.Add(_progressArc);

        _percentText = new TextBlock
        {
            Text = "--",
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 38,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ringLayout.Children.Add(_percentText);
        rootLayout.Children.Add(ringLayout);

        var contentLayout = new Grid();
        contentLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(73) });
        contentLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0) });
        contentLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(contentLayout, 1);

        var labels = new Grid
        {
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 0, 0)
        };
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleLayout = new StackPanel();
        _titleText = new TextBlock
        {
            Text = "Codex Left",
            Foreground = new SolidColorBrush(Color.FromRgb(239, 242, 245)),
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleLayout.Children.Add(_titleText);
        _detailText = new TextBlock
        {
            Text = "正在读取 Codex 额度…",
            Foreground = new SolidColorBrush(Color.FromRgb(151, 158, 166)),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 15.5,
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleLayout.Children.Add(_detailText);
        labels.Children.Add(titleLayout);

        var temperatureContents = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 1, 0, 0)
        };
        temperatureContents.Children.Add(new TextBlock
        {
            Text = "CPU",
            Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 190)),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        _cpuTemperatureText = new TextBlock
        {
            Text = "--°C",
            Foreground = new SolidColorBrush(Color.FromRgb(255, 235, 235)),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        temperatureContents.Children.Add(_cpuTemperatureText);
        var temperatureBadge = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(78, 26, 30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(129, 47, 52)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 5, 9, 5),
            ToolTip = "CPU Package 温度（Armoury Crate）",
            Child = temperatureContents
        };
        Grid.SetColumn(temperatureBadge, 1);
        labels.Children.Add(temperatureBadge);
        contentLayout.Children.Add(labels);

        var systemLayout = new Grid
        {
            Margin = new Thickness(0, 14, 0, 0)
        };
        systemLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        systemLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        systemLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
        systemLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        systemLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        systemLayout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });

        var networkPanel = new Grid
        {
            Margin = new Thickness(3, 0, 10, 0),
            ToolTip = "当前在线物理网卡的合计速度"
        };
        networkPanel.RowDefinitions.Add(new RowDefinition());
        networkPanel.RowDefinitions.Add(new RowDefinition());

        var downloadRow = CreateNetworkRow("↓", Color.FromRgb(0, 189, 255), out _downloadText);
        Grid.SetRow(downloadRow, 0);
        networkPanel.Children.Add(downloadRow);

        var uploadRow = CreateNetworkRow("↑", Color.FromRgb(80, 220, 98), out _uploadText);
        Grid.SetRow(uploadRow, 1);
        networkPanel.Children.Add(uploadRow);
        Grid.SetColumn(networkPanel, 0);
        systemLayout.Children.Add(networkPanel);

        var systemDivider = new Border
        {
            Width = 1,
            Height = 30,
            Background = new SolidColorBrush(Color.FromRgb(35, 46, 54)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(systemDivider, 1);
        systemLayout.Children.Add(systemDivider);

        var cpuTile = CreateStatTile(
            "CPU",
            Color.FromRgb(10, 47, 70),
            out _cpuText);
        cpuTile.ToolTip = "全系统 CPU 使用率";
        Grid.SetColumn(cpuTile, 3);
        systemLayout.Children.Add(cpuTile);

        _memoryTile = CreateStatTile(
            "RAM",
            Color.FromRgb(27, 73, 30),
            out _memoryText);
        _memoryTile.ToolTip = "物理内存使用率";
        Grid.SetColumn(_memoryTile, 5);
        systemLayout.Children.Add(_memoryTile);

        Grid.SetRow(systemLayout, 2);
        contentLayout.Children.Add(systemLayout);
        rootLayout.Children.Add(contentLayout);

        container.Child = rootLayout;
        Content = new Viewbox
        {
            Stretch = Stretch.Fill,
            Child = container
        };

        var contextMenu = new ContextMenu();
        var refreshItem = new MenuItem { Header = "立即刷新 Codex 用量" };
        refreshItem.Click += (_, _) => RefreshNow();
        contextMenu.Items.Add(refreshItem);

        var hideItem = new MenuItem { Header = "隐藏到托盘" };
        hideItem.Click += (_, _) =>
        {
            Hide();
            VisibilityChangedByUser?.Invoke(false);
        };
        contextMenu.Items.Add(hideItem);

        var resetItem = new MenuItem { Header = "恢复默认位置" };
        resetItem.Click += (_, _) => ResetPosition();
        contextMenu.Items.Add(resetItem);

        _startupMenuItem = new MenuItem
        {
            Header = "开机启动",
            IsCheckable = true,
            IsChecked = StartupManager.IsEnabled()
        };
        _startupMenuItem.Click += (_, _) => StartupToggleRequested?.Invoke();
        contextMenu.Items.Add(_startupMenuItem);

        contextMenu.Items.Add(new Separator());
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        contextMenu.Items.Add(exitItem);
        ContextMenu = contextMenu;

        Loaded += OnLoaded;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        Closing += OnClosing;
        IsVisibleChanged += (_, _) => VisibilityChangedByUser?.Invoke(IsVisible);

        _usageRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _usageRefreshTimer.Tick += (_, _) => RefreshNow();

        _systemRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _systemRefreshTimer.Tick += (_, _) => UpdateSystemStats();
    }

    private static Grid CreateNetworkRow(string arrow, Color arrowColor, out TextBlock valueText)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var arrowText = new TextBlock
        {
            Text = arrow,
            Foreground = new SolidColorBrush(arrowColor),
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(arrowText);

        valueText = new TextBlock
        {
            Text = "0.00 KB/s",
            Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Left
        };
        Grid.SetColumn(valueText, 1);
        row.Children.Add(valueText);
        return row;
    }

    private static Border CreateStatTile(
        string title,
        Color backgroundColor,
        out TextBlock valueText)
    {
        var contents = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        contents.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = new SolidColorBrush(Color.FromRgb(238, 244, 248)),
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center
        });

        valueText = new TextBlock
        {
            Text = "0%",
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 15.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        contents.Children.Add(valueText);

        return new Border
        {
            Background = new SolidColorBrush(backgroundColor),
            CornerRadius = new CornerRadius(6),
            Child = contents
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RestoreOrSetDefaultPosition();
        _usageRefreshTimer.Start();
        _systemRefreshTimer.Start();
        RefreshNow();
        UpdateSystemStats();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ResetPosition();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        try
        {
            DragMove();
            SavePosition();
        }
        catch (InvalidOperationException)
        {
            // The mouse may have been released between the event and DragMove.
        }
    }

    public async void RefreshNow()
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            var snapshot = await Task.Run(_reader.ReadLatest);
            if (snapshot is null)
            {
                return;
            }

            ShowSnapshot(snapshot);
        }
        catch (Exception)
        {
            // Keep the last successfully rendered usage snapshot.
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void UpdateSystemStats()
    {
        try
        {
            var snapshot = _systemMonitorReader.Sample();
            _uploadText.Text = FormatRate(snapshot.UploadBytesPerSecond);
            _downloadText.Text = FormatRate(snapshot.DownloadBytesPerSecond);
            _cpuText.Text = $"{snapshot.CpuPercent:0}%";
            _cpuTemperatureText.Text = snapshot.CpuTemperatureCelsius is { } temperature
                ? $"{temperature:0}°C"
                : "--°C";
            _memoryText.Text = $"{snapshot.MemoryPercent:0}%";
            _memoryTile.Background = new SolidColorBrush(snapshot.MemoryPercent switch
            {
                < 75 => Color.FromRgb(27, 73, 30),
                < 90 => Color.FromRgb(111, 70, 8),
                _ => Color.FromRgb(112, 27, 27)
            });
        }
        catch
        {
            _uploadText.Text = "--";
            _downloadText.Text = "--";
            _cpuText.Text = "--";
            _cpuTemperatureText.Text = "--°C";
            _memoryText.Text = "--";
        }
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024 * 1024)
        {
            var kilobytes = bytesPerSecond / 1024;
            var format = kilobytes switch
            {
                < 10 => "0.00",
                < 100 => "0.0",
                _ => "0"
            };
            return $"{kilobytes.ToString(format, CultureInfo.InvariantCulture)} KB/s";
        }

        if (bytesPerSecond < 1024d * 1024 * 1024)
        {
            return $"{bytesPerSecond / (1024 * 1024):0.00} MB/s";
        }

        return $"{bytesPerSecond / (1024d * 1024 * 1024):0.00} GB/s";
    }

    private void ShowSnapshot(UsageSnapshot snapshot)
    {
        var remaining = Math.Clamp(100 - snapshot.UsedPercent, 0, 100);
        _remainingFraction = remaining / 100;
        _percentText.Text = $"{Math.Round(remaining, MidpointRounding.AwayFromZero):0}%";
        _titleText.Text = "Codex";

        var accent = remaining switch
        {
            > 10 => Color.FromRgb(255, 159, 10),
            _ => Color.FromRgb(248, 113, 113)
        };
        _progressArc.Stroke = new SolidColorBrush(accent);
        UpdateProgressArc();

        var resetText = FormatReset(snapshot.ResetsAt);
        _detailText.Text = resetText;
        ToolTip = $"Codex 剩余 {remaining:0.#}%{Environment.NewLine}{resetText}";

        SnapshotChanged?.Invoke(
            remaining,
            $"Codex 剩余 {Math.Round(remaining, MidpointRounding.AwayFromZero):0}%");
    }

    private static string FormatReset(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null)
        {
            return string.Empty;
        }

        var localReset = resetsAt.Value.ToLocalTime();
        if (localReset <= DateTimeOffset.Now)
        {
            return "等待额度刷新";
        }

        return localReset.ToString("M/d HH:mm 重置", CultureInfo.GetCultureInfo("zh-CN"));
    }

    private void UpdateProgressArc()
    {
        const double center = 56;
        const double radius = 52;
        var sweepAngle = Math.Clamp(_remainingFraction, 0, 1) * 360;

        if (sweepAngle <= 0)
        {
            _progressArc.Data = Geometry.Empty;
            return;
        }

        if (sweepAngle >= 359.99)
        {
            _progressArc.Data = new EllipseGeometry(
                new Point(center, center),
                radius,
                radius);
            return;
        }

        var startPoint = PointOnCircle(center, radius, -90);
        var endPoint = PointOnCircle(center, radius, -90 + sweepAngle);
        var figure = new PathFigure
        {
            StartPoint = startPoint,
            IsClosed = false,
            IsFilled = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = endPoint,
            Size = new Size(radius, radius),
            IsLargeArc = sweepAngle > 180,
            SweepDirection = SweepDirection.Clockwise
        });

        _progressArc.Data = new PathGeometry(new[] { figure });
    }

    private static Point PointOnCircle(double center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180;
        return new Point(
            center + radius * Math.Cos(radians),
            center + radius * Math.Sin(radians));
    }

    private void RestoreOrSetDefaultPosition()
    {
        if (_settings.Left is not null &&
            _settings.Top is not null &&
            IsPositionOnVirtualScreen(_settings.Left.Value, _settings.Top.Value, Width, Height))
        {
            Left = _settings.Left.Value;
            Top = _settings.Top.Value;
            return;
        }

        SetDefaultPosition();
    }

    public void ResetPosition()
    {
        SetDefaultPosition();
        SavePosition();
    }

    private void SetDefaultPosition()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 16;
        Top = workArea.Bottom - Height - 12;
    }

    private static bool IsPositionOnVirtualScreen(
        double left,
        double top,
        double width,
        double height)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        return left >= virtualLeft &&
               top >= virtualTop &&
               left + width <= virtualRight &&
               top + height <= virtualBottom;
    }

    private void SavePosition()
    {
        _settings.Left = Left;
        _settings.Top = Top;
        SettingsStore.Save(_settings);
    }

    public void UpdateStartupMenuState(bool enabled)
    {
        _startupMenuItem.IsChecked = enabled;
    }

    public void CloseForExit()
    {
        _allowClose = true;
        _usageRefreshTimer.Stop();
        _systemRefreshTimer.Stop();
        _systemMonitorReader.Dispose();
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
        VisibilityChangedByUser?.Invoke(false);
    }
}
