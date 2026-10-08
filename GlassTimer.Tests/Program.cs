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

    [STAThread]
    private static int Main()
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
            Check("timer has no hover tooltip", glass.ToolTip == null);
            Check("stopped at 25 minutes", digits.Text == "25:00" && !Field<bool>(window, "running"));
            Check("Consolas, compact face, no taskbar button", digits.FontFamily.Source == "Consolas" &&
                glass.ActualWidth == 132 && glass.ActualHeight == 58 && !window.ShowInTaskbar);
            var center = digits.TranslatePoint(new Point(digits.ActualWidth / 2, digits.ActualHeight / 2), glass);
            Check("digits centered", Math.Abs(center.X - 66) < 0.1 && Math.Abs(center.Y - 29) < 0.1);
            var image = new RenderTargetBitmap(192, 124, 96, 96, PixelFormats.Pbgra32);
            image.Render((Visual)window.Content);
            byte[] pixel = new byte[4];
            image.CopyPixels(new Int32Rect(0, 90, 1, 1), pixel, 4, 0);
            Check("outside widget renders fully transparent", pixel[3] == 0);
            image.CopyPixels(new Int32Rect(40, 29, 1, 1), pixel, 4, 0);
            Check("unlocked idle face retains minimal hit-test alpha", pixel[3] == 1);
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
            Check("locked remains glass with controls hidden", ((SolidColorBrush)glass.Background).Color.A < 255 &&
                toolbar.Visibility == Visibility.Collapsed);
            Check("locked face has zero alpha", ((SolidColorBrush)glass.Background).Color.A == 0);
            var menu = Field<System.Windows.Forms.ContextMenuStrip>(window, "trayMenu");
            Check("tray contains only color theme and quit", menu.Items.Count == 2 &&
                menu.Items[0].Text == "Color theme" && menu.Items[1].Text == "Quit");
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
