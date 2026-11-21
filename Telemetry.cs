using System;
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
        public double Target, NextRpm, NextPower, Limit, SuggestedRpm, SuggestedPower, CurrentPower, ForceGain;
        public ShiftAdvice Advice;
        public bool Driving { get { return Car != 0; } }
    }

    internal static class Power
    {
        // The PS2 engine interpolates nine equally spaced torque samples.
        // Within each interval, full-throttle power is proportional to r * (m*r + b).
        public static double Peak(byte[] info)
        {
            double idle = BitConverter.ToSingle(info, 0);
            double limit = BitConverter.ToSingle(info, 8);
            if (!(idle >= 0 && idle < 4000 && limit > idle + 500 && limit <= 20000)) return 0;
            double width = (limit - idle) / 8, best = 0, at = 0;
            // GetEngineTorque cuts positive drive torque at limit minus 100 RPM.
            double last = Math.Ceiling(limit - 100) - 1;
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
        public double Limit, Target, PeakPower;
        private double idle, width;

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
            double peakRpm=Power.Peak(curve);
            if (peakRpm<=0) return false;
            width=(Limit-idle)/8;
            double driveRatio=gear>0 ? BitConverter.ToSingle(ratios,(gear-1)*4) : 1;
            if (!(driveRatio>0) || double.IsInfinity(driveRatio)) return false;
            PeakPower=peakRpm*Torque(peakRpm);
            for (int i=1;i<=top;i++)
            {
                double ratio=BitConverter.ToSingle(ratios,(i-1)*4)/driveRatio;
                relative[i]=ratio>0 && !double.IsInfinity(ratio) ? ratio : 0;
                if (gear<=0 || i<=gear || !(ratio>0 && ratio<1)) continue;
                double point=FindShift(ratio);
                if (upGear==0 || point<Target) { Target=point; upGear=i; }
            }
            return PeakPower>0;
        }

        public Reading AtSpeed(float rpm, bool moving)
        {
            Reading row=new Reading { Car=currentCar,Gear=currentGear,Top=topGear,Rpm=rpm,
                Target=Target,Limit=Limit,CurrentPower=PowerFraction(rpm),NextGear=upGear };
            if (currentGear<=0) return row;
            row.Suggested=moving?currentGear:1;
            if (!moving) return row;

            row.SuggestedRpm=rpm; row.SuggestedPower=row.CurrentPower;
            if (upGear!=0) row.NextRpm=rpm*relative[upGear];
            // At a fixed wheel speed, maximizing available wheel force is equivalent to
            // maximizing engine power. Common final drive and wheel radius cancel.
            for (int gear=1;gear<=topGear;gear++)
            {
                if (gear==currentGear || relative[gear]<=0) continue;
                double projected=rpm*relative[gear];
                if (projected<idle || projected>=Limit-100 || (gear<currentGear && projected>Limit-300)) continue;
                double power=PowerFraction(projected);
                if (gear>currentGear && relative[gear]<1 && power>row.NextPower)
                {
                    row.NextGear=gear; row.NextRpm=projected; row.NextPower=power;
                }
                if (power<=row.SuggestedPower+1e-7) continue;
                row.Suggested=gear; row.SuggestedRpm=projected; row.SuggestedPower=power;
            }
            if (row.CurrentPower>0) row.ForceGain=row.SuggestedPower/row.CurrentPower-1;
            else if (row.SuggestedPower>0) row.ForceGain=double.PositiveInfinity;
            return row;
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

        private double Gain(double rpm, double ratio)
        {
            // Same road speed, same final drive: next wheel torque minus current,
            // divided by the current gear ratio. The differential cancels.
            return ratio*Torque(rpm*ratio)-Torque(rpm);
        }

        private double FindShift(double ratio)
        {
            // Both wheel-force curves are linear between their combined breakpoints.
            // Walk back from fuel cut to find the start of the sustained next-gear advantage.
            double last=Math.Ceiling(Limit-100)-1;
            double first=idle/ratio;
            if (first>last || Gain(last,ratio)<0) return last;
            double[] marks=new double[20];
            int n=0;
            marks[n++]=first; marks[n++]=last;
            for (int i=1;i<=8;i++)
            {
                double p=idle+i*width;
                if (p>first && p<last) marks[n++]=p;
                p/=ratio;
                if (p>first && p<last) marks[n++]=p;
            }
            Array.Sort(marks,0,n);
            for (int i=n-1;i>0;i--)
            {
                double a=marks[i-1], b=marks[i], ga=Gain(a,ratio), gb=Gain(b,ratio);
                if (ga<0) return a+(b-a)*(-ga)/(gb-ga);
            }
            return first;
        }
    }


    internal sealed class ShiftState
    {
        private const double DownGain = .04;
        private const double KeepDownGain = .02;
        private const int DownConfirmMs = 60;
        private int car, gear, downGear, rises;
        private long downSince, lastTime = -1;
        private double lastRpm, rpmPerMs;
        private ShiftAdvice advice;

        public void Reset()
        {
            car = gear = downGear = rises = 0;
            lastTime = -1;
            rpmPerMs = 0;
            advice = ShiftAdvice.None;
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
            if (!row.Driving || row.Gear <= 0 || !(throttle > .05f))
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
            bool upAvailable = row.Target > 0 && row.NextGear > gear && row.NextPower > 0 && row.Suggested >= gear;
            if (upAvailable)
            {
                bool crossed = row.Rpm >= row.Target || (row.Suggested > gear && row.ForceGain >= DownGain);
                bool close = row.NextPower >= .985 * row.CurrentPower && row.Rpm + lead >= row.Target;
                bool holding = advice == ShiftAdvice.Up && row.Rpm >= row.Target - 45 &&
                               row.NextPower >= .995 * row.CurrentPower;
                if (crossed || close || holding)
                {
                    row.Suggested = row.NextGear;
                    row.SuggestedRpm = row.NextRpm;
                    row.SuggestedPower = row.NextPower;
                    row.ForceGain = row.CurrentPower > 0 ? row.NextPower / row.CurrentPower - 1 : double.PositiveInfinity;
                    downGear = 0;
                    row.Advice = advice = ShiftAdvice.Up;
                    return;
                }
            }

            bool holdingDown = advice == ShiftAdvice.Down && downGear == row.Suggested;
            bool down = row.Suggested > 0 && row.Suggested < gear &&
                        throttle >= (holdingDown ? .18f : .25f) &&
                        row.ForceGain >= (holdingDown ? KeepDownGain : DownGain);
            if (down && rate > 0)
            {
                // Avoid a downshift that would already run out of headroom during the input.
                double ahead = Math.Min(350, rate * 200);
                double landing = row.SuggestedRpm * (1 + ahead / row.Rpm);
                down = landing < row.Limit - 100 &&
                       model.PowerFraction(landing) >= model.PowerFraction(row.Rpm + ahead) * (1 + KeepDownGain);
            }
            if (down)
            {
                if (downGear != row.Suggested) { downGear = row.Suggested; downSince = now; }
                if (now - downSince >= DownConfirmMs)
                {
                    row.Advice = advice = ShiftAdvice.Down;
                    return;
                }
            }
            else downGear = 0;
            row.Advice = advice = ShiftAdvice.None;
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
        private readonly ShiftState shift = new ShiftState();
        private readonly GearModel model = new GearModel();
        private SafeProcessHandle handle;
        private Process process;
        private long ram, retry, tickTime;
        private int lastTick, lastCar;
        private Reading lastReading;
        private readonly byte[] gearbox = new byte[16];
        private readonly byte[] word = new byte[4], motion = new byte[20], gearRatios = new byte[8*4], info = new byte[52];
        private string waiting = "等待 HP2 启动";
        private const int MaxRam = 0x10000000;

        public Telemetry(string dir) { gameDir = Path.GetFullPath(dir); }

        private Reading Idle(string why)
        {
            shift.Reset();
            lastCar = 0;
            return new Reading { Status = why };
        }

        public Reading Poll()
        {
            try
            {
                if (process != null && process.HasExited) { Dispose(); retry = 0; }
                if (process == null)
                {
                    if (clock.ElapsedMilliseconds >= retry) Attach();
                    if (process == null) return Idle(waiting);
                }
                if (Int(0x330544) != 6) return Idle("已连接 · 等待比赛");
                int p = Int(0x330728);    // Player::Players[0], even in two-player races
                if (!Pointer(p)) return Idle("等待玩家车辆");
                int mode = Int(p + 196);
                int replay = Int(0x330148);
                if (mode == 1 || (Pointer(replay) && Int(replay+16) != 0)) return Idle("暂停");
                if (mode == 4 || mode == 7 || (Pointer(replay) && Int(replay) != 0)) return Idle("回放");
                if (mode != 2) return Idle(mode == 5 ? "等待发车" : "等待驾驶");
                int car = Int(p+4);
                if (!Pointer(car)) return Idle("等待玩家车辆");
                int mover = Int(car+48);
                if (!Pointer(mover) || Int(mover+4) != 0x2aeac8 || Int(mover+84) != car) return Idle("等待车辆物理数据");
                int engine = Int(mover+64), drive = Int(mover+68);
                if (!Pointer(engine) || !Pointer(drive)) return Idle("等待动力数据");
                int ei = Int(engine+16), di = Int(drive+16);
                if (!Pointer(ei) || !Pointer(di) || Int(drive+20) != engine) return Idle("等待动力数据");
                int top = Int(di+24);
                Read(ei, info);
                if (top < 1 || top > 8) return Idle("动力曲线不可用");
                Read(car+960, motion);
                bool moving=BitConverter.ToSingle(motion,0)>1.0f;
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
                Reading row=model.AtSpeed(rpm,moving);
                shift.Apply(model, ref row, moving ? throttle : 0, changingGear, clock.ElapsedMilliseconds);
                lastReading = row;
                return row;
            }
            catch (Exception e)
            {
                if (!(e is IOException || e is InvalidOperationException || e is Win32Exception)) throw;
                Dispose(); retry = clock.ElapsedMilliseconds+700;
                return Idle("等待游戏数据");
            }
        }

        // Only consider the original and cloud runtimes of this HP2 installation.
        // Derive the guest RAM base from the PE layout, then verify two retail code locations.
        public IntPtr GameWindow
        {
            get
            {
                if (process == null) return IntPtr.Zero;
                try
                {
                    process.Refresh();
                    return process.MainWindowHandle;
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

        private static bool Pointer(int a) { return (a & 3)==0 && a>=0x100000 && a < MaxRam-0x1000; }
        private void Read(int address, byte[] dst) { Read(address,dst,dst.Length); }
        private void Read(int address, byte[] dst, int length)
        {
            UIntPtr n;
            if (!Pointer(address) || !ReadProcessMemory(handle,new IntPtr(ram+address),dst,(UIntPtr)length,out n) || n.ToUInt64()!=(uint)length)
                throw new IOException("Game snapshot unavailable");
        }
        private int Int(int a) { Read(a,word); return BitConverter.ToInt32(word,0); }
        public void Dispose()
        {
            if (handle!=null) { handle.Dispose(); handle=null; }
            if (process!=null) { process.Dispose(); process=null; }
            ram=0; lastCar=0;
        }
    }
}

