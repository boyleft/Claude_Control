using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Shapes = System.Windows.Shapes;

namespace ClaudeControl
{
    internal sealed class FloatingControl
    {
        private const double ActionHeight=52, ActionGap=2;
        internal readonly Window Badge;
        internal readonly Window Flyout;
        private readonly Dictionary<DesktopApp,Button> buttons=new Dictionary<DesktopApp,Button>();
        private readonly Shapes.Ellipse lamp = new Shapes.Ellipse { Width=5, Height=5, Margin=new Thickness(3,1,0,0) };
        private readonly DispatcherTimer hover = new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(220) };
        private readonly Action<int,int> save;
        private readonly TextBlock clock = new TextBlock { FontSize=16, FontWeight=FontWeights.SemiBold, TextAlignment=TextAlignment.Center };
        private readonly TextBlock clockDate = new TextBlock { FontSize=9, TextAlignment=TextAlignment.Center };
        private Drawing.Point anchor;
        private Drawing.Rectangle origin;
        private bool pressed;
        private bool enabled;
        internal static Brush Color(string value) { return (Brush)new BrushConverter().ConvertFromString(value); }
        private static Window Surface(double width,double height,string title)
        {
            return new Window { Title=title, Width=width, Height=height, WindowStyle=WindowStyle.None,
                AllowsTransparency=true, Background=Brushes.Transparent, Topmost=true, ShowInTaskbar=false,
                ResizeMode=ResizeMode.NoResize, ShowActivated=false, WindowStartupLocation=WindowStartupLocation.Manual,
                FontFamily=new FontFamily("Microsoft YaHei UI, Segoe UI") };
        }
        internal FloatingControl(Style action, KeyValuePair<DesktopApp,string>[] choices, Action<DesktopApp> closeApp,
            Action showWindow, Action<int,int> savePosition)
        {
            save = savePosition;
            Badge=Surface(100,ActionHeight,"应用悬浮标");
            Flyout=Surface(152,ActionHeight*choices.Length+ActionGap*(choices.Length-1),"应用快捷操作");
            var content = new StackPanel { VerticalAlignment=VerticalAlignment.Center };
            var glyph = new Grid { Width=10, Height=10, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(0,0,4,0) };
            glyph.Children.Add(new Shapes.Ellipse { Stroke=Color("#28361F"), StrokeThickness=1.3 });
            glyph.Children.Add(new Shapes.Rectangle { Width=3, Height=3, RadiusX=0.6, RadiusY=0.6, Fill=Color("#1B2614") });
            var label = new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Center };
            label.Children.Add(glyph);
            label.Children.Add(new TextBlock { Text="应用控制", Foreground=Color("#141C10"), FontSize=10, FontWeight=FontWeights.SemiBold });
            label.Children.Add(lamp);
            content.Children.Add(label);
            content.Children.Add(clock);
            content.Children.Add(clockDate);
            foreach(var text in new[]{clock,clockDate}) text.Foreground=Color("#141C10");
            Badge.Content = new Border { Padding=new Thickness(6,1,6,1), Child=content, Background=Color("#788C5D"), CornerRadius=new CornerRadius(10),
                BorderBrush=Color("#5B6D46"), BorderThickness=new Thickness(1), Cursor=Cursors.SizeAll };
            var actions=new StackPanel { Background=Brushes.Transparent };
            foreach (var choice in choices) {
                var app=choice.Key;
                var button=new Button { Style=action, Content=choice.Value, Height=ActionHeight, Padding=new Thickness(8,0,8,0),
                    Margin=new Thickness(1,0,1,actions.Children.Count==choices.Length-1 ? 0 : ActionGap) };
                AutomationProperties.SetName(button,choice.Value);
                button.Click+=delegate { closeApp(app); };
                buttons.Add(app,button);
                actions.Children.Add(button);
            }
            Flyout.Content=actions;
            Badge.MouseEnter += delegate { if (!pressed) OpenFlyout(); };
            Badge.MouseLeave += delegate { hover.Start(); };
            Flyout.MouseEnter += delegate { hover.Stop(); };
            Flyout.MouseLeave += delegate { hover.Start(); };
            hover.Tick += delegate {
                hover.Stop();
                if (!Badge.IsMouseOver && !Flyout.IsMouseOver) Flyout.Hide();
            };
            bool dragged=false, doubleClick=false;
            Badge.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                doubleClick=e.ClickCount==2 && !dragged;
                dragged=false;
                pressed=true; anchor=Forms.Cursor.Position; origin=Bounds(Badge);
                Flyout.Hide(); Badge.CaptureMouse(); e.Handled=true;
            };
            Badge.MouseMove += delegate {
                if (!pressed) return;
                var p=Forms.Cursor.Position;
                var threshold=Forms.SystemInformation.DragSize;
                if (!dragged && Math.Abs(p.X-anchor.X)*2<threshold.Width && Math.Abs(p.Y-anchor.Y)*2<threshold.Height) return;
                dragged=true;
                MoveBadge(origin.X+p.X-anchor.X, origin.Y+p.Y-anchor.Y, Forms.Screen.FromPoint(p).WorkingArea);
            };
            Badge.MouseLeftButtonUp += delegate {
                if (!pressed) return;
                bool open=doubleClick && !dragged;
                Badge.ReleaseMouseCapture();
                if (open) showWindow();
            };
            Badge.LostMouseCapture += delegate {
                if (!pressed) return;
                pressed=false;
                if (dragged) { var bounds=Bounds(Badge); save(bounds.X,bounds.Y); }
                if (Badge.IsMouseOver) OpenFlyout();
            };
        }
        internal void UpdateClock(DateTimeOffset time)
        {
            clock.Text=time.ToString("HH:mm:ss",CultureInfo.InvariantCulture);
            clockDate.Text=time.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
        }
        internal void SetVisible(bool value, int? x, int? y)
        {
            enabled=value;
            if (!value) { hover.Stop(); Flyout.Hide(); Badge.Hide(); return; }
            // Ensure the HWND exists before positioning it in physical monitor coordinates.
            Badge.Show();
            var area=Forms.Screen.PrimaryScreen.WorkingArea;
            if (x.HasValue && y.HasValue) area=Forms.Screen.FromPoint(new Drawing.Point(x.Value,y.Value)).WorkingArea;
            var size=Bounds(Badge);
            MoveBadge(x ?? area.Right-size.Width-22, y ?? area.Top+80, area);
            if (Badge.IsMouseOver) OpenFlyout();
        }
        private void OpenFlyout()
        {
            if (!enabled || pressed) return;
            hover.Stop();
            var b=Bounds(Badge);
            var area=Forms.Screen.FromRectangle(b).WorkingArea;
            MoveBadge(b.X,b.Y,area);
            b=Bounds(Badge);
            Flyout.Show();
            Flyout.UpdateLayout();
            var f=Bounds(Flyout);
            int x=b.Right+3+f.Width<=area.Right ? b.Right+3 : b.Left-f.Width-3;
            Move(Flyout, x, b.Bottom-f.Height, area);
        }
        internal void Update(DesktopApp? stopping, bool running)
        {
            foreach (var pair in buttons) {
                pair.Value.IsEnabled=!stopping.HasValue;
                pair.Value.Content=stopping==pair.Key ? "正在关闭…" : AutomationProperties.GetName(pair.Value);
            }
            lamp.Fill=Color(running ? "#24331A" : "#DCE5D1");
        }
        internal void Dispose()
        {
            enabled=false; hover.Stop(); Flyout.Close(); Badge.Close();
        }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { internal int Left, Top, Right, Bottom; }
        [DllImport("user32")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y,int w,int h,uint flags);
        private static Drawing.Rectangle Bounds(Window w)
        {
            Rect r; GetWindowRect(new WindowInteropHelper(w).Handle, out r);
            return Drawing.Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom);
        }
        private void MoveBadge(int x, int y, Drawing.Rectangle area)
        {
            // Reserve space above the badge for the bottom-aligned menu without shrinking its buttons.
            double scale=PresentationSource.FromVisual(Badge).CompositionTarget.TransformToDevice.M22;
            int headroom=(int)Math.Ceiling((Flyout.Height-Badge.Height)*scale);
            Move(Badge,x,Math.Max(y,area.Top+headroom),area);
        }
        private static void Move(Window w, int x, int y, Drawing.Rectangle area)
        {
            var b=Bounds(w);
            x=Math.Max(area.Left,Math.Min(x,area.Right-b.Width));
            y=Math.Max(area.Top,Math.Min(y,area.Bottom-b.Height));
            SetWindowPos(new WindowInteropHelper(w).Handle, IntPtr.Zero, x,y,0,0,0x15);
        }
    }
}
