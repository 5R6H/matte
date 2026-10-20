using System;
using System.IO;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
[ComImport,Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IWallpaper {
 void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor,[MarshalAs(UnmanagedType.LPWStr)] string path);
 [return:MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitor);
 [return:MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint index);
 uint GetMonitorDevicePathCount();
 void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitor,out Rect rect);
 void SetBackgroundColor(uint color);
 uint GetBackgroundColor();
 void SetPosition(int position);
 int GetPosition();
 void SetSlideshow(IShellItemArray items);
 IShellItemArray GetSlideshow();
 void SetSlideshowOptions(uint options,uint milliseconds);
 void GetSlideshowOptions(out uint options,out uint milliseconds);
 void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)]string monitor,uint direction);
 uint GetStatus();
 void Enable([MarshalAs(UnmanagedType.Bool)]bool enabled);
}
[StructLayout(LayoutKind.Sequential)] struct Rect {public int Left,Top,Right,Bottom;}
partial class App:ApplicationContext {
 static string Dir=AppDomain.CurrentDomain.BaseDirectory;
IWallpaper wall; NotifyIcon tray; System.Windows.Forms.Timer poll,fade;
 uint from,target; Stopwatch animation=new Stopwatch(); string last=""; int failures=0; string currentSourcePath;
 Dictionary<string,uint> overrides=new Dictionary<string,uint>(StringComparer.OrdinalIgnoreCase);
 Dictionary<string,uint> cache=new Dictionary<string,uint>(StringComparer.OrdinalIgnoreCase);
 static uint Pack(Color c){return (uint)(c.R|(c.G<<8)|(c.B<<16));}
 static Color Unpack(uint c){return Color.FromArgb((int)(c&255),(int)((c>>8)&255),(int)((c>>16)&255));}
 internal static void Log(string s){try {string p=Path.Combine(Dir,"status.log");if(File.Exists(p)&&new FileInfo(p).Length>65536)File.WriteAllText(p,"");File.AppendAllText(p,DateTime.Now.ToString("s")+" "+s+Environment.NewLine);}catch{}}
 static string Key(string p){var f=new FileInfo(p);return p+"|"+f.Length+"|"+f.LastWriteTimeUtc.Ticks;}
 static IWallpaper CreateWallpaper(){return (IWallpaper)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")));}
 // IDesktopWallpaper lives inside explorer.exe; after Explorer restarts the old proxy fails with these RPC errors.
 static bool IsShellGone(Exception e){for(;e!=null;e=e.InnerException){int h=e.HResult;if(h==unchecked((int)0x800706BA)||h==unchecked((int)0x800706BE)||h==unchecked((int)0x80010108))return true;}return false;}
 void ReconnectShell(){try{if(wall!=null)Marshal.FinalReleaseComObject(wall);}catch{}wall=CreateWallpaper();last="";CancelBlend();Log("Reconnected to Explorer wallpaper service");}
 // Explorer restarted: the wallpaper service and the tray icon are new; the photo on show is shown again.
 void OnShellRestarted(){try{CloseSpatial();ReconnectShell();tray.Visible=false;tray.Visible=true;UseSlideshow(currentSourcePath);}catch(Exception e){Log("Shell restart recovery: "+e.Message);}}
 public App(){
 wall=CreateWallpaper();
 string op=Path.Combine(Dir,"overrides.tsv");if(File.Exists(op))foreach(string line in File.ReadAllLines(op)){string[] x=line.Split('\t');uint v;if(x.Length==2&&uint.TryParse(x[1],out v))overrides[x[0]]=v;}
 string cp=Path.Combine(Dir,PaletteFile);if(File.Exists(cp))foreach(string l in File.ReadAllLines(cp)){string[] x=l.Split('\t');uint v;if(x.Length==2&&uint.TryParse(x[1],out v))cache[x[0]]=v;}
 tray=new NotifyIcon();tray.Text="Matte";UpdateTray();
 tray.MouseClick+=delegate(object sender,MouseEventArgs e){TogglePanel(false);};tray.Visible=true;

 fade=new System.Windows.Forms.Timer();fade.Interval=32;fade.Tick+=delegate{try{
 double t=Math.Min(1,animation.Elapsed.TotalMilliseconds/1600);double u=t*t*(3-2*t);Color a=Unpack(from),b=Unpack(target);Color current=Color.FromArgb((int)Math.Round(a.R+(b.R-a.R)*u),(int)Math.Round(a.G+(b.G-a.G)*u),(int)Math.Round(a.B+(b.B-a.B)*u));wall.SetBackgroundColor(Pack(current));if(t>=1){fade.Stop();Log("Applied #"+b.R.ToString("X2")+b.G.ToString("X2")+b.B.ToString("X2"));}}catch(Exception e){fade.Stop();Log(e.Message);}};
 poll=new System.Windows.Forms.Timer();poll.Interval=2000;poll.Tick+=delegate{Check();UpdateTray();};InitSlideshow();poll.Start();Check();
 Application.ApplicationExit+=delegate{Restore();};
 }
 // Tray icon = a swatch of the colour around the photo on show (neutral grey before there is one).
 Icon trayIcon;int trayArgb;
 void UpdateTray(){
  if(tray==null)return;Color c=Swatch.Neutral;
  // the colour chosen for the photo on show (set the moment it is shown), not the desktop's colour of the moment: that
  // one is mid-fade during a change, and in Spatial mode it is only set a few seconds later (SetUnderneathLater)
  try{c=Unpack(shownColor!=0?shownColor:wall.GetBackgroundColor());}catch{return;}
  if(trayIcon!=null&&c.ToArgb()==trayArgb){UpdateDesktopIcon();return;}
  var old=trayIcon;trayIcon=Swatch.TrayIcon(c,SystemInformation.SmallIconSize.Width);trayArgb=c.ToArgb();tray.Icon=trayIcon;if(old!=null)old.Dispose();
  UpdateDesktopIcon();
 }
 // Desktop shortcuts to this exe get the same live swatch. A shortcut icon is read from a file, so the app writes an
 // .ico and points the shortcut at it. Explorer caches icons by path, so each colour gets its own file name
 // (matte-RRGGBB.ico): a path only ever holds one colour, and a cached copy of it can never be the wrong colour.
 // (The prefix changes with the icon's shape: swatch- files were the earlier square shape.)
 int desktopArgb;bool desktopBusy;
 [System.Runtime.InteropServices.DllImport("shell32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern void SHChangeNotify(int eventId,uint flags,string item1,IntPtr item2);
 void UpdateDesktopIcon(){
  if(desktopBusy||fade==null)return;           // fade is null during startup; the colour is always a photo's final one (UpdateTray)
  Color c=Color.FromArgb(trayArgb);if(c.ToArgb()==desktopArgb)return;
  desktopBusy=true;desktopArgb=c.ToArgb();string exe=Application.ExecutablePath,folder=Path.Combine(Dir,"desktop-icon"),icon=Path.Combine(folder,"matte-"+Theme.Hex(c).TrimStart('#')+".ico");
  Task.Run(()=>{
   try{
    Directory.CreateDirectory(folder);
    if(!File.Exists(icon))Swatch.WriteIco(c,icon,16,20,24,32,40,48,64,96,128,256);else File.SetLastWriteTime(icon,DateTime.Now);
    // Keep the 12 most recent colours; older files are not referenced by any shortcut any more.
    foreach(var square in new DirectoryInfo(folder).GetFiles("swatch-*.ico"))try{square.Delete();}catch{}
    var old=new DirectoryInfo(folder).GetFiles("matte-*.ico");Array.Sort(old,(x,y)=>y.LastWriteTime.CompareTo(x.LastWriteTime));
    for(int i=12;i<old.Length;i++)try{old[i].Delete();}catch{}
    dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
    try{
     foreach(string desk in new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)})
      foreach(string lnk in Directory.Exists(desk)?Directory.GetFiles(desk,"*.lnk"):new string[0]){
       try{dynamic link=shell.CreateShortcut(lnk);string target=link.TargetPath;
        if(string.Equals(target,exe,StringComparison.OrdinalIgnoreCase)){link.IconLocation=icon+",0";link.Save();SHChangeNotify(0x2000,0x0005,lnk,IntPtr.Zero);}
        Marshal.FinalReleaseComObject(link);}catch{}
      }
    }finally{Marshal.FinalReleaseComObject(shell);}
   }catch(Exception e){Log("Desktop icon: "+e.Message);desktopArgb=0;}
   finally{desktopBusy=false;}
  });
 }
 void Check(){try{
 if(File.Exists(Path.Combine(Dir,"stop.request"))){File.Delete(Path.Combine(Dir,"stop.request"));ExitThread();return;}
 if((DateTime.Now-lastSweep).TotalHours>=6)RunSpatialSweep();   // Spatial pictures kept tidy (SpatialStore.cs)
 // While a Spatial scene covers the desktop, Windows' static wallpaper is not what is shown; StartSpatial sets the colour itself.
 CheckRequests();if(BlendActive || spatialDesktop!=null || wall.GetPosition()!=3)return;
 uint count=wall.GetMonitorDevicePathCount();if(count==0)return;
 string monitor=wall.GetMonitorDevicePathAt(0);for(uint n=0;n<count;n++){string m=wall.GetMonitorDevicePathAt(n);Rect r;wall.GetMonitorRECT(m,out r);if(r.Left==0&&r.Top==0){monitor=m;break;}}
 string path=wall.GetWallpaper(monitor);if(string.IsNullOrEmpty(path)||!File.Exists(path))return;
 string key=Key(path);if(key==last)return;uint color=ColorFor(path);
 last=key;BeginFade(wall.GetBackgroundColor(),color);failures=0;
 }catch(Exception e){if(IsShellGone(e)){try{ReconnectShell();}catch(Exception r){Log("Reconnect: "+r.Message);}return;}if(failures++<3)Log("Check: "+e.Message);}}
 void BeginFade(uint start,uint color){from=start;target=color;animation.Restart();fade.Start();}
 // palette-v2.tsv caches AutoColor; the old palette.tsv held the classic colour only and is no longer read.
 const string PaletteFile="palette-v2.tsv";
 uint ColorFor(string path){string key=Key(path);uint color;
 if(!overrides.TryGetValue(path,out color) && !cache.TryGetValue(key,out color)){string source=path;try{color=AutoColor(source);}catch{source=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Microsoft\\Windows\\Themes\\TranscodedWallpaper");color=AutoColor(source);}cache[key]=color;File.AppendAllText(Path.Combine(Dir,PaletteFile),key+"\t"+color+Environment.NewLine);}
 return color;}
 static double ScreenAspect{get{var b=Screen.PrimaryScreen.Bounds;return (double)b.Width/b.Height;}}
 // The automatic colour: of the classic colour and the main edge colours, the one that meets the picture's edge most smoothly.
 static uint AutoColor(string p){uint classic=Analyze(p);return Pack(ColorModel.Load(p,ScreenAspect).Auto(Unpack(classic)).Color);}
 // Manual colour for one picture (from the colour picker); null removes it so the automatic colour applies again.
 void SaveOverride(string path,uint? color){
  if(color.HasValue)overrides[path]=color.Value;else overrides.Remove(path);
  try{File.WriteAllLines(Path.Combine(Dir,"overrides.tsv"),overrides.Select(kv=>kv.Key+"\t"+kv.Value));}catch(Exception e){Log("Overrides: "+e.Message);}
 }
 static Color FromHsl(double h,double s,double l){double c=(1-Math.Abs(2*l-1))*s,x=c*(1-Math.Abs((h/60)%2-1)),m=l-c/2;double r=0,g=0,b=0;if(h<60){r=c;g=x;}else if(h<120){r=x;g=c;}else if(h<180){g=c;b=x;}else if(h<240){g=x;b=c;}else if(h<300){r=x;b=c;}else{r=c;b=x;}return Color.FromArgb((int)Math.Round((r+m)*255),(int)Math.Round((g+m)*255),(int)Math.Round((b+m)*255));}
 static uint Analyze(string p){using(var im=Image.FromFile(p))using(var b=new Bitmap(96,96)){
 bool side=(double)im.Width/im.Height < (double)Screen.PrimaryScreen.Bounds.Width/Screen.PrimaryScreen.Bounds.Height;
 using(var g=Graphics.FromImage(b))g.DrawImage(im,0,0,96,96);
 var pixels=new List<double[]>();double[] hist=new double[1331];double[,] sums=new double[1331,3];
 for(int y=2;y<94;y++)for(int x=2;x<94;x++){bool le=x<14,ri=x>=82,to=y<14,bo=y>=82;if(!(le||ri||to||bo))continue;bool primary=side?(le||ri):(to||bo);int seg=primary?(side?(y*6/96+(le?0:6)):(x*6/96+(to?0:6))):-1;double w=primary?3:.5;Color c=b.GetPixel(x,y);int k=(c.R/24)*121+(c.G/24)*11+c.B/24;hist[k]+=w;sums[k,0]+=c.R*w;sums[k,1]+=c.G*w;sums[k,2]+=c.B*w;pixels.Add(new double[]{c.R,c.G,c.B,w,seg});}
 var order=new List<int>();for(int i=0;i<1331;i++)if(hist[i]>0)order.Add(i);order.Sort(delegate(int a,int z){int v=hist[z].CompareTo(hist[a]);return v!=0?v:a.CompareTo(z);});double bestScore=-1;double[] best=null;
 foreach(int k in order.GetRange(0,Math.Min(40,order.Count))){double[] c={sums[k,0]/hist[k],sums[k,1]/hist[k],sums[k,2]/hist[k]};double[] coverage=new double[12];double mass=0;
 for(int iter=0;iter<2;iter++){double r=0,g=0,bl=0;mass=0;Array.Clear(coverage,0,12);foreach(var v in pixels){double dr=v[0]-c[0],dg=v[1]-c[1],db=v[2]-c[2],dist=dr*dr+dg*dg+db*db+2*(dr-db)*(dr-db),dl=.2126*dr+.7152*dg+.0722*db;if(dist>=42*42||Math.Abs(dl)>=34)continue;double w=v[3];mass+=w;r+=v[0]*w;g+=v[1]*w;bl+=v[2]*w;if(v[4]>=0)coverage[(int)v[4]]+=w;}if(mass>0)c=new double[]{r/mass,g/mass,bl/mass};}
 int covered=0;double left=0,right=0;for(int i=0;i<12;i++){if(coverage[i]>30)covered++;if(i<6)left+=coverage[i];else right+=coverage[i];}
 Color cc=Color.FromArgb((int)Math.Round(c[0]),(int)Math.Round(c[1]),(int)Math.Round(c[2]));double h=cc.GetHue(),l=cc.GetBrightness(),ch=Math.Max(c[0],Math.Max(c[1],c[2]))-Math.Min(c[0],Math.Min(c[1],c[2]));double warm=(h>10&&h<48&&ch>25)?.8:1;
 double score=mass*(.4+.6*covered/12)*(1+.25*Math.Min(1,Math.Min(left,right)/200))*(1+.15*Math.Min(ch/70,1))*warm*Math.Min(1,Math.Max(.15,l/.38))*Math.Min(1,Math.Max(.25,(1-l)/.16));if(score>bestScore){bestScore=score;best=c;}}
 double lum=.2126*best[0]+.7152*best[1]+.0722*best[2];return Pack(Color.FromArgb((int)Math.Round(lum+(best[0]-lum)*.9),(int)Math.Round(lum+(best[1]-lum)*.9),(int)Math.Round(lum+(best[2]-lum)*.9)));}}

 // Quitting leaves the photo on show as the wallpaper (flat, on its colour); only Matte's own layers go.
 void Restore(){StopGeneration();CloseSpatial();CancelBlend();try{fade.Stop();}catch{}if(tray!=null){tray.Visible=false;tray.Dispose();}}
 protected override void ExitThreadCore(){poll.Stop();if(ui!=null)ui.Dispose();Restore();base.ExitThreadCore();}
 // --analyze <image> <out.txt>: the automatic colour of one image; --stop: asks the running Matte to quit.
 [STAThread] static void Main(string[] args){
  if(args.Length==3&&args[0]=="--analyze"){File.WriteAllText(args[2],Analyze(args[1]).ToString());return;}
  if(args.Length>0&&args[0]=="--stop"){File.WriteAllText(Path.Combine(Dir,"stop.request"),"stop");return;}
  bool created;using(var mutex=new Mutex(true,"Local\\Matte",out created)){
   if(!created){try{File.WriteAllText(Path.Combine(Dir,"show.request"),"panel");}catch{}return;}   // already running: opening it again shows its panel
   try{Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);Application.ThreadException+=delegate(object s,ThreadExceptionEventArgs e){Log("Unhandled: "+e.Exception);};Application.EnableVisualStyles();Application.Run(new App());}catch(Exception e){Log(e.ToString());}}
 }
}



