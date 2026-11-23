using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Shapes = System.Windows.Shapes;

[assembly: AssemblyTitle("Claude 控制台")]
[assembly: AssemblyProduct("Claude 控制台")]
[assembly: AssemblyVersion("1.0.0.0")]
namespace ClaudeControl
{
    internal static class Program
    {
        [STAThread] private static int Main(string[] args)
        {
            if (args.Length==1 && args[0]=="--stop-cowork") {
                try { BackgroundService.Stop(); return 0; } catch { return 1; }
            }
            bool first;
            using (var mutex = new Mutex(true, @"Local\ClaudeControl.Desk.v1", out first))
            using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ClaudeControl.Show.v1")) {
                if (!first) { signal.Set(); return 0; }
                try {
                    var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
                    var desk=new Desk();
                    var wake=ThreadPool.RegisterWaitForSingleObject(signal, delegate {
                        app.Dispatcher.BeginInvoke((Action)desk.Show);
                    }, null, Timeout.Infinite, false);
                    try { desk.Show(); app.Run(); } finally { wake.Unregister(null); desk.Dispose(); }
                } catch (Exception ex) {
                    MessageBox.Show(ex.GetBaseException().Message,"Claude 控制台",MessageBoxButton.OK,MessageBoxImage.Error); return 1;
                }
            }
            return 0;
        }
    }
    internal sealed class Desk : IDisposable
    {
        private readonly Window window;
        private readonly FloatingControl floating;
        private readonly Forms.NotifyIcon tray=new Forms.NotifyIcon();
        private readonly Forms.ContextMenuStrip menu=new Forms.ContextMenuStrip();
        private readonly Forms.ToolStripMenuItem trayStop, trayFloat;
        private readonly Drawing.Icon icon;
        private readonly ProcessTree tree=new ProcessTree(Installation.Owns, -1, true);
        private Dictionary<int,long> known=new Dictionary<int,long>();
        private readonly DispatcherTimer timer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(5) };
        private readonly CheckBox toggle;
        private readonly Button stop;
        private readonly TextBlock status, count, service, details, feedback, stopLabel;
        private const string Prefs=@"HKEY_CURRENT_USER\Software\ClaudeControlDesk";
        private int? x=Pref("X"), y=Pref("Y");
        private bool busy, reading, quitting, active;
        private int generation;
        private Window exitDialog;
        private static int? Pref(string name) { return Registry.GetValue(Prefs,name,null) as int?; }
        private static void Write(string name,int value) { Registry.SetValue(Prefs,name,value,RegistryValueKind.DWord); }
        private T Find<T>(string name) where T:FrameworkElement { return (T)window.FindName(name); }
        internal Desk()
        {
            using (var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Main.xaml"))
                window=(Window)XamlReader.Load(stream);
            icon=Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
            window.Icon=Imaging.CreateBitmapSourceFromHIcon(icon.Handle,Int32Rect.Empty,System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            toggle=Find<CheckBox>("FloatToggle"); stop=Find<Button>("Stop"); status=Find<TextBlock>("Status");
            count=Find<TextBlock>("Count"); service=Find<TextBlock>("Service"); details=Find<TextBlock>("Details");
            feedback=Find<TextBlock>("Feedback"); stopLabel=Find<TextBlock>("StopLabel");
            floating=new FloatingControl((Style)window.FindResource("Action"),RequestStop,Show,delegate(int left,int top){
                x=left; y=top; Write("X",left); Write("Y",top);
            });
            menu.Items.Add("打开控制台",null,delegate { Show(); });
            trayStop=(Forms.ToolStripMenuItem)menu.Items.Add("关闭 Claude",null,delegate { RequestStop(); });
            trayFloat=(Forms.ToolStripMenuItem)menu.Items.Add("桌面悬浮标",null,delegate { toggle.IsChecked=toggle.IsChecked!=true; });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出控制台",null,delegate { Exit(); });
            tray.Icon=icon; tray.Text="Claude 控制台"; tray.ContextMenuStrip=menu; tray.Visible=true;
            tray.MouseClick += delegate(object sender, Forms.MouseEventArgs e) { if (e.Button==Forms.MouseButtons.Left) Show(); };
            stop.Click += delegate { RequestStop(); };
            toggle.Checked += ChangeFloat; toggle.Unchecked += ChangeFloat;
            window.Closing += Closing;
            window.Loaded += delegate { toggle.IsChecked=Pref("Float")==1; ReadState(); timer.Start(); };
            timer.Tick += delegate { ReadState(); };
        }
        private void ChangeFloat(object sender,RoutedEventArgs e)
        {
            bool on=toggle.IsChecked==true;
            floating.SetVisible(on,x,y); trayFloat.Checked=on; Write("Float",on?1:0);
        }
        internal void Show()
        {
            window.Show(); window.WindowState=WindowState.Normal; window.Activate();
            if (exitDialog!=null) exitDialog.Activate();
        }
        private sealed class State
        {
            internal List<Node> Nodes;
            internal bool Service;
            internal bool Any { get { return Nodes.Count>0 || Service; } }
        }
        private State Inspect(Dictionary<int,long> identities)
        { return new State { Nodes=tree.Scan(identities), Service=BackgroundService.Active() }; }
        private void Display(State s)
        {
            active=s.Any;
            known=s.Nodes.Where(n=>!n.Unverified).ToDictionary(n=>n.Id,n=>n.Birth);
            int uncertain=s.Nodes.Count(n=>n.Unverified);
            status.Text=uncertain>0 ? "待核实" : active ? "运行中" : "未运行";
            count.Text=String.Format("{0} 个 Claude 关联进程",s.Nodes.Count-uncertain);
            service.Text="Cowork 后台服务"+(s.Service?"正在运行":"未运行")+(uncertain>0?" · "+uncertain+" 个进程待核实":"");
            details.Text=s.Nodes.Count==0?"Windows 登录会话中未发现匹配项。":String.Join("\n",s.Nodes.OrderBy(n=>n.Name).ThenBy(n=>n.Id)
                .Select(n=>n.Name+"   ·   PID "+n.Id+(n.Unverified?"   归属待核实":"")));
            Find<Shapes.Ellipse>("Lamp").Fill=FloatingControl.Color(active?"#A24C32":"#948C84");
            floating.Update(busy,active);
        }
        private async void ReadState()
        {
            if (reading || busy || quitting) return;
            reading=true; int ticket=generation; var previous=known;
            try {
                var state=await Task.Run(()=>Inspect(previous));
                if (ticket==generation && !quitting) Display(state);
            } catch (Exception ex) { if (ticket==generation && !quitting) ShowError(ex); }
            finally { reading=false; }
        }
        private void ShowError(Exception ex)
        {
            status.Text="未能核实"; count.Text="检测未完成"; service.Text="请重新检测后台服务"; details.Text="";
            feedback.Text="未能完成核实。"+ex.GetBaseException().Message;
        }
        private static string StopService(IntPtr owner)
        {
            try { BackgroundService.Stop(); return ""; }
            catch (Win32Exception ex) {
                if (ex.NativeErrorCode!=5) return ex.Message;
            } catch (Exception ex) { return ex.GetBaseException().Message; }
            try {
                var info=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--stop-cowork"){
                    UseShellExecute=true, Verb="runas", ErrorDialogParentHandle=owner };
                using (var p=Process.Start(info)) {
                    p.WaitForExit(); return p.ExitCode==0 ? "" : "Cowork 服务或其子进程未能完全停止。";
                }
            } catch (Win32Exception ex) { return ex.NativeErrorCode==1223?"管理员授权已取消。":ex.Message; }
        }
        private async void RequestStop()
        {
            if (busy || quitting) return;
            busy=true; generation++; stop.IsEnabled=false; trayStop.Enabled=false;
            floating.Update(true,active); stopLabel.Text="正在关闭…"; status.Text="正在关闭";
            feedback.Text="正在结束 Claude 关联进程。停止 Cowork 服务时，Windows 可能请求管理员权限。";
            var identities=new Dictionary<int,long>(known);
            var owner=new WindowInteropHelper(window).Handle;
            try {
                string issue=await Task.Run(delegate {
                    tree.Stop(identities);
                    string problem=BackgroundService.Active() ? StopService(owner) : "";
                    tree.Stop(identities);
                    return problem;
                });
                var state=await Task.Run(()=>Inspect(identities));
                Display(state);
                feedback.Text=!state.Any && issue.Length==0 ? "关闭检查完成。未发现仍在运行的匹配进程或 Cowork 服务。"
                    : "关闭未完成。"+issue+(state.Nodes.Count>0?"请查看进程明细，可用管理员身份运行控制台后重试。":"")+
                    (state.Service?" Cowork 服务仍在运行。":"");
            } catch (Exception ex) { ShowError(ex); }
            finally {
                busy=false; stop.IsEnabled=true; trayStop.Enabled=true; stopLabel.Text="关闭 Claude"; floating.Update(false,active);
                if (!window.IsVisible || floating.Flyout.IsActive) tray.ShowBalloonTip(3500,"Claude 控制台",feedback.Text,Forms.ToolTipIcon.Info);
            }
        }
        private void Closing(object sender,CancelEventArgs e)
        {
            if (quitting) return;
            e.Cancel=true;
            if (exitDialog!=null) return;
            var panel=new StackPanel { Margin=new Thickness(21) };
            panel.Children.Add(new TextBlock { Text="关闭控制台窗口", FontSize=20, FontWeight=FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text="隐藏到托盘后，仍可使用悬浮标和托盘菜单。", Margin=new Thickness(0,8,0,17) });
            var buttons=new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Right };
            panel.Children.Add(buttons);
            var dialog=new Window { Owner=window, Title="关闭控制台窗口", Content=panel, SizeToContent=SizeToContent.WidthAndHeight,
                ResizeMode=ResizeMode.NoResize, ShowInTaskbar=false, WindowStartupLocation=WindowStartupLocation.CenterOwner,
                FontFamily=window.FontFamily, Background=window.Background };
            int choice=0;
            foreach (var title in new[]{"取消","退出程序","隐藏到托盘"}) {
                int result=buttons.Children.Count;
                var b=new Button { Content=title, Style=(Style)window.FindResource("Action"), FontSize=13, Margin=new Thickness(6,0,0,0),
                    IsCancel=result==0, IsDefault=result==2, IsEnabled=result!=1 || !busy };
                if (result!=2) { b.Background=FloatingControl.Color("#EEEAE2"); b.Foreground=FloatingControl.Color("#473B34"); }
                b.Click+=delegate { choice=result; dialog.DialogResult=true; }; buttons.Children.Add(b);
            }
            exitDialog=dialog;
            try { dialog.ShowDialog(); } finally { exitDialog=null; }
            if (choice==2) window.Dispatcher.BeginInvoke((Action)window.Hide);
            if (choice==1) window.Dispatcher.BeginInvoke((Action)Exit);
        }
        private void Exit()
        {
            if (busy) { Show(); return; }
            quitting=true; timer.Stop(); tray.Visible=false;
            Application.Current.Shutdown();
        }
        public void Dispose()
        {
            timer.Stop(); tray.Visible=false; tray.Dispose(); menu.Dispose(); icon.Dispose();
        }
    }
}
