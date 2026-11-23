using System;
using System.Runtime.InteropServices;
using System.Windows;
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
        internal readonly Window Badge = Surface(99, 99, "Claude 悬浮标");
        internal readonly Window Flyout = Surface(142, 54, "Claude 快捷操作");
        private readonly Button stop = new Button();
        private readonly Shapes.Ellipse lamp = new Shapes.Ellipse { Width=7, Height=7, Margin=new Thickness(5,1,0,0) };
        private readonly DispatcherTimer hover = new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(220) };
        private readonly Action<int,int> save;
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
        internal FloatingControl(Style action, Action request, Action showWindow, Action<int,int> savePosition)
        {
            save = savePosition;
            var content = new StackPanel { VerticalAlignment=VerticalAlignment.Center, HorizontalAlignment=HorizontalAlignment.Center };
            var glyph = new Grid { Width=35, Height=35, Margin=new Thickness(0,0,0,6) };
            glyph.Children.Add(new Shapes.Ellipse { Stroke=Color("#28361F"), StrokeThickness=2 });
            glyph.Children.Add(new Shapes.Rectangle { Width=11, Height=11, RadiusX=2, RadiusY=2, Fill=Color("#1B2614") });
            content.Children.Add(glyph);
            var label = new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Center };
            label.Children.Add(new TextBlock { Text="Claude", Foreground=Color("#141C10"), FontSize=14, FontWeight=FontWeights.SemiBold });
            label.Children.Add(lamp);
            content.Children.Add(label);
            var tile = new Border { Width=99, Height=99, Child=content, Background=Color("#788C5D"), CornerRadius=new CornerRadius(24),
                BorderBrush=Color("#5B6D46"), BorderThickness=new Thickness(1), Cursor=Cursors.SizeAll };
            Badge.Content=new Viewbox { Child=tile };
            Badge.SourceInitialized += delegate {
                var scale=PresentationSource.FromVisual(Badge).CompositionTarget.TransformToDevice;
                Badge.Width=99/scale.M11; Badge.Height=99/scale.M22;
            };
            stop.Style=action;
            stop.Content="关闭 Claude";
            stop.Margin=new Thickness(1);
            stop.Click += delegate { request(); };
            Flyout.Content=stop;
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
                Move(Badge, origin.X+p.X-anchor.X, origin.Y+p.Y-anchor.Y, Forms.Screen.FromPoint(p).WorkingArea);
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
        internal void SetVisible(bool value, int? x, int? y)
        {
            enabled=value;
            if (!value) { hover.Stop(); Flyout.Hide(); Badge.Hide(); return; }
            // Ensure the HWND exists before positioning it in physical monitor coordinates.
            Badge.Show();
            var area=Forms.Screen.PrimaryScreen.WorkingArea;
            if (x.HasValue && y.HasValue) area=Forms.Screen.FromPoint(new Drawing.Point(x.Value,y.Value)).WorkingArea;
            var size=Bounds(Badge);
            Move(Badge, x ?? area.Right-size.Width-22, y ?? area.Top+80, area);
            if (Badge.IsMouseOver) OpenFlyout();
        }
        private void OpenFlyout()
        {
            if (!enabled || pressed) return;
            hover.Stop();
            var b=Bounds(Badge);
            var area=Forms.Screen.FromRectangle(b).WorkingArea;
            Flyout.Show();
            var f=Bounds(Flyout);
            int x=b.Right+3+f.Width<=area.Right ? b.Right+3 : b.Left-f.Width-3;
            Move(Flyout, x, b.Top+(b.Height-f.Height)/2, area);
        }
        internal void Update(bool busy, bool running)
        {
            stop.IsEnabled=!busy; stop.Content=busy ? "正在关闭…" : "关闭 Claude";
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
        private static void Move(Window w, int x, int y, Drawing.Rectangle area)
        {
            var b=Bounds(w);
            x=Math.Max(area.Left,Math.Min(x,area.Right-b.Width));
            y=Math.Max(area.Top,Math.Min(y,area.Bottom-b.Height));
            SetWindowPos(new WindowInteropHelper(w).Handle, IntPtr.Zero, x,y,0,0,0x15);
        }
    }
}
