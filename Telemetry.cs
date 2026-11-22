using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HP2ShiftLight
{
    internal enum ShiftAdvice { None, Up, Down }

    internal struct Reading
    {
        public string Status;
        public int Car, Gear, Top, Suggested, NextGear;
        public float Rpm;
        public double SpeedKmh, LateralG, LongitudinalG;
        public bool HasG;
        public AirWarning Air;
        public double Target, NextRpm, NextPower, Limit, SuggestedRpm, SuggestedPower, CurrentPower, ForceGain;
        public ShiftAdvice Advice;
        public bool PreparingUp;
        public bool Driving { get { return Car != 0; } }
    }

    internal static class Power
    {
        // The PS2 engine interpolates nine equally spaced torque samples.
        // Within each interval, full-throttle power is proportional to r * (m*r + b).
        public static double Peak(byte[] info, double ceiling = double.PositiveInfinity)
        {
            double idle = BitConverter.ToSingle(info, 0);
            double limit = BitConverter.ToSingle(info, 8);
            if (!(idle >= 0 && idle < 4000 && limit > idle + 500 && limit <= 20000)) return 0;
            double width = (limit - idle) / 8, best = 0, at = 0;
            // GetEngineTorque cuts positive drive torque at limit minus 100 RPM.
            double last = Math.Min(ceiling, Math.Ceiling(limit - 100) - 1);
            for (int i = 0; i < 8; i++)
            {
                double r = idle + i * width, upper = Math.Min(r + width, last);
                double t = BitConverter.ToSingle(info, 16 + 4*i);
                double next = BitConverter.ToSingle(info, 20 + 4*i);
                if (double.IsNaN(t) || double.IsInfinity(t) || double.IsNaN(next) || double.IsInfinity(next)) return 0;
                if (upper < r) continue;
                double m = (next - t) / width;
                Check(r, t, ref best, ref at);
                Check(upper, t + m * (upper-r), ref best, ref at);
                if (m < 0)
                {
                    double apex = (r-t/m)/2;
                    if (apex > r && apex < upper) Check(apex, t + m*(apex-r), ref best, ref at);
                }
            }
            return at;
        }

        private static void Check(double r, double t, ref double best, ref double at)
        {
            double power = r*t;
            if (power > best) { best = power; at = r; }
        }
    }

    internal sealed class GearModel
    {
        private readonly byte[] curve = new byte[52];
        private readonly byte[] ratios=new byte[8*4];
        private readonly double[] relative=new double[9];
        private int currentCar, currentGear, topGear, upGear;
        public double Limit, Target, PeakPower, DownCeiling;
        private double idle, width, peakRpm;

        public bool Set(int car, int gear, int top, byte[] data, byte[] gears)
        {
            bool same=car==currentCar && gear==currentGear && top==topGear;
            for (int i=0;same && i<curve.Length;i++) same=data[i]==curve[i];
            for (int i=0;same && i<top*4;i++) same=gears[i]==ratios[i];
            if (same) return PeakPower>0;
            Buffer.BlockCopy(data,0,curve,0,curve.Length);
            Buffer.BlockCopy(gears,0,ratios,0,top*4);
            currentCar=car; currentGear=gear; topGear=top; upGear=0; Target=0; PeakPower=0;
            idle=BitConverter.ToSingle(curve,0); Limit=BitConverter.ToSingle(curve,8);
            peakRpm=Power.Peak(curve);
            if (peakRpm<=0) return false;
            width=(Limit-idle)/8;
            // Retail EngineInfo exposes maximum RPM at +8 and tach redline at +4.
            double redline=BitConverter.ToSingle(curve,4);
            if (!(redline>idle && redline<=Limit)) redline=.90*Limit;
            DownCeiling=Math.Min(redline,Limit-100)-Math.Max(100,.02*Limit);
            double driveRatio=gear>0 ? BitConverter.ToSingle(ratios,(gear-1)*4) : 1;
            if (!(driveRatio>0) || double.IsInfinity(driveRatio)) return false;
            PeakPower=peakRpm*Torque(peakRpm);
            for (int i=1;i<=top;i++)
            {
                double ratio=BitConverter.ToSingle(ratios,(i-1)*4)/driveRatio;
                relative[i]=ratio>0 && !double.IsInfinity(ratio) ? ratio : 0;
                if (gear>0 && i==gear+1 && ratio>0 && ratio<1)
                {
                    upGear=i;
                    Target=ShiftTarget(ratio);
                }
            }
            return PeakPower>0;
        }

        public Reading AtSpeed(float rpm, bool moving, double landingScale = 1, double upperScale = 1, double downScale = 1)
        {
            Reading row=new Reading { Car=currentCar,Gear=currentGear,Top=topGear,Rpm=rpm,
                Target=Target,Limit=Limit,CurrentPower=PowerFraction(rpm),NextGear=upGear };
            if (currentGear<=0) return row;
            row.Suggested=moving?currentGear:1;
            if (!moving) return row;

            double arrival=rpm*landingScale;
            row.SuggestedRpm=rpm;
            row.SuggestedPower=row.CurrentPower;
            if (upGear!=0) {
                if (landingScale!=1) row.Target=ShiftTarget(landingScale*relative[upGear]);
                row.NextRpm=arrival*relative[upGear];
                row.NextPower=PowerFraction(row.NextRpm);
            }
            double bestGain=0;
            for (int candidate=1;candidate<currentGear;candidate++)
            {
                double ratio=relative[candidate];
                if (ratio<=1 || rpm*ratio*Math.Max(1,upperScale)>=DownCeiling) continue;
                double now=rpm*ratio, then=rpm*downScale*ratio;
                if (Math.Min(now,then)<idle || Math.Max(now,then)>=Limit-100) continue;
                double currentChoice=PowerFraction(now), arrivalChoice=PowerFraction(then);
                double stay=PowerFraction(rpm*downScale);
                if (currentChoice<=row.CurrentPower || arrivalChoice<=stay) continue;
                // Both the immediate shift and the projected clutch engagement must win.
                double gain=Math.Min(RelativeGain(currentChoice,row.CurrentPower),RelativeGain(arrivalChoice,stay));
                if (gain<bestGain || (gain==bestGain && arrivalChoice<=row.SuggestedPower)) continue;
                bestGain=gain; row.Suggested=candidate; row.SuggestedRpm=then;
                row.SuggestedPower=arrivalChoice; row.ForceGain=gain;
            }
            return row;
        }

        private static double RelativeGain(double available,double current)
        {
            return current>0 ? available/current-1 : double.PositiveInfinity;
        }

        private double Torque(double rpm)
        {
            double x=Math.Max(0,Math.Min(8,(rpm-idle)/width));
            int segment=Math.Min(7,(int)x);
            double left=BitConverter.ToSingle(curve,16+4*segment);
            return left+(x-segment)*(BitConverter.ToSingle(curve,20+4*segment)-left);
        }

        public double PowerFraction(double rpm)
        {
            // The retail torque table clamps its input at idle when the current gear bogs down.
            return PeakPower>0 && rpm>0 && rpm<Limit-100 ? Math.Max(0,rpm*Torque(rpm))/PeakPower : 0;
        }

        private double ShiftTarget(double landingRatio)
        {
            if (!(landingRatio>0)) return 0;
            double cut=Limit-100;
            double target=peakRpm/landingRatio;
            if (target<cut) return target;
            // A wide ratio step can make the engine peak unreachable. Look for
            // the highest available landing power with room to shift before fuel cut.
            double attainable=Power.Peak(curve,(cut-75)*landingRatio);
            return attainable>0 ? attainable/landingRatio : 0;
        }
    }


    internal sealed class ShiftState
    {
        private int car, gear, rises, downGear;
        private long lastTime = -1;
        private double lastRpm, rpmPerMs;

        public void Reset()
        {
            car = gear = downGear = rises = 0;
            lastTime = -1;
            rpmPerMs = 0;
        }

        private void TrackRpm(double rpm, long now)
        {
            long dt = now - lastTime;
            if (lastTime >= 0 && dt > 0 && dt <= 220 && rpm > lastRpm)
            {
                double rate = (rpm - lastRpm) / dt;
                // Two rising intervals must agree before projecting ahead. Reject RPM spikes.
                if (rate <= 12)
                {
                    rpmPerMs = rises == 0 ? rate : Math.Min(rate, .5 * (rate + rpmPerMs));
                    rises = Math.Min(2, rises + 1);
                }
                else { rises = 0; rpmPerMs = 0; }
            }
            else { rises = 0; rpmPerMs = 0; }
            lastRpm = rpm;
            lastTime = now;
        }

        public void Apply(GearModel model, ref Reading row, float throttle, bool changingGear, long now)
        {
            if (changingGear)
            {
                Reset();
                row.Status = "换挡中";
                row.Suggested = row.NextGear = 0;
                row.SuggestedRpm = row.NextRpm = row.SuggestedPower = row.NextPower = row.Target = row.ForceGain = 0;
                return;
            }
            if (!row.Driving || row.Gear <= 0)
            {
                Reset();
                return;
            }
            if (car != row.Car || gear != row.Gear || lastTime < 0 || now - lastTime > 220 || now < lastTime)
            {
                Reset();
                car = row.Car;
                gear = row.Gear;
            }
            TrackRpm(row.Rpm, now);
            double rate = rises >= 2 ? rpmPerMs : 0;
            double lead = throttle >= .35f ? Math.Min(180, rate * 120) : 0;
            bool upAvailable = row.Target>0 && row.NextGear==gear+1 && row.NextPower>0;
            if (upAvailable && throttle>.05f) {
                row.PreparingUp = row.Rpm<row.Target && row.Rpm+lead>=row.Target;
                if (row.Rpm>=row.Target) {
                    row.Suggested=row.NextGear;
                    row.SuggestedRpm=row.NextRpm;
                    row.SuggestedPower=row.NextPower;
                    row.ForceGain=0;
                    downGear=0;
                    row.Advice=ShiftAdvice.Up;
                    return;
                }
            }

            // Report a safe lower gear on the first fresh physics sample.
            // A small release band prevents a tie from toggling the cue.
            bool lower=row.Suggested>0 && row.Suggested<gear;
            bool hold=lower && downGear==row.Suggested;
            if (lower && row.ForceGain>=(hold?.004:.01)) {
                downGear=row.Suggested;
                row.Advice=ShiftAdvice.Down;
            } else {
                downGear=0;
                row.Advice=ShiftAdvice.None;
            }
        }
    }



    // Observe road speed, including while the clutch is open. Engine flare during
    // a shift cannot be used to estimate the road speed at re-engagement.
    internal sealed class ShiftLanding
    {
        private int car, fromGear;
        private long last=-1, opened=-1;
        private double speed, rate, openSpeed;
        private readonly double[] duration={220,220}, drift={0,0};
        private readonly int[] samples=new int[2];

        public void Reset()
        {
            car=fromGear=0; last=opened=-1; speed=rate=0;
            Array.Clear(samples,0,2);
            for (int i=0;i<2;i++) { duration[i]=220; drift[i]=0; }
        }

        public void Observe(int id,int gear,double roadSpeed,bool changing,float throttle,long now)
        {
            if (car!=id || !(roadSpeed>1) || now<last || (last>=0 && now-last>220)) Reset();
            car=id;
            if (!(roadSpeed>1) || double.IsInfinity(roadSpeed)) return;
            if (last>=0 && now>last) {
                double instantaneous=(roadSpeed-speed)/(now-last);
                rate=Math.Abs(instantaneous)<.04 ? .55*rate+.45*instantaneous : 0;
            }
            if (changing && opened<0 && fromGear>0) {
                opened=last; openSpeed=speed;
            } else if (!changing && opened>=0) {
                long elapsed=now-opened;
                double change=(roadSpeed-openSpeed)/Math.Max(1,elapsed);
                if (gear>0 && Math.Abs(gear-fromGear)==1 && elapsed>=60 && elapsed<=750 &&
                    throttle>=.35f && openSpeed>5 && Math.Abs(change)<.025) {
                    int i=gear<fromGear?0:1;
                    double weight=1.0/Math.Min(5,++samples[i]);
                    duration[i]+=weight*(elapsed-duration[i]);
                    drift[i]+=weight*(change-drift[i]);
                }
                opened=-1;
            }
            last=now; speed=roadSpeed;
            if (!changing) fromGear=gear;
        }

        public double Scale(bool lower,out double upper, bool coasting = false)
        {
            int i=lower?0:1;
            // Blend a completed shift's measured speed change with current road acceleration.
            double predicted=samples[i]>0 && !coasting ? .65*drift[i]+.35*rate : rate;
            double span=duration[i], denom=Math.Max(1,speed);
            upper=1+Math.Max(0,Math.Max(rate,predicted))*(span+60)/denom;
            return 1+Math.Max(-.10,Math.Min(.10,predicted*span/denom));
        }
    }

    // Horizontal inertial load felt by the driver, positive right and forward.
    internal sealed class PlanarLoad
    {
        private struct Sample
        {
            public double T, X, Y;
        }
        private const double Window=.5;
        private readonly List<Sample> samples=new List<Sample>(40);
        private int vehicle;
        private double right, forward;

        public void Reset() { samples.Clear(); vehicle=0; }

        public void Apply(ref Reading row,int car,double seconds,int yaw,float ax,float ay)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || float.IsNaN(ax) || float.IsNaN(ay) ||
                float.IsInfinity(ax) || float.IsInfinity(ay)) { Reset(); return; }
            double angle=yaw*(2*Math.PI/65536), c=Math.Cos(angle), s=Math.Sin(angle);
            var point=new Sample {T=seconds,X=(ay*c-ax*s)/9.80665,Y=-(ax*c+ay*s)/9.80665};
            if (vehicle!=car || (samples.Count>0 && (seconds<samples[samples.Count-1].T ||
                seconds-samples[samples.Count-1].T>.3))) Reset();
            vehicle=car;
            if (samples.Count==0) { right=point.X; forward=point.Y; }
            if (samples.Count==0 || seconds>samples[samples.Count-1].T)
            {
                samples.Add(point);
                double cutoff=seconds-Window;
                int first=0;
                while (first+1<samples.Count && samples[first+1].T<=cutoff) first++;
                if (first>0) samples.RemoveRange(0,first);
                // Integrate the piecewise-linear samples, clipping the oldest interval.
                double areaX=0, areaY=0;
                for (int i=1;i<samples.Count;i++)
                {
                    Sample a=samples[i-1], b=samples[i];
                    double span=b.T-Math.Max(a.T,cutoff), fraction=span/(b.T-a.T);
                    areaX+=span*(b.X+.5*fraction*(a.X-b.X));
                    areaY+=span*(b.Y+.5*fraction*(a.Y-b.Y));
                }
                double duration=seconds-Math.Max(samples[0].T,cutoff);
                if (duration>0) { right=areaX/duration; forward=areaY/duration; }
            }
            row.HasG=true; row.LateralG=right; row.LongitudinalG=forward;
        }
    }

    internal sealed class Telemetry : IDisposable
    {
        [DllImport("kernel32", SetLastError=true)]
        private static extern SafeProcessHandle OpenProcess(uint rights, bool inherit, int id);
        [DllImport("kernel32", SetLastError=true)]
        private static extern bool ReadProcessMemory(SafeProcessHandle handle, IntPtr address, [Out] byte[] data, UIntPtr bytes, out UIntPtr count);
        private readonly string gameDir;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly PlanarLoad load = new PlanarLoad();
        private readonly byte[] acceleration = new byte[8];
        private readonly ShiftLanding landing = new ShiftLanding();
        private readonly ShiftState shift = new ShiftState();
        private readonly GearModel model = new GearModel();
        private SafeProcessHandle handle;
        private Process process, session;
        public bool GameEnded { get { return session!=null && session.HasExited; } }
        private long ram, retry, tickTime, replayUntil;
        private int lastTick, lastCar;
        private Reading lastReading;
        private readonly byte[] gearbox = new byte[16];
        private readonly byte[] word = new byte[4], motion = new byte[20], gearRatios = new byte[8*4], info = new byte[52];
        private string waiting = "等待 HP2 启动";
        private readonly Func<int,int> readWord;
        private const int MaxRam = 0x10000000;

        public Telemetry(string dir) { gameDir = Path.GetFullPath(dir); readWord=Int; }

        private Reading Idle(string why)
        {
            shift.Reset();
            landing.Reset();
            load.Reset();
            lastCar = 0;
            return new Reading { Status = why };
        }

        // HP2 can leave the driver in racing mode while its results list covers the car.
        // Follow the active screen list used by the runtime's menu_on_screen().
        private bool RaceMenuOpen()
        {
            int list=Int(0x3305a4);
            if (!IsGuestPointer(list)) return false;
            int node=Int(list+412);
            if (!IsGuestPointer(node)) return false;
            int head=node+8, screen=Int(head);
            if (screen==head || !IsGuestPointer(screen)) return false;
            int menu=Int(screen+8);
            if (!IsGuestPointer(menu)) return false;
            int item=Int(menu);
            return item!=menu && IsGuestPointer(item);
        }

        public Reading Poll()
        {
            try
            {
                if (GameEnded) return Idle("游戏已退出");
                if (process == null)
                {
                    if (clock.ElapsedMilliseconds >= retry) Attach();
                    if (process == null) return Idle(waiting);
                }
                if (Int(0x330544) != 6) { replayUntil=0; return Idle("已连接 · 等待比赛"); }
                int p = Int(0x330728);    // Player::Players[0], even in two-player races
                if (!IsGuestPointer(p)) return Idle("等待玩家车辆");
                int mode = Int(p + 196);
                int replay = Int(0x330148);
                if (mode == 1 || (IsGuestPointer(replay) && Int(replay+16) != 0)) return Idle("暂停");
                // The replay manager can clear its playback bit between displayed frames.
                if (mode==4 || mode==7 || (IsGuestPointer(replay) && Int(replay)!=0))
                    replayUntil=clock.ElapsedMilliseconds+250;
                if (clock.ElapsedMilliseconds<replayUntil) return Idle("回放");
                if ((mode==2 || mode==5) && RaceMenuOpen()) return Idle("赛事菜单");
                if (mode != 2) {
                    var waitingRow=Idle(mode == 5 ? "等待发车" : "等待驾驶");
                    if (mode==5) {
                        int gridCar=Int(p+4);
                        if (IsGuestPointer(gridCar)) waitingRow.Air=Pursuit.Read(readWord,gridCar);
                    }
                    return waitingRow;
                }
                int car = Int(p+4);
                if (!IsGuestPointer(car)) return Idle("等待玩家车辆");
                int mover = Int(car+48);
                if (!IsGuestPointer(mover) || Int(mover+4) != 0x2aeac8 || Int(mover+84) != car) return Idle("等待车辆物理数据");
                int engine = Int(mover+64), drive = Int(mover+68);
                if (!IsGuestPointer(engine) || !IsGuestPointer(drive)) return Idle("等待动力数据");
                int ei = Int(engine+16), di = Int(drive+16);
                if (!IsGuestPointer(ei) || !IsGuestPointer(di) || Int(drive+20) != engine) return Idle("等待动力数据");
                int top = Int(di+24);
                Read(ei, info);
                if (top < 1 || top > 8) return Idle("动力曲线不可用");
                Read(car+960, motion);
                float roadSpeed=BitConverter.ToSingle(motion,0);
                if (float.IsNaN(roadSpeed) || float.IsInfinity(roadSpeed)) return Idle("等待车速");
                bool moving=roadSpeed>1.0f;
                int gear = BitConverter.ToInt32(motion,8)-1;   // retail Gear enum: reverse, neutral, 1st …
                float throttle = BitConverter.ToSingle(motion,12), rpm = BitConverter.ToSingle(motion,16);
                if (gear < -1 || gear > top || !(rpm >= 0 && rpm < 24000) || Int(p+4) != car || Int(0x330728) != p) return Idle("等待车辆数据");
                int tick = Int(0x330c7c);   // WorldTimeSeconds as bits; frozen simulations must not leave a lamp on.
                bool fresh = tick != lastTick || car != lastCar;
                if (fresh) tickTime = clock.ElapsedMilliseconds;
                lastTick = tick; lastCar = car;
                if (clock.ElapsedMilliseconds - tickTime > 300)
                {
                    shift.Reset();
                    landing.Reset();
                    load.Reset();
                    return new Reading { Status = "等待比赛继续" };
                }
                if (!fresh) return lastReading;
                // Retail GetDesiredGear, GetCurrentGear and IsClutchEngaged read +44, +48 and +56.
                // Engine RPM is only a valid gear-ratio reference with the clutch locked.
                bool changingGear = false;
                if (moving && gear > 0)
                {
                    Read(drive+44, gearbox);
                    changingGear = BitConverter.ToInt32(gearbox,12) != 3 ||
                                   BitConverter.ToInt32(gearbox,0) != gear+1 ||
                                   BitConverter.ToInt32(gearbox,4) != gear+1;
                }
                // Forward ratios start at raw gear index 2 (reverse and neutral precede them).
                Read(di+40,gearRatios,top*4);
                if (!model.Set(car,gear,top,info,gearRatios)) return Idle("动力数据不可用");
                long now=clock.ElapsedMilliseconds;
                landing.Observe(car,gear,BitConverter.ToSingle(motion,0),changingGear,throttle,now);
                double upper, unused;
                double upScale=landing.Scale(false,out unused);
                double downScale=landing.Scale(true,out upper,throttle<=.05f);
                Reading row=model.AtSpeed(rpm,moving,upScale,upper,downScale);
                shift.Apply(model, ref row, moving ? throttle : 0, changingGear, now);
                row.SpeedKmh=Math.Abs((double)roadSpeed)*3.6;
                // GetForwardAcceleration uses the world acceleration at Car+0x80.
                // CarState::GetYawAlways reads the 16-bit heading at CarState+0x364.
                Read(car+0x80,acceleration);
                int heading=Int(car+0x3a4)&0xffff;
                if (Int(p+4)!=car || Int(0x330728)!=p) return Idle("等待玩家车辆");
                float seconds=BitConverter.ToSingle(BitConverter.GetBytes(tick),0);
                load.Apply(ref row,car,seconds,heading,BitConverter.ToSingle(acceleration,0),BitConverter.ToSingle(acceleration,4));
                row.Air=Pursuit.Read(readWord,car);
                lastReading = row;
                return row;
            }
            catch (Exception e)
            {
                if (!(e is IOException || e is InvalidOperationException || e is Win32Exception)) throw;
                ReleaseReader(); retry = clock.ElapsedMilliseconds+700;
                return Idle("等待游戏数据");
            }
        }

        // Only consider the original and cloud runtimes of this HP2 installation.
        // Derive the guest RAM base from the PE layout, then verify two retail code locations.
        public IntPtr GameWindow
        {
            get
            {
                if (session == null) return IntPtr.Zero;
                try
                {
                    session.Refresh();
                    return session.MainWindowHandle;
                }
                catch (InvalidOperationException) { return IntPtr.Zero; }
                catch (Win32Exception) { return IntPtr.Zero; }
            }
        }

        private void Attach()
        {
            retry = clock.ElapsedMilliseconds+900;
            waiting = "等待 HP2 启动";
            foreach (string rel in new string[] { @"Timelapse-Clouds\speed-clouds.exe", "speed.exe" })
            {
                string file = Path.GetFullPath(Path.Combine(gameDir,rel));
                Process[] candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(file));
                try
                {
                    foreach (Process candidate in candidates)
                    {
                        SafeProcessHandle read = null;
                        try
                        {
                            ProcessModule module = candidate.MainModule;
                            if (module == null || !String.Equals(file,module.FileName,StringComparison.OrdinalIgnoreCase)) continue;
                            if (session==null) {
                                try { session=Process.GetProcessById(candidate.Id); }
                                catch (ArgumentException) { continue; }
                            }
                            if (session.Id!=candidate.Id) continue;
                            long data = DataStart(file);
                            if (data <= 0) continue;
                            read = OpenProcess(0x1010,false,candidate.Id); // query-limited and read-only
                            if (read.IsInvalid) { waiting = "无法读取 HP2 进程"; continue; }
                            long location = module.BaseAddress.ToInt64()+data;
                            if (!Match(read,location+0x18ab70,0x27bdffd0,0xffb00000) ||
                                !Match(read,location+0x18f838,0xc4810008,0x46016034))
                            { waiting = "等待 HP2 0.14 游戏数据"; continue; }
                            process = candidate; handle = read; read = null; ram = location;
                            return;
                        }
                        catch (Exception e)
                        {
                            if (!(e is Win32Exception || e is IOException || e is InvalidOperationException || e is UnauthorizedAccessException)) throw;
                        }
                        finally { if (read != null) read.Dispose(); }
                    }
                }
                finally { foreach (Process q in candidates) if (!Object.ReferenceEquals(q,process)) q.Dispose(); }
            }
        }

        private static long DataStart(string file)
        {
            using (BinaryReader r = new BinaryReader(File.OpenRead(file)))
            {
                r.BaseStream.Position=0x3c; int pe=r.ReadInt32();
                r.BaseStream.Position=pe;
                if (r.ReadUInt32()!=0x4550 || r.ReadUInt16()!=0x8664) return 0;
                int sections=r.ReadUInt16();
                r.BaseStream.Position=pe+20; int opt=r.ReadUInt16();
                for (int i=0;i<sections;i++)
                {
                    r.BaseStream.Position=pe+24+opt+i*40;
                    string name=System.Text.Encoding.ASCII.GetString(r.ReadBytes(8)).TrimEnd('\0');
                    uint size=r.ReadUInt32(), address=r.ReadUInt32(), initialized=r.ReadUInt32();
                    if (name==".data" && size>=MaxRam)
                        return address + (((long)initialized+4095)&~4095L);
                }
                return 0;
            }
        }

        private static bool Match(SafeProcessHandle h, long address, uint a, uint b)
        {
            byte[] data=new byte[8]; UIntPtr count;
            return ReadProcessMemory(h,new IntPtr(address),data,(UIntPtr)8,out count) && count.ToUInt64()==8 &&
                BitConverter.ToUInt32(data,0)==a && BitConverter.ToUInt32(data,4)==b;
        }

        internal static bool IsGuestPointer(int a) { return (a & 3)==0 && a>=0x100000 && a < MaxRam-0x1000; }
        private void Read(int address, byte[] dst) { Read(address,dst,dst.Length); }
        private void Read(int address, byte[] dst, int length)
        {
            UIntPtr n;
            if (!IsGuestPointer(address) || !ReadProcessMemory(handle,new IntPtr(ram+address),dst,(UIntPtr)length,out n) || n.ToUInt64()!=(uint)length)
                throw new IOException("Game snapshot unavailable");
        }
        private int Int(int a) { Read(a,word); return BitConverter.ToInt32(word,0); }
        private void ReleaseReader()
        {
            if (handle!=null) { handle.Dispose(); handle=null; }
            if (process!=null) { process.Dispose(); process=null; }
            ram=0; lastCar=0;
        }

        public void Dispose()
        {
            ReleaseReader();
            if (session!=null) { session.Dispose(); session=null; }
        }
    }
}

