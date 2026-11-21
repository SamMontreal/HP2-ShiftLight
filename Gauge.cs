using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("HP2 ShiftLight")]
[assembly: System.Reflection.AssemblyVersion("1.6.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.6.0.0")]

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
        private static readonly Color Surface=HudTheme.Base, Muted=HudTheme.Silver,
            Bright=HudTheme.White, Amber=HudTheme.Orange;
        private const int PanelWidth=620, PanelHeight=108;
        private static readonly Color UpLit=Color.FromArgb(192,40,50), UpDim=Color.FromArgb(80,24,31),
            DownLit=Color.FromArgb(12,135,73), DownDim=Color.FromArgb(13,60,42);
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
        private static readonly Rectangle CloseBox=new Rectangle(PanelWidth-26,3,22,23);

        public Gauge(string dir)
        {
            telemetry=new Telemetry(dir);
            Text="HP2 驾驶仪表"; AutoScaleMode=AutoScaleMode.None;
            FormBorderStyle=FormBorderStyle.None; StartPosition=FormStartPosition.Manual;
            BackColor=Surface; DoubleBuffered=true; TopMost=true;
            Opacity=0.88;
            current=telemetry.Poll();
            PlaceOnGameScreen();
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
                PlaceOnGameScreen();
            }
            DisplayReading(next);
            pursuit.UpdateFor(this,next.Air,zoom,gameArea);
        }

        internal void DisplayReading(Reading next)
        {
            bool changed=next.Advice!=current.Advice || next.Car!=current.Car || next.Gear!=current.Gear;
            if (changed) flashClock.Restart();
            bool bright=next.Advice!=ShiftAdvice.None && flashClock.ElapsedMilliseconds%500<250;
            bool redraw=changed || bright!=brightPhase || next.Status!=current.Status || next.PreparingUp!=current.PreparingUp ||
                next.HasG!=current.HasG || Math.Round(next.SpeedKmh)!=Math.Round(current.SpeedKmh) ||
                Math.Round(next.LateralG,2)!=Math.Round(current.LateralG,2) || Math.Round(next.LongitudinalG,2)!=Math.Round(current.LongitudinalG,2) ||
                Math.Round(next.Rpm)!=Math.Round(current.Rpm) || next.Top!=current.Top || next.Target!=current.Target ||
                next.NextRpm!=current.NextRpm || next.NextPower!=current.NextPower || next.NextGear!=current.NextGear ||
                next.Suggested!=current.Suggested || next.SuggestedRpm!=current.SuggestedRpm || next.ForceGain!=current.ForceGain;
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
        private static string GearName(int n) { return n<0?"R":n==0?"N":n.ToString(); }

        private void DrawLoad(Graphics g, Color ink, Color secondary)
        {
            const int cx=471,cy=53,r=33;
            using (Pen grid=new Pen(Color.FromArgb(153,149,144),1))
            {
                g.DrawEllipse(grid,cx-r,cy-r,2*r,2*r);
                g.DrawEllipse(grid,cx-r/2f,cy-r/2f,r,r);
                g.DrawLine(grid,cx-r,cy,cx+r,cy);
                g.DrawLine(grid,cx,cy-r,cx,cy+r);
            }
            if (current.HasG)
            {
                // 1 g per ring. Clamp only the marker at the outer 2 g ring.
                double length=Math.Sqrt(current.LateralG*current.LateralG+current.LongitudinalG*current.LongitudinalG);
                float scale=(float)(r/Math.Max(2,length));
                float dx=(float)current.LateralG*scale, dy=(float)-current.LongitudinalG*scale;
                using (Pen stem=new Pen(Amber,2)) g.DrawLine(stem,cx,cy,cx+dx,cy+dy);
                using (Brush dot=new SolidBrush(Amber)) g.FillEllipse(dot,cx+dx-3.5f,cy+dy-3.5f,7,7);
            }
            TextAt(g,"平均 G",label,ink,518,12);
            string x=current.HasG?SignedG(current.LateralG):"—", y=current.HasG?SignedG(current.LongitudinalG):"—";
            TextAt(g,"横向",small,secondary,518,39); TextAt(g,x,label,ink,551,36);
            TextAt(g,"纵向",small,secondary,518,62); TextAt(g,y,label,ink,551,59);
            TextAt(g,"每圈 1 g",small,secondary,449,87);
            TextAt(g,"最近 0.5 秒",small,secondary,518,87);
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
            Color accent=up?Color.FromArgb(255,100,100):down?Color.FromArgb(93,242,153):current.PreparingUp?Amber:Amber;
            Color shade=up?(brightPhase?UpLit:UpDim):down?(brightPhase?DownLit:DownDim):Surface;
            Color secondary=alert?Color.FromArgb(232,239,234):Muted;
            g.Clear(shade); g.ScaleTransform(zoom,zoom); g.SmoothingMode=SmoothingMode.AntiAlias;
            g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            HudTheme.Frame(g,PanelWidth,PanelHeight,shade,accent);
            using (Pen line=new Pen(Color.FromArgb(106,104,100),1))
            {
                foreach (int x in new int[]{119,275,425}) g.DrawLine(line,x,12,x,PanelHeight-12);
            }
            bool live=current.Driving;
            TextAt(g,"车速",label,secondary,15,11);
            TextAt(g,live?Math.Round(current.SpeedKmh).ToString("0"):"—",speedDigits,Bright,13,24);
            TextAt(g,"km/h",label,secondary,17,82);
            TextAt(g,"发动机",label,secondary,132,11);
            TextAt(g,live?Math.Round(current.Rpm).ToString("0"):"—",digits,Bright,130,30);
            TextAt(g,"RPM",small,secondary,223,50);
            double limit=current.Target>0?current.Target:current.Limit;
            double fraction=live && limit>0?Math.Min(1,current.Rpm/limit):0;
            for (int i=0;i<16;i++)
                Box(g,fraction>=(i+1)/16.0?(i>=13?Color.FromArgb(255,104,46):accent):Color.FromArgb(83,82,81),133+i*8,72,6,5);
            TextAt(g,current.Target>0?"升挡目标  "+Math.Round(current.Target).ToString("0"):"升挡目标  —",small,secondary,132,84);

            TextAt(g,"当前挡",small,secondary,288,11); TextAt(g,"建议挡",small,secondary,370,11);
            TextAt(g,live?GearName(current.Gear):"—",digits,Bright,289,26);
            TextAt(g,"→",gearFont,secondary,329,30);
            TextAt(g,live && current.Suggested>0?GearName(current.Suggested):"—",digits,accent,375,26);
            string status=up?"↑ 升挡":down?"↓ 降挡":current.Status;
            if (status==null) status=current.PreparingUp?"准备升挡":current.Gear==current.Top?"最高挡":"行驶中";
            TextAt(g,status,label,alert?Color.White:secondary,288,65);
            bool lower=live && current.Suggested>0 && current.Suggested<current.Gear;
            double predicted=lower?current.SuggestedRpm:current.NextRpm;
            if (predicted>0)
                TextAt(g,(lower?current.Suggested:current.NextGear)+" 挡 ≈ "+Math.Round(predicted).ToString("0")+" RPM",small,secondary,288,86);
            if (lower && current.ForceGain>0)
                TextAt(g,double.IsInfinity(current.ForceGain)?"驱动力恢复":"力 +"+(current.ForceGain*100).ToString("0")+"%",small,secondary,353,67);
            else if (current.NextPower>0)
                TextAt(g,"功率 "+(100*current.NextPower).ToString("0")+"%",small,secondary,353,67);
            DrawLoad(g,Bright,secondary);
            using (Pen pen=new Pen(secondary,1.4f))
            {
                int x=CloseBox.X+7,y=CloseBox.Y+7;
                g.DrawLine(pen,x,y,x+7,y+7); g.DrawLine(pen,x+7,y,x,y+7);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop(); timer.Dispose(); telemetry.Dispose();
            pursuit.Dispose(); label.Dispose(); digits.Dispose(); gearFont.Dispose(); speedDigits.Dispose(); small.Dispose();
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

