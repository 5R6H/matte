using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

// Whether the window in front is a real fullscreen app or game (SpatialPower.cs pauses Spatial for it).
// QUNS_BUSY / RUNNING_D3D_FULL_SCREEN alone are not enough: on this machine Explorer kept reporting BUSY for minutes with
// an ordinary window in front (and only while Matte ran, so possibly over its own desktop layer). So the window in front
// must be exactly its monitor's bounds (Matte is per-monitor DPI aware: both in physical pixels), not maximized, without
// a title bar, and not the desktop, Matte or the Claude app; and there must be one monitor only (with more, the rest of
// the desktop is still in view). A maximized window never counts, even with an auto-hidden taskbar.
static class FullscreenCheck {
 [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder s,int n);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h,out Rect r);
 [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
 [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int index);
 [StructLayout(LayoutKind.Sequential)] struct Rect{public int Left,Top,Right,Bottom;}
 internal static uint OwnPid=(uint)Process.GetCurrentProcess().Id;   // (tests: 0, so their own windows count)
 internal static IntPtr Foreground(){return GetForegroundWindow();}
 // null: fullscreen; otherwise why not
 internal static string WhyNot(IntPtr h){
  if(h==IntPtr.Zero)return "no foreground window";
  uint pid;GetWindowThreadProcessId(h,out pid);var cls=new StringBuilder(64);GetClassName(h,cls,64);
  string proc="";try{using(var p=Process.GetProcessById((int)pid))proc=p.ProcessName;}catch{}
  if(pid==OwnPid)return "Matte";
  if(cls.ToString()=="Progman"||cls.ToString()=="WorkerW")return "desktop";
  if(string.Equals(proc,"claude",StringComparison.OrdinalIgnoreCase))return "Claude app";
  if(Screen.AllScreens.Length>1)return "several monitors";
  if(IsZoomed(h))return proc+" maximized";
  if((GetWindowLong(h,-16)&0x00C00000)==0x00C00000)return proc+" has a title bar";   // WS_CAPTION
  Rect r;if(!GetWindowRect(h,out r))return "no rect";
  var m=Screen.FromHandle(h).Bounds;
  if(r.Left!=m.Left||r.Top!=m.Top||r.Right!=m.Right||r.Bottom!=m.Bottom)return proc+" not exactly fullscreen";
  return null;
 }
}
