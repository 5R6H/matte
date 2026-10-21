using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Diagnostics;

sealed class DesktopBlend : Form {
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string c,string n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr p,IntPtr after,string c,string n);
 [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p,IntPtr l);
 delegate bool EnumProc(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
 [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h,uint key,byte alpha,uint flags);
 [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int hgt,uint flags);
 [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from,IntPtr to,ref Point p,uint n);
 [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h,uint m,IntPtr w,IntPtr l,uint f,uint t,out IntPtr r);
 internal Bitmap Before,After;internal float Progress;IntPtr host,icons;bool layeredDesktop;internal int Paints;
 protected override bool ShowWithoutActivation{get{return true;}}
 protected override CreateParams CreateParams{get{var p=base.CreateParams;p.Style=(p.Style&~unchecked((int)0x80000000))|0x40000000;p.ExStyle|=0x08000080;if(layeredDesktop)p.ExStyle|=0x80000;p.Parent=host;return p;}}
 protected override void WndProc(ref Message m){if(m.Msg==0x84){m.Result=new IntPtr(-1);return;}if(m.Msg==0x21){m.Result=new IntPtr(3);return;}base.WndProc(ref m);}
 internal static IntPtr DesktopHost(out IntPtr icons,out bool layered,bool initialize=true){
  icons=IntPtr.Zero;layered=false;IntPtr prog=FindWindow("Progman",null),result;if(prog==IntPtr.Zero)return IntPtr.Zero;IntPtr found=IntPtr.Zero;
  IntPtr child=FindWindowEx(prog,IntPtr.Zero,"WorkerW",null);if(child!=IntPtr.Zero&&FindWindowEx(child,IntPtr.Zero,"SHELLDLL_DefView",null)==IntPtr.Zero){icons=FindWindowEx(prog,IntPtr.Zero,"SHELLDLL_DefView",null);if(icons!=IntPtr.Zero){layered=true;return prog;}}
  EnumWindows(delegate(IntPtr h,IntPtr p){if(FindWindowEx(h,IntPtr.Zero,"SHELLDLL_DefView",null)!=IntPtr.Zero){IntPtr w=FindWindowEx(IntPtr.Zero,h,"WorkerW",null);if(w!=IntPtr.Zero){found=w;return false;}}return true;},IntPtr.Zero);
  if(found==IntPtr.Zero&&initialize){SendMessageTimeout(prog,0x052c,new IntPtr(0xd),IntPtr.Zero,2,500,out result);SendMessageTimeout(prog,0x052c,new IntPtr(0xd),new IntPtr(1),2,500,out result);return DesktopHost(out icons,out layered,false);}return found;
 }
 internal static Bitmap Compose(string path,Color color,Rectangle desktop,Screen[] screens){var result=new Bitmap(desktop.Width,desktop.Height,PixelFormat.Format32bppPArgb);using(var g=Graphics.FromImage(result)){g.Clear(color);g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;if(!string.IsNullOrEmpty(path))using(var picture=App.LoadUpright(path))foreach(var s in screens){Rectangle b=s.Bounds;double scale=Math.Min((double)b.Width/picture.Width,(double)b.Height/picture.Height);int w=(int)Math.Round(picture.Width*scale),h=(int)Math.Round(picture.Height*scale);g.DrawImage(picture,new Rectangle(b.X-desktop.X+(b.Width-w)/2,b.Y-desktop.Y+(b.Height-h)/2,w,h));}}return result;}
 internal DesktopBlend(Bitmap before,Bitmap after,Rectangle bounds){host=DesktopHost(out icons,out layeredDesktop);if(host==IntPtr.Zero)throw new InvalidOperationException("Desktop animation host unavailable");Before=before;After=after;FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;AutoScaleMode=AutoScaleMode.None;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.Opaque,true);ClientSize=bounds.Size;IntPtr h=Handle;if(layeredDesktop&&!SetLayeredWindowAttributes(h,0,255,2))throw new InvalidOperationException("Layered desktop setup failed");SetParent(h,host);if(GetParent(h)!=host)throw new InvalidOperationException("Desktop animation attachment failed");var origin=bounds.Location;MapWindowPoints(IntPtr.Zero,host,ref origin,1);SetWindowPos(h,icons,origin.X,origin.Y,bounds.Width,bounds.Height,0x10);Show();SetWindowPos(h,icons,0,0,0,0,0x10|0x01|0x02);Refresh();}
 internal bool HostAlive{get{return IsWindow(host)&&GetParent(Handle)==host;}}
 // Whole-window opacity on the layered desktop (Windows 11); without layering the window simply stays opaque.
 internal void SetAlpha(byte a){if(layeredDesktop)SetLayeredWindowAttributes(Handle,0,a,2);}
 protected override void OnPaintBackground(PaintEventArgs e){}
 protected override void OnPaint(PaintEventArgs e){e.Graphics.DrawImageUnscaled(Before,0,0);using(var a=new ImageAttributes()){var m=new ColorMatrix();m.Matrix33=Math.Max(0,Math.Min(1,Progress));a.SetColorMatrix(m);e.Graphics.DrawImage(After,new Rectangle(0,0,After.Width,After.Height),0,0,After.Width,After.Height,GraphicsUnit.Pixel,a);}Paints++;}
 protected override void Dispose(bool disposing){if(disposing){if(Before!=null){Before.Dispose();Before=null;}if(After!=null){After.Dispose();After=null;}}base.Dispose(disposing);}
}
partial class App {
 DesktopBlend blend;System.Windows.Forms.Timer blendTimer;Stopwatch blendClock=new Stopwatch();string blendPath;uint blendColor;bool blendCommitted;
 bool BlendActive{get{return blend!=null;}}
 void CancelBlend(){if(blendTimer!=null){blendTimer.Stop();blendTimer.Dispose();blendTimer=null;}if(blend!=null){blend.Close();blend.Dispose();blend=null;}blendPath=null;}
 void FinishBlend(){if(blend==null)return;string p=blendPath;uint c=blendColor;wall.SetBackgroundColor(c);wall.SetWallpaper(null,p);last=Key(p);CancelBlend();}
 // Monitors on screen now: Windows also lists displays once connected and now gone (they have no rectangle), which
 // must not stop the cross-fade.
 int ActiveMonitors(){int n=0;uint c=wall.GetMonitorDevicePathCount();for(uint i=0;i<c;i++){try{Rect r;wall.GetMonitorRECT(wall.GetMonitorDevicePathAt(i),out r);if(r.Right>r.Left)n++;}catch{}}return n;}
 bool StartBlend(string path,uint color){if(wall.GetPosition()!=3||ActiveMonitors()!=1)return false;if(BlendActive)FinishBlend();Bitmap before=null,after=null;try{Rectangle bounds=SystemInformation.VirtualScreen;string old=wall.GetWallpaper(wall.GetMonitorDevicePathAt(0));before=DesktopBlend.Compose(old,Unpack(wall.GetBackgroundColor()),bounds,Screen.AllScreens);after=DesktopBlend.Compose(path,Unpack(color),bounds,Screen.AllScreens);blend=new DesktopBlend(before,after,bounds);before=after=null;blendPath=path;blendColor=color;blendCommitted=false;blendClock.Restart();blendTimer=new System.Windows.Forms.Timer();blendTimer.Interval=16;blendTimer.Tick+=delegate{try{if(!blend.HostAlive){FinishBlend();return;}double t=Math.Min(1,blendClock.Elapsed.TotalMilliseconds/1600.0);if(!blendCommitted){blend.Progress=(float)(t*t*(3-2*t));blend.Invalidate();}if(t>=1&&!blendCommitted){blend.Progress=1;blend.Refresh();wall.SetBackgroundColor(blendColor);wall.SetWallpaper(null,blendPath);last=Key(blendPath);blendCommitted=true;}if(blendCommitted&&blendClock.ElapsedMilliseconds>=2800){CancelBlend();}}catch(Exception e){Log("Synchronized fade: "+e.Message);CancelBlend();last="";}};blendTimer.Start();return true;}catch(Exception e){if(before!=null)before.Dispose();if(after!=null)after.Dispose();CancelBlend();Log("Synchronized fade fallback: "+e.Message);return false;}}
}
