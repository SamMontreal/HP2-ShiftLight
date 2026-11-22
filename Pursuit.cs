using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HP2ShiftLight
{
    internal struct AirWarning
    {
        public string Status, Detail;
        public bool Committed;
        public bool Visible { get { return Status!=null; } }
    }

    internal static class Pursuit
    {
        private static bool Pointer(int value) { return Telemetry.IsGuestPointer(value); }
        private static readonly string[] AttackNames={"导弹","火药桶","双排火药桶","钉刺带"};
        // Retail UpdateChopper chooses one of four presets. FireWeapon dispatches the
        // preset through the game's table to Rocket, SmokeBomb or DroppedSpikes.
        public static AirWarning Read(Func<int,int> word,int car)
        {
            AirWarning off=new AirWarning();
            if (!Pointer(car) || word(0x31aaf4)<=0) return off; // same gate as police manager creation
            int police=word(0x331bec);
            if (!Pointer(police)) return off;
            int playerCop=word(police+12);
            if (playerCop>=0 && playerCop<7) {
                int cop=word(police+16+4*playerCop);
                if (Pointer(cop) && word(cop+4)==car) return off;
            }
            var idle=new AirWarning {Status="空域监视",Detail="尚未锁定本车"};
            int world=word(0x330288);
            if (!Pointer(world)) return idle;
            int heli=word(world+24);
            if (!Pointer(heli) || word(heli+0x3c0)!=0 || word(heli+0x2d4)!=car) return idle;
            int state=word(heli+0x2d8);
            var cue=new AirWarning {Status="直升机追踪",Detail="攻击待判定"};
            if (state==4) {
                int preset=word(heli+0x324);
                if (preset>=0 && preset<AttackNames.Length) {
                    cue.Committed=true;
                    cue.Status=word(heli+0x328)==0?"即将攻击":"攻击进行中";
                    cue.Detail=AttackNames[preset];
                }
            } else if (state==0 || state==3) {
                // Match the player's own pursuit in the circular cop-manager list,
                // then mirror the eligible branches before the game's random draw.
                int head=police+0x2c, node=word(head);
                for (int i=0;i<6 && node!=head && Pointer(node);i++,node=word(node)) {
                    if (word(node+16)!=car || word(node+24)!=heli || word(node+0x62c)!=3) continue;
                    int mask=word(node+0x634);
                    bool permitsSpecial=(word(car+0x84c)&255)==0;
                    bool rocket=permitsSpecial && (mask&0x100)!=0 && word(heli+0x324)!=0;
                    bool spikes=permitsSpecial && (mask&0x80)!=0;
                    cue.Detail=rocket && spikes ? "候选：桶 / 导弹 / 钉刺带" :
                        rocket ? "候选：桶 / 导弹" : spikes ? "候选：桶 / 钉刺带" : "预计：火药桶";
                    break;
                }
            } else return idle;
            return word(heli+0x2d4)==car && word(heli+0x2d8)==state ? cue : idle;
        }
    }

    internal static class HudTheme
    {
        internal static readonly Color Base=Color.FromArgb(26,26,27), Silver=Color.FromArgb(204,204,202),
            Orange=Color.FromArgb(255,169,49), White=Color.FromArgb(247,245,238);
        internal static GraphicsPath Outline(float x,float y,float width,float height,float cut)
        {
            var p=new GraphicsPath();
            p.AddPolygon(new PointF[]{new PointF(x+cut,y),new PointF(x+width,y),new PointF(x+width,y+height-cut),
                new PointF(x+width-cut,y+height),new PointF(x,y+height),new PointF(x,y+cut)});
            return p;
        }
        internal static void Frame(Graphics g,int width,int height,Color baseColor,Color signal)
        {
            var rect=new Rectangle(0,0,width,height);
            using (var fill=new LinearGradientBrush(rect,ControlPaint.Light(baseColor,.24f),ControlPaint.Dark(baseColor,.28f),90f))
            using (var border=new Pen(Color.FromArgb(155,151,145)))
            using (var path=Outline(1,1,width-2,height-2,8))
            using (var bar=new SolidBrush(signal)) {
                g.FillPath(fill,path); g.DrawPath(border,path);
                g.FillPolygon(bar,new[]{new Point(8,2),new Point(width/4,2),new Point(width/4-4,5),new Point(5,5)});
            }
        }
    }

    internal sealed class PursuitPanel : RaceOverlay
    {
        internal const int DesignWidth=248, Gap=6;
        private const int H=72;
        private readonly Font label=new Font("Microsoft YaHei UI",12,FontStyle.Bold,GraphicsUnit.Pixel);
        private readonly Font detail=new Font("Microsoft YaHei UI",18,FontStyle.Bold,GraphicsUnit.Pixel);
        private AirWarning reading;
        private float scale=1;
        internal PursuitPanel()
        {
            Text="HP2 空中预警"; BackColor=HudTheme.Base; Opacity=.90;
        }
        internal static Rectangle SideBounds(Rectangle main,Rectangle view,float zoom,out float scale)
        {
            int gap=Math.Max(1,(int)Math.Round(Gap*zoom));
            int right=view.Right-main.Right-gap, left=main.Left-view.Left-gap;
            bool placeRight=right>=Math.Ceiling(DesignWidth*zoom) || right>=left;
            int space=Math.Max(1,placeRight?right:left);
            scale=Math.Min(zoom,space/(float)DesignWidth);
            int width=Math.Min(space,(int)Math.Ceiling(DesignWidth*scale)), height=Math.Max(1,(int)Math.Ceiling(H*scale));
            int x=placeRight?main.Right+gap:main.Left-gap-width;
            return new Rectangle(x,main.Top+(main.Height-height)/2,width,height);
        }
        internal void UpdateFor(Form main,AirWarning next,float zoom,Rectangle view)
        {
            if (!next.Visible) { if (Visible) Hide(); return; }
            float newScale;
            var bounds=SideBounds(main.Bounds,view,zoom,out newScale);
            bool changed=reading.Status!=next.Status || reading.Detail!=next.Detail || reading.Committed!=next.Committed || scale!=newScale;
            reading=next; scale=newScale; Bounds=bounds;
            if (!Visible) Show(main);
            if (changed) Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g=e.Graphics;g.ScaleTransform(scale,scale);g.SmoothingMode=SmoothingMode.AntiAlias;
            var accent=reading.Committed?Color.FromArgb(255,103,58):HudTheme.Orange;
            HudTheme.Frame(g,DesignWidth,H,HudTheme.Base,accent);
            using(var bright=new SolidBrush(HudTheme.White))
            using(var colour=new SolidBrush(accent)) {
                g.DrawString("HP  /  空中预警",label,colour,9,10);
                g.DrawString(reading.Status,label,bright,152,10);
                g.DrawString(reading.Detail,detail,bright,9,32);
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { label.Dispose(); detail.Dispose(); }
            base.Dispose(disposing);
        }
    }
}