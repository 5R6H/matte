using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

// SpatialNative.dll (native\SpatialNative.cpp): the Direct3D 11 Spatial renderer. It reads the .spatial file, the cursor
// and the frame clock itself on its own threads; this is only the call surface. Events arrive on a renderer thread.
sealed class NativeSpatial : IDisposable {
 internal const int FirstScene=1,TransitionDone=2,LoadError=3,FadeOutDone=4;
 [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate void EventProc(int kind,int sequence);
 [StructLayout(LayoutKind.Sequential)] internal struct Stats{public long Presents,SceneDraws,Composites,Loads,Occlusions;public double LastLoadMs;public int SwapKind,Width,Height;public float MinZ;public double BenchScene,BenchComposite;public long Pauses;public int Paused;}
 const string Dll="SpatialNative.dll";
 [DllImport(Dll,CharSet=CharSet.Unicode)] static extern IntPtr SnCreate(IntPtr hwnd,int w,int h,uint rgb,EventProc cb,StringBuilder error,int errorLen);
 [DllImport(Dll)] static extern void SnSetStill(IntPtr r,IntPtr bgra,int w,int h,int stride);
 [DllImport(Dll)] static extern int SnStart(IntPtr r,int waitMs);
 [DllImport(Dll)] static extern void SnSetStillFade(IntPtr r,int ms);
 [DllImport(Dll,CharSet=CharSet.Unicode)] static extern void SnShow(IntPtr r,string path,uint rgb,int sequence);
 [DllImport(Dll)] static extern void SnSetColor(IntPtr r,uint rgb);
 [DllImport(Dll)] static extern void SnSetBackdrop(IntPtr r,IntPtr bgra,int w,int h,int stride);
 [DllImport(Dll)] static extern void SnFadeOut(IntPtr r,IntPtr bgra,int w,int h,int stride,int ms);
 [DllImport(Dll)] static extern void SnSetPointer(IntPtr r,int mode,float x,float y,int settle);
 [DllImport(Dll)] static extern void SnSetMotion(IntPtr r,int mode);
 [DllImport(Dll,CharSet=CharSet.Unicode)] static extern int SnCapture(IntPtr r,string png,float mix);
 [DllImport(Dll)] static extern void SnSetPaused(IntPtr r,int on);
 [DllImport(Dll)] static extern void SnStats(IntPtr r,out Stats s);
 [DllImport(Dll)] static extern void SnDestroy(IntPtr r);
 IntPtr r;readonly EventProc callback;int sequence;
 internal NativeSpatial(IntPtr hwnd,Size size,Color background,Action<int,int> onEvent){
  callback=(kind,seq)=>{try{onEvent(kind,seq);}catch{}};
  var error=new StringBuilder(512);r=SnCreate(hwnd,size.Width,size.Height,Rgb(background),callback,error,error.Capacity);
  if(r==IntPtr.Zero)throw new InvalidOperationException("Native Spatial renderer: "+error);
 }
 static uint Rgb(Color c){return (uint)(c.R<<16|c.G<<8|c.B);}
 delegate void PixelsProc(IntPtr bgra,int w,int h,int stride);
 static void WithPixels(Bitmap b,PixelsProc use){var d=b.LockBits(new Rectangle(0,0,b.Width,b.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);try{use(d.Scan0,d.Width,d.Height,d.Stride);}finally{b.UnlockBits(d);}}
 // The flat photo shown until the first scene has faded in over it (the window's size). Before Start.
 internal void SetStill(Bitmap still){WithPixels(still,(p,w,h,s)=>SnSetStill(r,p,w,h,s));}
 // how long the first scene takes over the still (before Start): 700 ms, or a slide's 1600 when the still is the photo before
 internal void SetStillFade(int ms){SnSetStillFade(r,ms);}
 // Starts drawing; true once the first frame (the still, or the background colour) has been presented.
 internal bool Start(int waitMs){return SnStart(r,waitMs)!=0;}
 internal int Show(string path,Color background){SnShow(r,path,Rgb(background),++sequence);return sequence;}
 internal void SetColor(Color c){SnSetColor(r,Rgb(c));}
 // The ambient background behind the scenes shown after this (the window's size); null = the colour.
 internal void SetBackdrop(Bitmap b){if(b==null)SnSetBackdrop(r,IntPtr.Zero,0,0,0);else WithPixels(b,(p,w,h,s)=>SnSetBackdrop(r,p,w,h,s));}
 internal void FadeOut(Bitmap still,int ms){WithPixels(still,(p,w,h,s)=>SnFadeOut(r,p,w,h,s,ms));}
 internal void SetPointer(bool fixedPointer,float x,float y,bool settle){SnSetPointer(r,fixedPointer?1:0,x,y,settle?1:0);}
 // How the camera moves: 0 follows the cursor, 1 the opposite way, 2 drifts on its own (SpatialMotion).
 internal void SetMotion(int mode){SnSetMotion(r,mode);}
 
 internal int Capture(string png,float mix){return SnCapture(r,png,mix);}
 // While the desktop cannot be seen: the pointer stops driving redraws (slide changes and fades still draw); false resumes.
 internal void SetPaused(bool on){SnSetPaused(r,on?1:0);}
 internal Stats GetStats(){Stats s;SnStats(r,out s);return s;}
 public void Dispose(){if(r!=IntPtr.Zero){var p=r;r=IntPtr.Zero;SnDestroy(p);}GC.KeepAlive(callback);}
}
