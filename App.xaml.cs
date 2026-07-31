using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace CodexPulse;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\CodexPulse_9CC75E90_2B8A_45CE_A307_E6A5A6788D70";

    private Mutex? _singleInstanceMutex;
    private MainWindow? _window;
    private Forms.NotifyIcon? _notifyIcon;
    private Icon? _notifyIconImage;
    private Forms.ToolStripMenuItem? _visibilityMenuItem;
    private Forms.ToolStripMenuItem? _startupMenuItem;
    private bool _isExiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        var reader = new UsageReader();
        var settings = SettingsStore.Load();
        _window = new MainWindow(reader, settings);
        _window.SnapshotChanged += OnSnapshotChanged;
        _window.VisibilityChangedByUser += UpdateVisibilityMenuText;
        _window.StartupToggleRequested += ToggleStartup;
        _window.ExitRequested += ExitApplication;

        CreateNotifyIcon(reader.SessionRoot);
        _window.Show();
    }

    private void CreateNotifyIcon(string sessionRoot)
    {
        _notifyIconImage = CreateUsageIcon(null, Color.FromArgb(83, 92, 104));
        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = _notifyIconImage,
            Text = "Codex Pulse：正在读取…",
            Visible = true
        };

        var menu = new Forms.ContextMenuStrip();
        _visibilityMenuItem = new Forms.ToolStripMenuItem("隐藏小组件");
        _visibilityMenuItem.Click += (_, _) => Dispatcher.Invoke(ToggleWindowVisibility);
        menu.Items.Add(_visibilityMenuItem);

        var refreshItem = new Forms.ToolStripMenuItem("立即刷新");
        refreshItem.Click += (_, _) => Dispatcher.Invoke(() => _window?.RefreshNow());
        menu.Items.Add(refreshItem);

        var resetItem = new Forms.ToolStripMenuItem("恢复默认位置");
        resetItem.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            EnsureWindowVisible();
            _window?.ResetPosition();
        });
        menu.Items.Add(resetItem);

        _startupMenuItem = new Forms.ToolStripMenuItem("开机启动")
        {
            Checked = StartupManager.IsEnabled()
        };
        _startupMenuItem.Click += (_, _) => Dispatcher.Invoke(ToggleStartup);
        menu.Items.Add(_startupMenuItem);

        var openDataItem = new Forms.ToolStripMenuItem("打开 Codex 数据目录");
        openDataItem.Click += (_, _) =>
        {
            if (!Directory.Exists(sessionRoot))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = sessionRoot,
                UseShellExecute = true
            });
        };
        menu.Items.Add(openDataItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        var exitItem = new Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Dispatcher.Invoke(ExitApplication);
        menu.Items.Add(exitItem);

        menu.Opening += (_, _) =>
        {
            if (_startupMenuItem is not null)
            {
                _startupMenuItem.Checked = StartupManager.IsEnabled();
            }
        };

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ToggleWindowVisibility);
    }

    private void ToggleWindowVisibility()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.IsVisible)
        {
            _window.Hide();
        }
        else
        {
            EnsureWindowVisible();
        }

        UpdateVisibilityMenuText(_window.IsVisible);
    }

    private void EnsureWindowVisible()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        _window.Topmost = true;
    }

    private void UpdateVisibilityMenuText(bool isVisible)
    {
        if (_visibilityMenuItem is not null)
        {
            _visibilityMenuItem.Text = isVisible ? "隐藏小组件" : "显示小组件";
        }
    }

    private void ToggleStartup()
    {
        var enable = !StartupManager.IsEnabled();
        StartupManager.SetEnabled(enable);

        if (_startupMenuItem is not null)
        {
            _startupMenuItem.Checked = enable;
        }

        _window?.UpdateStartupMenuState(enable);
    }

    private void OnSnapshotChanged(double? remainingPercent, string tooltip)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var color = remainingPercent switch
        {
            null => Color.FromArgb(83, 92, 104),
            > 30 => Color.FromArgb(45, 212, 191),
            > 10 => Color.FromArgb(245, 158, 11),
            _ => Color.FromArgb(248, 113, 113)
        };

        var newIcon = CreateUsageIcon(remainingPercent, color);
        var oldIcon = _notifyIconImage;
        _notifyIconImage = newIcon;
        _notifyIcon.Icon = newIcon;
        oldIcon?.Dispose();
        _notifyIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    private void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _notifyIcon?.Dispose();
        _notifyIconImage?.Dispose();
        _window?.CloseForExit();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _notifyIconImage?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static Icon CreateUsageIcon(double? remainingPercent, Color accent)
    {
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        using var background = new SolidBrush(Color.FromArgb(255, 35, 39, 46));
        graphics.FillEllipse(background, 2, 2, 60, 60);

        using var ringPen = new Pen(Color.FromArgb(110, 118, 129), 5);
        graphics.DrawArc(ringPen, 5, 5, 54, 54, -90, 360);

        if (remainingPercent is not null)
        {
            using var accentPen = new Pen(accent, 5)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawArc(
                accentPen,
                5,
                5,
                54,
                54,
                -90,
                (float)(360 * Math.Clamp(remainingPercent.Value, 0, 100) / 100));
        }

        var text = remainingPercent is null
            ? "C"
            : Math.Round(remainingPercent.Value, MidpointRounding.AwayFromZero).ToString("0");
        var fontSize = text.Length switch
        {
            1 => 27f,
            2 => 23f,
            _ => 17f
        };
        using var font = new Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        var bounds = new RectangleF(7, 7, 50, 50);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        graphics.DrawString(text, font, textBrush, bounds, format);

        var iconHandle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(iconHandle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
