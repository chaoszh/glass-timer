using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassTimer;

internal static class Program
{
    private static int checks;
    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Check(string name, bool condition)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine($"PASS {name}");
        checks++;
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new TimerWindow(testMode: true) { Opacity = 0, ShowActivated = false };
        try
        {
            window.Show();
            window.UpdateLayout();
            var digits = Field<TextBlock>(window, "digits");
            var glass = Field<Border>(window, "glass");
            var progress = Field<System.Windows.Shapes.Path>(window, "progress");
            var preferences = Field<Preferences>(window, "preferences");
            var toolbar = Field<StackPanel>(window, "toolbar");
            foreach (Button button in toolbar.Children)
                Check("control has accessible name without tooltip", button.ToolTip == null &&
                    !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(button)));
            var handle = new WindowInteropHelper(window).Handle;
            Check("contrast sampling interval is 100 ms",
                Field<System.Windows.Threading.DispatcherTimer>(window, "contrastTimer").Interval == TimeSpan.FromMilliseconds(100));
            Check("adaptive color defaults on and excludes timer from capture",
                preferences.AdaptiveColor && GetWindowDisplayAffinity(handle, out uint affinity) && affinity == 0x11);
            Check("timer has no hover tooltip", glass.ToolTip == null);
            Check("stopped at 25 minutes", digits.Text == "25:00" && !Field<bool>(window, "running"));
            Check("Consolas, compact face, no taskbar button", digits.FontFamily.Source == "Consolas" &&
                glass.ActualWidth == 132 && glass.ActualHeight == 58 && !window.ShowInTaskbar);
            var center = digits.TranslatePoint(new Point(digits.ActualWidth / 2, digits.ActualHeight / 2), glass);
            Check("digits centered", Math.Abs(center.X - 66) < 0.1 && Math.Abs(center.Y - 29) < 0.1);
            var image = new RenderTargetBitmap(234, 124, 96, 96, PixelFormats.Pbgra32);
            image.Render((Visual)window.Content);
            byte[] pixel = new byte[4];
            image.CopyPixels(new Int32Rect(0, 90, 1, 1), pixel, 4, 0);
            Check("outside widget renders fully transparent", pixel[3] == 0);
            image.CopyPixels(new Int32Rect(61, 32, 1, 1), pixel, 4, 0);
            Check("unlocked idle face has translucent theme surface", pixel[3] is >= 60 and <= 62);
            void Wheel(int delta) => glass.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, delta)
                { RoutedEvent = UIElement.MouseWheelEvent });
            Wheel(120);
            Check("scroll up sets duration and stays stopped", digits.Text == "26:00" && !Field<bool>(window, "running"));
            Wheel(-120);
            Check("scroll down sets duration", digits.Text == "25:00");
            preferences.Minutes = 1;
            Call(window, "ResetTimer");
            Wheel(-120);
            Check("one-minute lower bound", preferences.Minutes == 1);
            preferences.Minutes = 180;
            Call(window, "ResetTimer");
            Wheel(120);
            Check("180-minute upper bound", preferences.Minutes == 180 && digits.Text == "180:00");
            preferences.Minutes = 25;
            Call(window, "ResetTimer");
            double fullStroke = progress.StrokeDashArray[0];
            Check("full border covers perimeter", Math.Abs(fullStroke * 2 - (268 + 26 * Math.PI)) < 0.01);
            Call(window, "ToggleRunning");
            Thread.Sleep(1100);
            Call(window, "Tick");
            Check("countdown and shrinking border", digits.Text == "24:59" && progress.StrokeDashArray[0] < fullStroke);
            Wheel(120);
            Check("running ignores wheel", preferences.Minutes == 25);
            Call(window, "ToggleRunning");
            string paused = digits.Text;
            Thread.Sleep(150);
            Call(window, "Tick");
            Check("pause freezes time", digits.Text == paused);
            Call(window, "SetHover", true);
            Check("hover becomes 80 percent opaque with controls", ((SolidColorBrush)glass.Background).Color.A == 204 &&
                toolbar.Visibility == Visibility.Visible);
            window.UpdateLayout();
            var playButton = Field<Button>(window, "play");
            playButton.Focus();
            var playSurface = (Border)playButton.Template.FindName("Surface", playButton);
            Check("focused control has no persistent border and retains keyboard focus visual",
                playSurface.BorderThickness == new Thickness(0) &&
                playButton.FocusVisualStyle != null &&
                window.Resources["ControlFocus"] is SolidColorBrush);
            var frame = Field<Border>(window, "toolbarFrame");
            window.UpdateLayout();
            var frameOrigin = frame.TranslatePoint(new Point(), window);
            Check("controls border fits without clipping", frameOrigin.X >= 0 && frameOrigin.Y >= 0 &&
                frameOrigin.X + frame.ActualWidth <= window.ActualWidth &&
                frameOrigin.Y + frame.ActualHeight <= window.ActualHeight);
            Call(window, "SetLocked", true);
            Call(window, "SetHover", true);
            int style = GetWindowLong(handle, -20);
            Check("native lock is topmost, click-through, and nonactivating",
                window.Topmost && (style & 0x20) != 0 && (style & 0x08000000) != 0 && (style & 0x80000) != 0);
            Check("locked keeps translucent surface with controls hidden", ((SolidColorBrush)glass.Background).Color.A == 61 &&
                toolbar.Visibility == Visibility.Collapsed);
            Check("locked translucent face still passes clicks through",
                ((SolidColorBrush)glass.Background).Color.A == 61 && (GetWindowLong(handle, -20) & 0x20) != 0);
            var menu = Field<System.Windows.Forms.ContextMenuStrip>(window, "trayMenu");
            Check("tray exposes adaptive color, color theme, and quit", menu.Items.Count == 3 &&
                menu.Items[0].Text == "Adaptive color: On" &&
                menu.Items[1].Text == "Color theme" && menu.Items[2].Text == "Quit");
            var themeContextMenu = glass.ContextMenu!;
            Check("timer has a context menu with all five themes",
                themeContextMenu.Items.Count == 5 &&
                ((System.Windows.Controls.MenuItem)themeContextMenu.Items[0]).Header as string == "Slate");
            themeContextMenu.ApplyTemplate();
            var menuSurface = (Border)themeContextMenu.Template.FindName("MenuSurface", themeContextMenu);
            Check("theme menu matches demo width, rounded surface, and font",
                menuSurface.Width == 224 && menuSurface.CornerRadius.TopLeft == 10 &&
                themeContextMenu.FontFamily.Source == "Segoe UI" && themeContextMenu.FontSize == 13);
            var previewItem = (System.Windows.Controls.MenuItem)themeContextMenu.Items[1];
            previewItem.ApplyTemplate();
            Check("theme items use swatches and a right-side custom selection mark",
                previewItem.Icon is System.Windows.Shapes.Ellipse { Width: 9, Height: 9 } &&
                previewItem.Template.FindName("ItemSurface", previewItem) is Border { CornerRadius.TopLeft: 5 } &&
                previewItem.Template.FindName("SelectionMark", previewItem) is System.Windows.Shapes.Path);
            Color selectedInk = ((SolidColorBrush)digits.Foreground).Color;
            previewItem.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
            Color previewInk = ((SolidColorBrush)digits.Foreground).Color;
            Check("hovering a context theme previews it without selecting",
                previewInk != selectedInk && preferences.Theme == "Slate");
            Call(window, "Tick");
            Check("theme preview survives timer refresh and colors the menu",
                ((SolidColorBrush)digits.Foreground).Color == previewInk &&
                ((SolidColorBrush)themeContextMenu.Foreground).Color == previewInk &&
                ((SolidColorBrush)themeContextMenu.Resources["MenuHover"]).Color.A == 31);
            previewItem.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseLeaveEvent });
            Check("leaving a theme preview restores selected colors",
                ((SolidColorBrush)digits.Foreground).Color == selectedInk);
            previewItem.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = UIElement.MouseEnterEvent });
            themeContextMenu.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.ContextMenu.ClosedEvent));
            Check("closing theme menu cancels its preview",
                ((SolidColorBrush)digits.Foreground).Color == selectedInk && preferences.Theme == "Slate");
            previewItem.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            Check("context theme selection updates saved theme and tray check",
                preferences.Theme == "Mint" &&
                ((System.Windows.Forms.ToolStripMenuItem)((System.Windows.Forms.ToolStripMenuItem)menu.Items[1]).DropDownItems[1]).Checked);
            themeContextMenu.PlacementTarget = glass;
            themeContextMenu.IsOpen = true;
            themeContextMenu.UpdateLayout();
            previewItem.ApplyTemplate();
            Check("opened theme menu renders custom layout and selected mark",
                Math.Abs(themeContextMenu.ActualWidth - 248) < 1 &&
                ((System.Windows.Shapes.Path)previewItem.Template.FindName("SelectionMark", previewItem)).Visibility == Visibility.Visible);
            if (args.Length == 1)
            {
                var menuImage = new RenderTargetBitmap((int)Math.Ceiling(themeContextMenu.ActualWidth),
                    (int)Math.Ceiling(themeContextMenu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                menuImage.Render(themeContextMenu);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(menuImage));
                using var output = System.IO.File.Create(args[0]);
                encoder.Save(output);
            }
            themeContextMenu.IsOpen = false;
            ((System.Windows.Forms.ToolStripMenuItem)((System.Windows.Forms.ToolStripMenuItem)menu.Items[1]).DropDownItems[0]).PerformClick();
            var adaptiveItem = Field<System.Windows.Forms.ToolStripMenuItem>(window, "adaptiveItem");
            adaptiveItem.PerformClick();
            Check("adaptive color off stops sampling and allows capture",
                !preferences.AdaptiveColor && !Field<System.Windows.Threading.DispatcherTimer>(window, "contrastTimer").IsEnabled &&
                GetWindowDisplayAffinity(handle, out affinity) && affinity == 0);
            adaptiveItem.PerformClick();
            Check("adaptive color on resumes capture exclusion",
                preferences.AdaptiveColor && GetWindowDisplayAffinity(handle, out affinity) && affinity == 0x11);
            Wheel(120);
            Check("locked ignores wheel", preferences.Minutes == 25);
            Field<System.Windows.Forms.ToolStripMenuItem>(window, "lockItem").PerformClick();
            style = GetWindowLong(handle, -20);
            Check("tray unlock restores native interaction", !window.Topmost && (style & 0x20) == 0 &&
                (style & 0x08000000) == 0);
            window.Hide();
            Call(window, "BringToFront");
            Check("tray brings hidden unlocked window forward without pinning", window.IsVisible && !window.Topmost);
            Call(window, "SetLocked", true);
            window.Hide();
            Call(window, "BringToFront");
            style = GetWindowLong(handle, -20);
            Check("tray brings locked window forward preserving click-through", window.IsVisible && window.Topmost &&
                (style & 0x20) != 0 && (style & 0x08000000) != 0);
            Call(window, "SetLocked", false);
            Call(window, "ResetTimer");
            Check("reset restores full border", digits.Text == "25:00" && progress.StrokeDashArray[0] == fullStroke);
            foreach (string name in new[] { "Slate", "Mint", "Amber", "Lavender", "Rose" })
            {
                preferences.Theme = name;
                Set(window, "lightBackground", false);
                Call(window, "ApplyTheme");
                Color dark = ((SolidColorBrush)digits.Foreground).Color;
                Set(window, "lightBackground", true);
                Call(window, "ApplyTheme");
                Check(name + " has paired foreground colors", dark != ((SolidColorBrush)digits.Foreground).Color);
            }
            Call(window, "TogglePin");
            Check("pin keeps unlocked window topmost", window.Topmost && preferences.Pinned);
            Call(window, "BringToFront");
            Check("bring forward preserves pin", window.Topmost);
            Call(window, "TogglePin");
            Check("unpin releases topmost", !window.Topmost);
            var backdrop = new Window
            {
                Width = 400, Height = 260, Left = 100, Top = 100,
                Background = Brushes.White, WindowStyle = WindowStyle.None, ShowInTaskbar = false
            };
            backdrop.Show();
            backdrop.UpdateLayout();
            window.Left = 140;
            window.Top = 140;
            window.Opacity = 1;
            window.Topmost = true;
            glass.Background = Brushes.Red;
            window.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.Render);
            Thread.Sleep(150);
            Point sample = glass.PointToScreen(new Point(10, 29));
            using (var capture = new System.Drawing.Bitmap(1, 1))
            {
                using (var graphics = System.Drawing.Graphics.FromImage(capture))
                    graphics.CopyFromScreen((int)sample.X, (int)sample.Y, 0, 0, capture.Size,
                        System.Drawing.CopyPixelOperation.SourceCopy);
                var color = capture.GetPixel(0, 0);
                Check("screen sampling excludes layered timer and reads white backdrop",
                    color.R > 240 && color.G > 240 && color.B > 240);
            }
            backdrop.Close();
            window.Opacity = 0;
            window.Topmost = false;
            Call(window, "SetHover", false);
            Set(window, "remaining", 0d);
            Call(window, "UpdateDisplay");
            Check("empty border at zero", digits.Text == "00:00" && progress.Visibility == Visibility.Hidden);
            Call(window, "ToggleRunning");
            Check("restart after zero", Field<bool>(window, "running") && digits.Text == "25:00");
            Check("repeat control fits expanded toolbar", toolbar.Children.Count == 5 && window.Width == 234);
            var animationFactory = typeof(TimerWindow).GetMethod("CreateAlarmAnimation", BindingFlags.Static | BindingFlags.NonPublic)!;
            var vibration = (System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames)animationFactory.Invoke(null,
                new object[] { new double[] { -3, 3, -3, 3, -3, 3 } })!;
            Check("vibration matches demo cadence without scaling",
                vibration.Duration.TimeSpan == TimeSpan.FromSeconds(1) &&
                vibration.KeyFrames[1].Value == -3 && vibration.KeyFrames[6].Value == 3 &&
                vibration.KeyFrames[7].KeyTime.TimeSpan == TimeSpan.FromSeconds(0.45) &&
                glass.RenderTransform is TranslateTransform);
            Set(window, "remaining", -2d);
            Call(window, "Tick");
            Check("completion rings with red face, white digits and no perimeter",
                Field<bool>(window, "ringing") && !Field<bool>(window, "running") &&
                ((SolidColorBrush)glass.Background).Color == Color.FromRgb(180, 35, 50) &&
                ((SolidColorBrush)digits.Foreground).Color == Colors.White &&
                progress.Visibility == Visibility.Hidden &&
                Field<System.Windows.Shapes.Path>(window, "track").Visibility == Visibility.Hidden &&
                Field<TranslateTransform>(window, "alarmOffset").HasAnimatedProperties);
            Call(window, "SetLocked", true);
            Check("locked alarm temporarily accepts dismissal without changing lock preference",
                preferences.Locked && (GetWindowLong(handle, -20) & 0x20) == 0);
            Call(window, "StopAlarm");
            Check("dismissal restores click-through and removes vibration",
                (GetWindowLong(handle, -20) & 0x20) != 0 &&
                !Field<TranslateTransform>(window, "alarmOffset").HasAnimatedProperties);
            Call(window, "SetLocked", false);
            Call(window, "ToggleRepeat");
            Check("repeat preference toggles without starting stopped timer", preferences.Repeating && !Field<bool>(window, "running"));
            var restored = System.Text.Json.JsonSerializer.Deserialize<Preferences>(
                System.Text.Json.JsonSerializer.Serialize(preferences))!;
            Check("repeat preference survives serialization with backwards-compatible default",
                restored.Repeating && !System.Text.Json.JsonSerializer.Deserialize<Preferences>("{}")!.Repeating);
            Call(window, "ToggleRunning");
            Set(window, "remaining", -3001d);
            Call(window, "Tick");
            Check("repeat skips elapsed rounds and rings while next round runs",
                Field<bool>(window, "running") && Field<bool>(window, "ringing") &&
                Field<double>(window, "remaining") > 0 && Field<double>(window, "remaining") <= 1500);
            Call(window, "StopAlarm");
            Check("dismissing repeat alarm leaves countdown running", Field<bool>(window, "running"));
            Call(window, "StartAlarm");
            Call(window, "ResetTimer");
            Check("reset clears alarm and restores border", !Field<bool>(window, "ringing") && progress.Visibility == Visibility.Visible);
            Call(window, "ToggleRunning");
            preferences.Minutes = 1;
            preferences.Repeating = false;
            Call(window, "ResetTimer");
            Set(window, "remaining", 10d);
            Call(window, "ToggleRepeat");
            var elapsedDigits = Field<TextBlock>(window, "elapsedDigits");
            Check("enabling repeat includes current round elapsed immediately",
                digits.Text == "00:10" && elapsedDigits.Text == "00:50" &&
                elapsedDigits.Visibility == Visibility.Visible);
            window.UpdateLayout();
            var elapsedOrigin = elapsedDigits.TranslatePoint(new Point(), glass);
            Check("mini elapsed readout fits beneath countdown without resizing",
                elapsedDigits.FontSize == 11 && elapsedOrigin.Y > digits.TranslatePoint(new Point(), glass).Y &&
                elapsedOrigin.Y + elapsedDigits.ActualHeight < glass.ActualHeight &&
                glass.ActualWidth == 132 && glass.ActualHeight == 58);
            Call(window, "ToggleRunning");
            Set(window, "remaining", -125d);
            Call(window, "Tick");
            Check("elapsed accumulates across multiple skipped rounds",
                elapsedDigits.Text == "03:05" && Field<double>(window, "completedDuration") == 180);
            Call(window, "ToggleRunning");
            string pausedElapsed = elapsedDigits.Text;
            Thread.Sleep(100);
            Call(window, "Tick");
            Check("paused elapsed freezes", elapsedDigits.Text == pausedElapsed);
            Call(window, "ToggleRepeat");
            Check("disabling repeat hides mini readout", elapsedDigits.Visibility == Visibility.Collapsed);
            Call(window, "ToggleRepeat");
            Check("reenabling repeat starts total from the current round",
                elapsedDigits.Text == "00:05" && Field<double>(window, "completedDuration") == 0);
            Call(window, "ResetTimer");
            Check("reset clears elapsed session", elapsedDigits.Text == "00:00" &&
                Field<double>(window, "completedDuration") == 0);
            Console.WriteLine($"{checks} integration checks passed.");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 1;
        }
        finally
        {
            Call(window, "Exit");
        }
    }
}
