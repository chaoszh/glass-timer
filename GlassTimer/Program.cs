using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("GlassTimer.Tests")]

namespace GlassTimer;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var singleInstance = new System.Threading.Mutex(true, "Local\\GlassTimer.Desktop", out bool created);
        if (!created)
        {
            MessageBox.Show("Glass Timer is already running. Look for its timer icon in the system tray.", "Glass Timer");
            return;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "Glass Timer error", MessageBoxButton.OK, MessageBoxImage.Error);
            app.Shutdown(1);
        };
        var window = new TimerWindow();
        app.Run(window);
    }
}

internal sealed class Preferences
{
    public int Minutes { get; set; } = 25;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Locked { get; set; }
    public bool Pinned { get; set; }
    public string Theme { get; set; } = "Slate";
}

internal sealed class TimerWindow : Window
{
    private static readonly string SettingsFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassTimer", "settings.json");
    private readonly Preferences preferences;
    private readonly Stopwatch clock = new();
    private readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ContextMenuStrip trayMenu = new();
    private readonly Border glass;
    private readonly TextBlock digits;
    private readonly System.Windows.Shapes.Path progress;
    private readonly StackPanel toolbar;
    private readonly Border toolbarFrame;
    private readonly Button play;
    private readonly Button pin;
    private readonly System.Windows.Shapes.Path track;
    private readonly DispatcherTimer contrastTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool hovered;
    private bool lightBackground;
    private bool captureErrorReported;
    private static readonly System.Collections.Generic.Dictionary<string, string[][]> Themes = new()
    {
        ["Slate"] = new[] { new[] { "#EDF4FC", "#A9CFF5", "#202D3C" }, new[] { "#243342", "#365F85", "#E8EFF7" } },
        ["Mint"] = new[] { new[] { "#EBFFF5", "#9DDFC2", "#193B30" }, new[] { "#193B30", "#28684F", "#E3F3EA" } },
        ["Amber"] = new[] { new[] { "#FFF5E5", "#EDC786", "#40301E" }, new[] { "#49331B", "#80551D", "#F7EEDC" } },
        ["Lavender"] = new[] { new[] { "#F5F0FF", "#CCB8F0", "#362B48" }, new[] { "#392A50", "#69508D", "#EEE8F6" } },
        ["Rose"] = new[] { new[] { "#FFF0F2", "#EAB2BE", "#402A33" }, new[] { "#4B2933", "#8D465B", "#F7E7EC" } }
    };
    private readonly Forms.ToolStripMenuItem lockItem = new();
    private readonly Forms.ToolStripMenuItem playItem = new();
    private readonly Forms.ToolStripMenuItem showItem = new();
    private readonly DispatcherTimer hoverDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private double remaining;
    private bool running;
    private bool locked;
    private bool exiting;
    private IntPtr handle;
    private readonly bool testMode;
    private const double Perimeter = 372 - 104 + 26 * Math.PI;

    public TimerWindow(bool testMode = false)
    {
        this.testMode = testMode;
        preferences = testMode ? new Preferences() : LoadPreferences();
        remaining = preferences.Minutes * 60;
        locked = preferences.Locked;
        Title = "Glass Timer";
        Width = 192;
        Height = 124;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = locked || preferences.Pinned;
        Left = preferences.Left ?? SystemParameters.WorkArea.Right - Width - 40;
        Top = preferences.Top ?? SystemParameters.WorkArea.Top + 80;
        ClampPosition();

        var root = new Grid { Margin = new Thickness(8, 4, 8, 0) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
        glass = new Border
        {
            Width = 132, HorizontalAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(105, 21, 36, 50))
        };
        var face = new Grid();
        digits = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 26,
            FontWeight = FontWeights.Normal,
            Foreground = Brushes.White,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var geometry = Geometry.Parse("M66,1 L118,1 A13,13 0 0 1 131,14 L131,44 A13,13 0 0 1 118,57 L14,57 A13,13 0 0 1 1,44 L1,14 A13,13 0 0 1 14,1 Z");
        face.Children.Add(digits);
        track = new System.Windows.Shapes.Path
        {
            Data = geometry, Stroke = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
            StrokeThickness = 2, IsHitTestVisible = false
        };
        face.Children.Add(track);
        progress = new System.Windows.Shapes.Path
        {
            Data = geometry, Stroke = new SolidColorBrush(Color.FromRgb(182, 220, 255)),
            StrokeThickness = 2, IsHitTestVisible = false
        };
        face.Children.Add(progress);
        glass.Child = face;
        root.Children.Add(glass);
        toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed
        };
        toolbarFrame = new Border
        {
            Child = toolbar, CornerRadius = new CornerRadius(11), Padding = new Thickness(5),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
            Background = new SolidColorBrush(Color.FromRgb(32, 45, 60)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed
        };
        play = AddButton("M9,5 L20,12 L9,19 Z", "Start / pause / resume timer", ToggleRunning);
        AddButton("M4,10 A8,8 0 1 1 6,18 M4,4 L4,10 L10,10", "Reset to initial duration", ResetTimer);
        pin = AddButton("M8,3 L16,3 M9,3 L9,10 L6,14 L18,14 L15,10 L15,3 M12,14 L12,21", "Pin always on top", TogglePin);
        AddButton("M7,10 L17,10 Q19,10 19,12 L19,19 Q19,21 17,21 L7,21 Q5,21 5,19 L5,12 Q5,10 7,10 M8,10 L8,7 A4,4 0 0 1 16,7 L16,10", "Lock position and pass clicks through", () => SetLocked(true));
        Grid.SetRow(toolbarFrame, 1);
        root.Children.Add(toolbarFrame);
        Content = root;

        glass.MouseLeftButtonDown += (_, e) =>
        {
            if (locked) return;
            if (e.ClickCount == 2) ToggleRunning();
            else
            {
                DragMove();
                ClampPosition();
                SavePreferences();
            }
        };
        glass.MouseWheel += (_, e) =>
        {
            if (locked || running || e.Delta == 0) return;
            preferences.Minutes = Math.Clamp(preferences.Minutes + Math.Sign(e.Delta), 1, 180);
            ResetTimer();
            SavePreferences();
            e.Handled = true;
        };
        MouseEnter += (_, _) => { hoverDelay.Stop(); SampleBackground(); SetHover(true); };
        MouseLeave += (_, _) => hoverDelay.Start();
        DpiChanged += (_, _) => SetHover(IsMouseOver);
        LocationChanged += (_, _) => SampleBackground();
        hoverDelay.Tick += (_, _) => { hoverDelay.Stop(); if (!IsMouseOver) SetHover(false); };

        var themeMenu = new Forms.ToolStripMenuItem("Color theme");
        foreach (string name in Themes.Keys)
        {
            var choice = new Forms.ToolStripMenuItem(name) { Checked = name == preferences.Theme };
            choice.Click += (_, _) =>
            {
                preferences.Theme = name;
                foreach (Forms.ToolStripMenuItem item in themeMenu.DropDownItems) item.Checked = item == choice;
                ApplyTheme();
                SavePreferences();
            };
            themeMenu.DropDownItems.Add(choice);
        }
        trayMenu.Items.Add(themeMenu);
        trayMenu.Items.Add("Quit", null, (_, _) => Exit());
        lockItem.Click += (_, _) => SetLocked(!locked);
        playItem.Click += (_, _) => ToggleRunning();
        showItem.Click += (_, _) => { if (IsVisible) Hide(); else BringToFront(); UpdateDisplay(); };
        tray = new Forms.NotifyIcon
        {
            Icon = CreateTrayIcon(), Text = "Glass Timer", ContextMenuStrip = trayMenu, Visible = !testMode
        };
        tray.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            if (locked) SetLocked(false);
            BringToFront();
        };
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            Native.ExcludeFromCapture(handle);
            ApplyWindowMode();
            SetHover(false);
            SampleBackground();
        };
        ticker.Tick += (_, _) => Tick();
        ticker.Start();
        contrastTimer.Tick += (_, _) => SampleBackground();
        if (!testMode) contrastTimer.Start();
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; Hide(); UpdateDisplay(); } };
        UpdateDisplay();
    }

    private static Viewbox CreateControlIcon(string path)
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(path), StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        });
        ((System.Windows.Shapes.Path)canvas.Children[0]).SetBinding(System.Windows.Shapes.Path.StrokeProperty,
            new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        return new Viewbox { Width = 18, Height = 18, Child = canvas };
    }

    private Button AddButton(string path, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = CreateControlIcon(path), Width = 38, Height = 34, Margin = new Thickness(0, 0, 4, 0),
            Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Surface";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), "Surface"));
        template.Triggers.Add(hover);
        var focus = new Trigger { Property = IsKeyboardFocusedProperty, Value = true };
        focus.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(182, 220, 255)), "Surface"));
        focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "Surface"));
        template.Triggers.Add(focus);
        button.Template = template;
        button.MouseEnter += (_, _) => ApplyTheme();
        button.MouseLeave += (_, _) => ApplyTheme();
        button.Click += (_, _) => action();
        toolbar.Children.Add(button);
        return button;
    }

    private double CurrentRemaining => Math.Max(0, remaining - (running ? clock.Elapsed.TotalSeconds : 0));

    private void Tick()
    {
        if (running && CurrentRemaining <= 0)
        {
            remaining = 0;
            running = false;
            clock.Reset();
            tray.ShowBalloonTip(5000, "Glass Timer", "Time is up.", Forms.ToolTipIcon.Info);
        }
        UpdateDisplay();
    }

    private void ToggleRunning()
    {
        if (running)
        {
            remaining = CurrentRemaining;
            running = false;
            clock.Reset();
        }
        else
        {
            if (remaining <= 0) remaining = preferences.Minutes * 60;
            clock.Restart();
            running = true;
        }
        UpdateDisplay();
    }

    private void ResetTimer()
    {
        remaining = preferences.Minutes * 60;
        clock.Restart();
        if (!running) clock.Stop();
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        double seconds = CurrentRemaining;
        int rounded = (int)Math.Ceiling(seconds);
        digits.Text = $"{rounded / 60:00}:{rounded % 60:00}";
        double fraction = seconds / (preferences.Minutes * 60);
        progress.StrokeDashArray = new DoubleCollection { Perimeter * fraction / 2, Perimeter / 2 };
        progress.Visibility = fraction > 0 ? Visibility.Visible : Visibility.Hidden;
        if (play.Tag is not bool priorRunning || priorRunning != running)
        {
            play.Content = CreateControlIcon(running ? "M8,5 L8,19 M16,5 L16,19" : "M9,5 L20,12 L9,19 Z");
            play.Tag = running;
        }
        lockItem.Text = locked ? "Unlock widget" : "Lock widget";
        playItem.Text = running ? "Pause timer" : "Start / resume timer";
        showItem.Text = IsVisible ? "Hide widget" : "Show widget";
        tray.Text = $"Glass Timer - {digits.Text}{(running ? "" : " (stopped)")}";
        ApplyTheme();
    }

    private void SetHover(bool hovered)
    {
        hovered &= !locked;
        this.hovered = hovered;
        ApplyTheme();
        toolbar.Visibility = hovered ? Visibility.Visible : Visibility.Collapsed;
        toolbarFrame.Visibility = toolbar.Visibility;
        if (handle != IntPtr.Zero)
        {
            UpdateLayout();
            var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
            Point origin = hovered ? toolbarFrame.TranslatePoint(new Point(), this) : new Point();
            Native.SetVisibleRegion(handle, scale,
                hovered ? new Rect(origin, new Size(toolbarFrame.ActualWidth, toolbarFrame.ActualHeight)) : null);
        }
    }

    private void SetLocked(bool value)
    {
        locked = value;
        preferences.Locked = value;
        SetHover(false);
        ApplyWindowMode();
        SavePreferences();
        UpdateDisplay();
    }

    private void ApplyWindowMode()
    {
        Topmost = locked || preferences.Pinned;
        if (handle != IntPtr.Zero) Native.SetClickThrough(handle, locked);
    }

    private void BringToFront()
    {
        Show();
        Topmost = true;
        if (!locked)
        {
            Activate();
            Topmost = preferences.Pinned;
        }
        UpdateDisplay();
    }

    private void TogglePin()
    {
        preferences.Pinned = !preferences.Pinned;
        ApplyWindowMode();
        ApplyTheme();
        SavePreferences();
    }

    private void ApplyTheme()
    {
        var colors = Themes[preferences.Theme][lightBackground ? 1 : 0];
        Color ink = (Color)ColorConverter.ConvertFromString(colors[0]);
        Color accent = (Color)ColorConverter.ConvertFromString(colors[1]);
        Color surface = (Color)ColorConverter.ConvertFromString(colors[2]);
        surface.A = 204;
        digits.Foreground = new SolidColorBrush(ink);
        progress.Stroke = new SolidColorBrush(accent);
        track.Stroke = new SolidColorBrush(Color.FromArgb(64, ink.R, ink.G, ink.B));
        // Layered windows pass through zero-alpha pixels even without WS_EX_TRANSPARENT.
        glass.Background = hovered ? new SolidColorBrush(surface)
            : locked ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        toolbarFrame.Background = new SolidColorBrush(surface);
        toolbarFrame.BorderBrush = track.Stroke;
        foreach (Button button in toolbar.Children)
        {
            bool selected = button == pin && preferences.Pinned;
            Color fill = selected ? accent : ink;
            fill.A = (byte)(selected ? button.IsMouseOver ? 71 : 46 : button.IsMouseOver ? 36 : 0);
            button.Foreground = new SolidColorBrush(selected ? accent : ink);
            button.ApplyTemplate();
            if (button.Template.FindName("Surface", button) is Border border)
                border.Background = new SolidColorBrush(fill);
        }
        if (pin != null) System.Windows.Automation.AutomationProperties.SetName(pin,
            preferences.Pinned ? "Unpin always on top" : "Pin always on top");
    }

    private void SampleBackground()
    {
        if (testMode || !IsVisible || captureErrorReported || handle == IntPtr.Zero) return;
        try
        {
            Point topLeft = glass.PointToScreen(new Point(0, 0));
            Point bottomRight = glass.PointToScreen(new Point(glass.ActualWidth, glass.ActualHeight));
            using var bitmap = new Drawing.Bitmap(Math.Max(1, (int)(bottomRight.X - topLeft.X)), Math.Max(1, (int)(bottomRight.Y - topLeft.Y)));
            using (var graphics = Drawing.Graphics.FromImage(bitmap))
                graphics.CopyFromScreen((int)topLeft.X, (int)topLeft.Y, 0, 0, bitmap.Size, Drawing.CopyPixelOperation.SourceCopy);
            double brightness = 0;
            int count = 0;
            for (int y = 4; y < bitmap.Height; y += 8)
                for (int x = 4; x < bitmap.Width; x += 8)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    brightness += (0.2126 * pixel.R + 0.7152 * pixel.G + 0.0722 * pixel.B) / 255;
                    count++;
                }
            if (count > 0)
            {
                brightness /= count;
                bool nextLightBackground = lightBackground;
                if (brightness > 0.65) nextLightBackground = true;
                else if (brightness < 0.45) nextLightBackground = false;
                if (nextLightBackground != lightBackground)
                {
                    lightBackground = nextLightBackground;
                    ApplyTheme();
                }
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or ArgumentException or ExternalException)
        {
            contrastTimer.Stop();
            if (!captureErrorReported)
            {
                captureErrorReported = true;
                tray.ShowBalloonTip(5000, "Automatic contrast unavailable", e.Message, Forms.ToolTipIcon.Warning);
            }
        }
    }

    private void ClampPosition()
    {
        var bounds = SystemParameters.VirtualScreenWidth > 0
            ? new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
            : SystemParameters.WorkArea;
        Left = Math.Clamp(Left, bounds.Left, Math.Max(bounds.Left, bounds.Right - Width));
        Top = Math.Clamp(Top, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - Height));
    }

    private static Preferences LoadPreferences()
    {
        if (!File.Exists(SettingsFile)) return new Preferences();
        try
        {
            var settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsFile))
                ?? throw new InvalidDataException("Settings are empty.");
            if (settings.Minutes is < 1 or > 180 ||
                !Themes.ContainsKey(settings.Theme) ||
                (settings.Left.HasValue && !double.IsFinite(settings.Left.Value)) ||
                (settings.Top.HasValue && !double.IsFinite(settings.Top.Value)))
                throw new InvalidDataException("Settings contain an invalid duration or position.");
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            MessageBox.Show($"Could not load saved settings. Defaults will be used.\n\n{e.Message}", "Glass Timer");
            return new Preferences();
        }
    }

    private void SavePreferences()
    {
        if (testMode) return;
        preferences.Left = Left;
        preferences.Top = Top;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsFile)!);
            string temporary = SettingsFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences));
            File.Move(temporary, SettingsFile, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Could not save settings.\n\n{e.Message}", "Glass Timer");
        }
    }

    private static Drawing.Icon CreateTrayIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Drawing.Pen(Drawing.Color.FromArgb(182, 220, 255), 3);
        graphics.DrawEllipse(pen, 5, 7, 22, 22);
        graphics.DrawLine(pen, 16, 11, 16, 18);
        graphics.DrawLine(pen, 16, 18, 21, 21);
        graphics.DrawLine(pen, 12, 3, 20, 3);
        IntPtr iconHandle = bitmap.GetHicon();
        try
        {
            using var borrowed = Drawing.Icon.FromHandle(iconHandle);
            return (Drawing.Icon)borrowed.Clone();
        }
        finally { Native.DestroyIcon(iconHandle); }
    }

    private void Exit()
    {
        SavePreferences();
        exiting = true;
        ticker.Stop();
        contrastTimer.Stop();
        hoverDelay.Stop();
        tray.Visible = false;
        tray.Icon?.Dispose();
        tray.Dispose();
        trayMenu.Dispose();
        Close();
        Application.Current.Shutdown();
    }
}

internal static class Native
{
    private const int ExtendedStyle = -20;
    private const int Transparent = 0x20;
    private const int ToolWindow = 0x80;
    private const int NoActivate = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr icon);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);
    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr first, IntPtr second, int mode);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    internal static void ExcludeFromCapture(IntPtr hwnd)
    {
        if (!SetWindowDisplayAffinity(hwnd, 0x11))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not exclude timer from screen capture.");
    }


    internal static void SetClickThrough(IntPtr hwnd, bool enabled)
    {
        int style = GetWindowLong(hwnd, ExtendedStyle) | ToolWindow;
        style = enabled ? style | Transparent | NoActivate : style & ~(Transparent | NoActivate);
        if (SetWindowLong(hwnd, ExtendedStyle, style) == 0 && Marshal.GetLastWin32Error() != 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static void SetVisibleRegion(IntPtr hwnd, Matrix scale, Rect? toolbar)
    {
        IntPtr region = CreateRoundRectRgn((int)(30 * scale.M11), (int)(4 * scale.M22),
            (int)(162 * scale.M11) + 1, (int)(62 * scale.M22) + 1,
            (int)(28 * scale.M11), (int)(28 * scale.M22));
        if (region == IntPtr.Zero) throw new InvalidOperationException("Could not create the timer window region.");
        try
        {
            if (toolbar is Rect rect)
            {
                IntPtr controls = CreateRoundRectRgn((int)(rect.Left * scale.M11), (int)(rect.Top * scale.M22),
                    (int)Math.Ceiling(rect.Right * scale.M11) + 1, (int)Math.Ceiling(rect.Bottom * scale.M22) + 1,
                    (int)(22 * scale.M11), (int)(22 * scale.M22));
                if (controls == IntPtr.Zero) throw new InvalidOperationException("Could not create the controls region.");
                try
                {
                    if (CombineRgn(region, region, controls, 2) == 0)
                        throw new InvalidOperationException("Could not combine timer window regions.");
                }
                finally { DeleteObject(controls); }
            }
            if (SetWindowRgn(hwnd, region, true) == 0)
                throw new InvalidOperationException("Could not apply the timer window region.");
            region = IntPtr.Zero;
        }
        finally { if (region != IntPtr.Zero) DeleteObject(region); }
    }

}
