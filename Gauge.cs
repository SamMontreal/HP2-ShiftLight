using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace HP2ShiftLight
{
    internal sealed class Gauge : Form
    {
        [DllImport("user32")] private static extern bool ReleaseCapture();
        [DllImport("user32")] private static extern IntPtr SendMessage(IntPtr h,int msg,IntPtr w,IntPtr l);
        [DllImport("user32")] private static extern bool SetProcessDPIAware();
        [DllImport("user32")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint fallback);
        [DllImport("user32", CharSet=CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("user32")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
        [DllImport("shcore")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
            public Rectangle Bounds { get { return Rectangle.FromLTRB(Left,Top,Right,Bottom); } }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Screen, Work;
            public uint Flags;
        }
        private static readonly Color Surface=Color.FromArgb(22,26,30), Muted=Color.FromArgb(165,178,187),
            Bright=Color.FromArgb(245,246,238), Mint=Color.FromArgb(103,216,175), Amber=Color.FromArgb(255,194,80);
        private const int PanelWidth=500, PanelHeight=96;
        private static readonly Color UpLit=Color.FromArgb(192,40,50), UpDim=Color.FromArgb(80,24,31),
            DownLit=Color.FromArgb(12,135,73), DownDim=Color.FromArgb(13,60,42);
        private readonly Stopwatch flashClock=new Stopwatch();
        private bool brightPhase;
        private readonly Font label=new Font("Microsoft YaHei UI",13,FontStyle.Bold,GraphicsUnit.Pixel);
        private readonly Font digits=new Font("Bahnschrift",33,FontStyle.Bold,GraphicsUnit.Pixel);
        private readonly Font gearFont=new Font("Bahnschrift",23,FontStyle.Bold,GraphicsUnit.Pixel);
        private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        private readonly Telemetry telemetry;
        private readonly Stopwatch displayCheck=Stopwatch.StartNew();
        private Rectangle lastAnchor;
        private float zoom=1f;
        private Reading current;
        private static readonly Rectangle CloseBox=new Rectangle(PanelWidth-32,32,25,28);

        public Gauge(string dir)
        {
            telemetry=new Telemetry(dir);
            Text="HP2 外置转速表"; AutoScaleMode=AutoScaleMode.None;
            FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual;
            BackColor=Surface; DoubleBuffered=true; TopMost=true;
            Opacity=0.70;
            current=telemetry.Poll();
            PlaceOnGameScreen();
            timer.Interval=30; timer.Tick+=UpdateGauge; timer.Start();
        }

        // Win32 returns physical desktop coordinates, including negative origins on secondary monitors.
        internal static Rectangle BottomCenter(Rectangle screen, Rectangle work, Rectangle game, uint dpi, out float scale)
        {
            bool fullscreen=!game.IsEmpty && game.Left<=screen.Left+2 && game.Top<=screen.Top+2 &&
                game.Right>=screen.Right-2 && game.Bottom>=screen.Bottom-2;
            Rectangle area=fullscreen || work.IsEmpty ? screen : work;
            scale=Math.Max(dpi/96f,Math.Min(2f,Math.Max(1f,screen.Width/2400f)));
            scale=Math.Min(scale,Math.Min(area.Width/(float)PanelWidth,area.Height/(float)(PanelHeight+16)));
            int width=(int)Math.Ceiling(PanelWidth*scale), height=(int)Math.Ceiling(PanelHeight*scale);
            int x=screen.Left+(screen.Width-width)/2;
            x=Math.Max(area.Left,Math.Min(x,area.Right-width));
            return new Rectangle(x,area.Bottom-height-(int)Math.Round(16*scale),width,height);
        }

        private void PlaceOnGameScreen()
        {
            IntPtr game=telemetry.GameWindow;
            IntPtr monitor=MonitorFromWindow(game,game==IntPtr.Zero?1u:2u);
            MonitorInfo info=new MonitorInfo { Size=Marshal.SizeOf(typeof(MonitorInfo)) };
            if (!GetMonitorInfo(monitor,ref info)) return;
            uint dpi=96, x, y;
            try { if (GetDpiForMonitor(monitor,0,out x,out y)==0 && x>0) dpi=x; }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            Rect gameRect;
            Rectangle gameBounds=game!=IntPtr.Zero && GetWindowRect(game,out gameRect) ? gameRect.Bounds : Rectangle.Empty;
            float newZoom;
            Rectangle anchor=BottomCenter(info.Screen.Bounds,info.Work.Bounds,gameBounds,dpi,out newZoom);
            if (anchor!=lastAnchor || zoom!=newZoom)
            {
                lastAnchor=anchor; zoom=newZoom; Bounds=anchor; Invalidate();
            }
            displayCheck.Restart();
        }

        private void UpdateGauge(object sender, EventArgs e)
        {
            Reading next=telemetry.Poll();
            if (displayCheck.ElapsedMilliseconds>=600) PlaceOnGameScreen();
            DisplayReading(next);
        }

        internal void DisplayReading(Reading next)
        {
            bool changed=next.Advice!=current.Advice || next.Car!=current.Car || next.Gear!=current.Gear;
            if (changed) flashClock.Restart();
            bool bright=next.Advice!=ShiftAdvice.None && flashClock.ElapsedMilliseconds%500<250;
            bool redraw=changed || bright!=brightPhase || next.Status!=current.Status ||
                Math.Round(next.Rpm)!=Math.Round(current.Rpm) || next.Top!=current.Top || next.Target!=current.Target ||
                next.NextRpm!=current.NextRpm || next.NextPower!=current.NextPower || next.NextGear!=current.NextGear ||
                next.Suggested!=current.Suggested || next.SuggestedRpm!=current.SuggestedRpm || next.ForceGain!=current.ForceGain;
            current=next;
            brightPhase=bright;
            if (redraw) Invalidate();
        }

        private static GraphicsPath Capsule(float x,float y,float width,float height)
        {
            GraphicsPath path=new GraphicsPath();
            path.AddArc(x,y,height,height,90,180);
            path.AddArc(x+width-height,y,height,height,270,180);
            path.CloseFigure();
            return path;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (ClientSize.Width<ClientSize.Height || ClientSize.Height<1) return;
            using (GraphicsPath path=Capsule(0,0,ClientSize.Width,ClientSize.Height))
                Region=new Region(path);
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg==0x21) { m.Result=(IntPtr)3; return; }   // MA_NOACTIVATE
            base.WndProc(ref m);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button!=MouseButtons.Left) return;
            if (CloseBox.Contains((int)(e.X/zoom),(int)(e.Y/zoom))) { Close(); return; }
            ReleaseCapture(); SendMessage(Handle,0xa1,(IntPtr)2,IntPtr.Zero);
        }

        private static void TextAt(Graphics g,string text,Font font,Color color,int x,int y)
        {
            using (Brush b=new SolidBrush(color)) g.DrawString(text,font,b,x,y,StringFormat.GenericTypographic);
        }
        private static void Box(Graphics g,Color color,int x,int y,int w,int h)
        {
            using (Brush b=new SolidBrush(color)) g.FillRectangle(b,x,y,w,h);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g=e.Graphics;
            bool up=current.Advice==ShiftAdvice.Up, down=current.Advice==ShiftAdvice.Down;
            bool alert=up || down;
            Color background=up?(brightPhase?UpLit:UpDim):down?(brightPhase?DownLit:DownDim):Surface;
            Color ink=alert?Color.White:Bright;
            Color secondary=alert?Color.FromArgb(234,241,235):Muted;
            g.Clear(background);
            g.ScaleTransform(zoom,zoom);
            g.SmoothingMode=SmoothingMode.AntiAlias;
            g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (GraphicsPath outline=Capsule(1,1,PanelWidth-2,PanelHeight-2))
            using (Pen edge=new Pen(alert?secondary:Color.FromArgb(81,99,103),1))
                g.DrawPath(edge,outline);

            TextAt(g,"RPM",label,secondary,30,13);
            string rpm=current.Driving?Math.Round(current.Rpm).ToString("0",CultureInfo.InvariantCulture):"—";
            TextAt(g,rpm,digits,ink,28,28);
            TextAt(g,"当前挡",label,secondary,123,13);
            string gear=current.Driving?(current.Gear<0?"R":current.Gear==0?"N":current.Gear.ToString()):"—";
            TextAt(g,gear,gearFont,ink,132,34);
            TextAt(g,"建议挡",label,secondary,180,13);
            string suggested=current.Suggested>0?current.Suggested.ToString():"—";
            TextAt(g,suggested,gearFont,alert?ink:Mint,189,34);

            string status=up?"↑ 升挡":down?"↓ 降挡":current.Status;
            if (!alert && current.Driving && status == null)
                status=current.Suggested>0 && current.Suggested<current.Gear?"再加速":current.Gear==current.Top?"最高挡":current.Target>0 && current.Rpm>=current.Target*.965?"准备升挡":"行驶中";
            TextAt(g,status,label,ink,244,17);
            if (current.Target>0)
                TextAt(g,"升挡目标 "+Math.Round(current.Target).ToString("0"),label,secondary,244,42);
            bool other=current.Suggested>0 && current.Suggested!=current.Gear;
            int dest=other?current.Suggested:current.NextGear;
            if (dest>0 && current.Driving)
            {
                double projected=other?current.SuggestedRpm:current.NextRpm;
                string landing=projected>0?dest+" 挡 ≈ "+Math.Round(projected).ToString("0")+" RPM":"等待起步";
                TextAt(g,landing,label,ink,352,17);
                if (other && current.Suggested<current.Gear)
                    TextAt(g,double.IsInfinity(current.ForceGain)?"恢复驱动力":current.ForceGain>=9?"驱动力 ≥10倍":
                        "驱动力 +"+(100*current.ForceGain).ToString("0")+"%",label,secondary,352,42);
                else if (current.NextPower>0) TextAt(g,"峰值功率 "+(100*current.NextPower).ToString("0")+"%",label,secondary,352,42);
            }
            double fraction=current.Driving?Math.Max(0,Math.Min(1,current.Rpm/(current.Target>0?current.Target:current.Limit))):0;
            for (int i=0;i<18;i++)
            {
                Color c=alert?Color.White:Color.FromArgb(56,66,72);
                if (!alert && fraction>=(i+1)/18.0) c=i>=15?Amber:Mint;
                Box(g,c,35+i*24,73,19,5);
            }
            using (Pen pen=new Pen(secondary,1.5f))
            {
                g.DrawLine(pen,PanelWidth-25,41,PanelWidth-17,49);
                g.DrawLine(pen,PanelWidth-17,41,PanelWidth-25,49);
            }
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop(); timer.Dispose(); telemetry.Dispose();
            label.Dispose(); digits.Dispose(); gearFont.Dispose();
            base.OnFormClosed(e);
        }
        internal static void EnableMonitorDpi()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; }
            catch (EntryPointNotFoundException) { }
            SetProcessDPIAware();
        }
        [STAThread]
        public static void Main()
        {
            bool first;
            using (Mutex one=new Mutex(true,@"Local\HP2RecompShiftLight",out first))
            {
                if (!first) return;
                EnableMonitorDpi();
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Gauge(AppDomain.CurrentDomain.BaseDirectory));
            }
        }
    }
}

