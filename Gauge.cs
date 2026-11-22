using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("HP2 ShiftLight")]
[assembly: System.Reflection.AssemblyVersion("1.7.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.7.0.0")]

namespace HP2ShiftLight
{
    // A layered window with WS_EX_TRANSPARENT passes mouse input through to
    // the game, including when the two windows live on different UI threads.
    internal class RaceOverlay : Form
    {
        private const int WS_EX_TRANSPARENT=0x20, WS_EX_TOOLWINDOW=0x80,
            WS_EX_LAYERED=0x80000, WS_EX_NOACTIVATE=0x08000000;

        protected RaceOverlay()
        {
            FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual;
            AutoScaleMode=AutoScaleMode.None; ShowInTaskbar=false; DoubleBuffered=true;
        }

        [DllImport("user32")]
        private static extern bool SetWindowPos(IntPtr window,IntPtr order,int x,int y,int cx,int cy,uint flags);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Form.SetVisibleCore explicitly requests keyboard focus when TopMost
            // is true. Set the native z-order without activating the form instead.
            const uint SWP_NOSIZE=1, SWP_NOMOVE=2, SWP_NOACTIVATE=0x10;
            SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,SWP_NOSIZE|SWP_NOMOVE|SWP_NOACTIVATE);
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get {
                var p=base.CreateParams;
                p.ExStyle|=WS_EX_NOACTIVATE|WS_EX_TRANSPARENT|WS_EX_LAYERED|WS_EX_TOOLWINDOW;
                return p;
            }
        }
        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE=0x21, MA_NOACTIVATE=3;
            if (m.Msg==WM_MOUSEACTIVATE) { m.Result=(IntPtr)MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }
    }

    internal sealed class Gauge : RaceOverlay
    {
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
        private static readonly Color Surface=HudTheme.Base, Muted=HudTheme.Silver,
            Bright=HudTheme.White, Amber=HudTheme.Orange;
        private const int PanelWidth=620, PanelHeight=108, LoadRange=3;
        private static readonly Color Red=Color.FromArgb(255,83,67), Green=Color.FromArgb(89,226,141),
            Unlit=Color.FromArgb(77,76,75);
        private readonly Stopwatch flashClock=new Stopwatch();
        private bool brightPhase;
        private readonly Font label=new Font("Microsoft YaHei UI",13,FontStyle.Bold,GraphicsUnit.Pixel);
        private readonly Font speedDigits=new Font("Bahnschrift",44,FontStyle.Bold|FontStyle.Italic,GraphicsUnit.Pixel);
        private readonly Font small=new Font("Microsoft YaHei UI",11,FontStyle.Regular,GraphicsUnit.Pixel);
        private readonly Font digits=new Font("Bahnschrift",33,FontStyle.Bold,GraphicsUnit.Pixel);
        private readonly Font gearFont=new Font("Bahnschrift",23,FontStyle.Bold,GraphicsUnit.Pixel);
        private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        private readonly Telemetry telemetry;
        private readonly PursuitPanel pursuit=new PursuitPanel();
        private readonly Stopwatch displayCheck=Stopwatch.StartNew();
        private Rectangle lastAnchor, gameArea;
        private bool reserveSide;
        private float zoom=1f;
        private Reading current;

        public Gauge(string dir)
        {
            telemetry=new Telemetry(dir);
            Text="HP2 驾驶仪表"; BackColor=Surface; Opacity=0.88;
            current=telemetry.Poll();
            reserveSide=current.Air.Visible;
            PlaceOnGameScreen(telemetry.GameWindow);
            timer.Interval=30; timer.Tick+=UpdateGauge; timer.Start();
        }

        // Win32 returns physical desktop coordinates, including negative origins on secondary monitors.
        private static Rectangle PlayArea(Rectangle screen,Rectangle work,Rectangle game)
        {
            bool fullscreen=!game.IsEmpty && game.Left<=screen.Left+2 && game.Top<=screen.Top+2 &&
                game.Right>=screen.Right-2 && game.Bottom>=screen.Bottom-2;
            Rectangle area=fullscreen || work.IsEmpty ? screen : work;
            if (!fullscreen && !game.IsEmpty) {
                var viewport=Rectangle.Intersect(area,game);
                if (viewport.Width>0 && viewport.Height>0) area=viewport;
            }
            return area;
        }

        internal static Rectangle BottomCenter(Rectangle screen, Rectangle work, Rectangle game, uint dpi, out float scale, bool withPursuit=false)
        {
            Rectangle area=PlayArea(screen,work,game);
            scale=Math.Max(dpi/96f,Math.Min(2f,Math.Max(1f,screen.Width/2400f)));
            int requiredWidth=PanelWidth+(withPursuit?2*(PursuitPanel.DesignWidth+PursuitPanel.Gap+2):0);
            scale=Math.Min(scale,Math.Min(area.Width/(float)requiredWidth,area.Height/(float)(PanelHeight+16)));
            int width=(int)Math.Ceiling(PanelWidth*scale), height=(int)Math.Ceiling(PanelHeight*scale);
            return new Rectangle(area.Left+(area.Width-width)/2,area.Bottom-height-(int)Math.Round(16*scale),width,height);
        }

        private void PlaceOnGameScreen(IntPtr game)
        {
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
            gameArea=PlayArea(info.Screen.Bounds,info.Work.Bounds,gameBounds);
            Rectangle anchor=BottomCenter(info.Screen.Bounds,info.Work.Bounds,gameBounds,dpi,out newZoom,reserveSide);
            if (anchor!=lastAnchor || zoom!=newZoom)
            {
                lastAnchor=anchor; zoom=newZoom; Bounds=anchor; Invalidate();
            }
            displayCheck.Restart();
        }

        private void UpdateGauge(object sender, EventArgs e)
        {
            Reading next=telemetry.Poll();
            if (telemetry.GameEnded) { Close(); return; }
            if (displayCheck.ElapsedMilliseconds>=600 || reserveSide!=next.Air.Visible) {
                reserveSide=next.Air.Visible;
                PlaceOnGameScreen(telemetry.GameWindow);
            }
            DisplayReading(next);
            pursuit.UpdateFor(this,next.Air,zoom,gameArea);
        }

        internal void DisplayReading(Reading next)
        {
            bool changed=next.Advice!=current.Advice || next.Car!=current.Car || next.Gear!=current.Gear;
            if (changed) flashClock.Restart();
            bool bright=next.Advice==ShiftAdvice.Up && flashClock.ElapsedMilliseconds%360<180;
            bool redraw=changed || bright!=brightPhase || next.Status!=current.Status || next.LitLamps!=current.LitLamps ||
                next.HasG!=current.HasG || Math.Round(next.SpeedKmh)!=Math.Round(current.SpeedKmh) ||
                Math.Round(next.LateralG,2)!=Math.Round(current.LateralG,2) || Math.Round(next.LongitudinalG,2)!=Math.Round(current.LongitudinalG,2) ||
                Math.Round(next.Rpm)!=Math.Round(current.Rpm) || next.Top!=current.Top || next.Suggested!=current.Suggested ||
                LandingPercent(next)!=LandingPercent(current);
            current=next;
            brightPhase=bright;
            if (redraw) Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (ClientSize.Width<8 || ClientSize.Height<8) return;
            Region old=Region;
            using (GraphicsPath path=HudTheme.Outline(0,0,ClientSize.Width,ClientSize.Height,8*zoom))
                Region=new Region(path);
            if (old!=null) old.Dispose();
        }

        private static void TextAt(Graphics g,string text,Font font,Color color,int x,int y)
        {
            using (Brush b=new SolidBrush(color)) g.DrawString(text,font,b,x,y,StringFormat.GenericTypographic);
        }
        private static void Box(Graphics g,Color color,int x,int y,int w,int h)
        {
            using (Brush b=new SolidBrush(color)) g.FillRectangle(b,x,y,w,h);
        }
        private static string GearName(int n) { return n<0?"R":n==0?"N":n.ToString(); }
        internal static int LandingPercent(Reading row)
        {
            if (row.Advice==ShiftAdvice.Down && row.SuggestedRpm>0)
                return (int)Math.Round(100*row.SuggestedPower);
            if (row.NextGear>0 && row.NextRpm>0)
                return (int)Math.Round(100*row.NextPower);
            return -1;
        }

        private void DrawLoad(Graphics g, Color ink, Color secondary)
        {
            const int cx=471,cy=53,r=33;
            using (Pen grid=new Pen(Color.FromArgb(153,149,144),1))
            {
                for (int level=1;level<=LoadRange;level++) {
                    float radius=r*level/(float)LoadRange;
                    g.DrawEllipse(grid,cx-radius,cy-radius,2*radius,2*radius);
                }
                g.DrawLine(grid,cx-r,cy,cx+r,cy);
                g.DrawLine(grid,cx,cy-r,cx,cy+r);
            }
            if (current.HasG)
            {
                // Clip the vector at the rim while keeping both numeric readings intact.
                double length=Math.Sqrt(current.LateralG*current.LateralG+current.LongitudinalG*current.LongitudinalG);
                float scale=(float)(r/Math.Max(LoadRange,length));
                float dx=(float)current.LateralG*scale, dy=(float)-current.LongitudinalG*scale;
                using (Pen stem=new Pen(Amber,2)) g.DrawLine(stem,cx,cy,cx+dx,cy+dy);
                using (Brush dot=new SolidBrush(Amber)) g.FillEllipse(dot,cx+dx-3.5f,cy+dy-3.5f,7,7);
            }
            TextAt(g,"平均 G",label,ink,518,12);
            string x=current.HasG?SignedG(current.LateralG):"—", y=current.HasG?SignedG(current.LongitudinalG):"—";
            TextAt(g,"横向",small,secondary,518,39); TextAt(g,x,label,ink,551,36);
            TextAt(g,"纵向",small,secondary,518,62); TextAt(g,y,label,ink,551,59);
            TextAt(g,LoadRange+" g",small,secondary,462,87);
        }
        private static string SignedG(double v)
        {
            return (Math.Abs(v)<.005?0:v).ToString("+0.00;-0.00;0.00",CultureInfo.InvariantCulture);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g=e.Graphics;
            bool up=current.Advice==ShiftAdvice.Up, down=current.Advice==ShiftAdvice.Down;
            bool alert=up || down;
            Color accent=up?Red:down?Green:Amber;
            g.Clear(Surface); g.ScaleTransform(zoom,zoom); g.SmoothingMode=SmoothingMode.AntiAlias;
            g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            HudTheme.Frame(g,PanelWidth,PanelHeight,Surface,Amber);
            using (Pen line=new Pen(Color.FromArgb(106,104,100),1))
            {
                foreach (int x in new int[]{119,275,425}) g.DrawLine(line,x,12,x,PanelHeight-12);
            }
            bool live=current.Driving;
            TextAt(g,"车速",label,Muted,15,11);
            TextAt(g,live?Math.Round(current.SpeedKmh).ToString("0"):"—",speedDigits,Bright,13,24);
            TextAt(g,"km/h",label,Muted,17,82);
            TextAt(g,"转速",label,Muted,132,11);
            TextAt(g,live?Math.Round(current.Rpm).ToString("0"):"—",digits,Bright,130,30);
            TextAt(g,"RPM",small,Muted,227,50);
            for (int i=0;i<ShiftState.LampCount;i++) {
                Color lamp=Unlit;
                if (up && brightPhase) lamp=Red;
                if (!up && i<current.LitLamps) lamp=i<4?Green:i<7?Amber:Red;
                Box(g,lamp,133+i*13,78,11,9);
            }

            TextAt(g,"当前挡",small,Muted,288,11); TextAt(g,"建议挡",small,Muted,370,11);
            TextAt(g,live?GearName(current.Gear):"—",digits,Bright,289,26);
            if (alert) TextAt(g,"→",gearFont,accent,329,30);
            TextAt(g,alert?GearName(current.Suggested):"—",digits,alert?accent:Muted,375,26);
            string status=up?"↑ 升挡":down?"↓ 降挡":current.Status;
            if (status==null && live && current.Gear==current.Top) status="最高挡";
            if (status!=null) TextAt(g,status,label,alert?accent:Muted,288,62);
            if (live) {
                int power=LandingPercent(current);
                TextAt(g,"入挡功率",small,Muted,288,86);
                TextAt(g,power>=0?power+"%":"—",label,Bright,352,83);
            }
            DrawLoad(g,Bright,Muted);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !IsDisposed) {
                timer.Stop(); timer.Dispose(); telemetry.Dispose(); pursuit.Dispose();
                label.Dispose(); digits.Dispose(); gearFont.Dispose(); speedDigits.Dispose(); small.Dispose();
            }
            base.Dispose(disposing);
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
                using (var gauge=new Gauge(AppDomain.CurrentDomain.BaseDirectory)) Application.Run(gauge);
            }
        }
    }
}

