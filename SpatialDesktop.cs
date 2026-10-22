using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;

// The Spatial layer on the desktop, parented under Progman/WorkerW like DesktopBlend. SpatialNative.dll draws it with
// Direct3D 11: it reads the .spatial file, the cursor and the frame clock itself, and does its own fades (from and to a
// flat still of the photo). If it cannot start, the photo simply shows flat.
sealed class SpatialDesktopHost : Form {
 readonly NativeSpatial renderer;
 readonly SpatialLeases leases=new SpatialLeases();
 readonly Dictionary<int,SpatialLoad> loads=new Dictionary<int,SpatialLoad>();
 readonly Action<SpatialLoad> attached;readonly Action<SpatialLoad,int,NativeSpatial.FailureReason,string> failed;
 IntPtr host,icons; bool layered; bool ready;
 [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h,uint key,byte alpha,uint flags);
 [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int hgt,uint flags);
 [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from,IntPtr to,ref Point p,uint n);
 [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int n);
 [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h,int n,int v);
 protected override bool ShowWithoutActivation{get{return true;}}
 protected override CreateParams CreateParams{get{var p=base.CreateParams;p.Style=(p.Style&~unchecked((int)0x80000000));p.ExStyle|=0x08000080;if(layered)p.ExStyle|=0x80000;return p;}}
 protected override void WndProc(ref Message m){if(m.Msg==0x84){m.Result=new IntPtr(-1);return;}if(m.Msg==0x21){m.Result=new IntPtr(3);return;}base.WndProc(ref m);}
 internal bool Ready{get{return ready;}}
 // still: what the desktop shows now (DesktopBlend.Compose, the window's size): shown first, the first scene fades in
 // over it in stillFadeMs; backdrop: the ambient / extended background behind the scene, or null for the colour.
 internal SpatialDesktopHost(SpatialVerification verified,Rectangle bounds,Color color,Bitmap still,Bitmap backdrop,Action<SpatialLoad> attached,Action<SpatialLoad,int,NativeSpatial.FailureReason,string> failed,int stillFadeMs=700){
  this.attached=attached;this.failed=failed;
  host=DesktopBlend.DesktopHost(out icons,out layered);if(host==IntPtr.Zero)throw new InvalidOperationException("Desktop host unavailable");
  FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;AutoScaleMode=AutoScaleMode.None;ClientSize=bounds.Size;BackColor=Color.Black;
  SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.Opaque,true);   // the swap chain is the only content
  IntPtr h=Handle;
  // Opaque: the flip-model swap chain does not take the window's alpha; fades are drawn instead.
  if(layered&&!SetLayeredWindowAttributes(h,0,255,2))throw new InvalidOperationException("Layered desktop setup failed");int style=GetWindowLong(h,-16);SetWindowLong(h,-16,(style|0x40000000)&~unchecked((int)0x80000000));SetParent(h,host);
  var origin=bounds.Location;MapWindowPoints(IntPtr.Zero,host,ref origin,1);SetWindowPos(h,icons,origin.X,origin.Y,bounds.Width,bounds.Height,0x10);
  try{
   renderer=new NativeSpatial(h,bounds.Size,color,OnNativeEvent);renderer.SetStill(still);renderer.SetStillFade(stillFadeMs);
   // shown only once the still is on the swap chain, so the window never appears empty over the wallpaper
   if(!renderer.Start(1500))App.Log("Spatial native: first frame late");
  }catch{if(renderer!=null)renderer.Dispose();DestroyHandle();throw;}
  try{Show();SetWindowPos(h,icons,0,0,0,0,0x10|0x01|0x02);ready=true;Navigate(verified,color,backdrop);}catch{Dispose();throw;}
 }
 internal void Navigate(SpatialVerification verified,Color color,Bitmap backdrop){var lease=verified.TakeSceneLease();if(lease==null||verified.Pair==null){if(lease!=null)lease.Dispose();throw new InvalidOperationException("Spatial requires a verified lease");}
  try{renderer.SetBackdrop(backdrop);int sequence=renderer.Show(verified.Pair.Artifact,color);leases.Add(sequence,lease);var load=new SpatialLoad(this,sequence,verified);loads.Add(sequence,load);attached(load);}catch{lease.Dispose();throw;}}
 internal void SetColor(Color color){renderer.SetColor(color);}
 // How the camera moves (App.MotionCode): 0 follow the cursor, 1 the opposite way, 2 circle, 3 handheld.
 internal void SetMotion(int mode){renderer.SetMotion(mode);}
 // Fades to the still (what is left underneath once the layer closes), then calls done.
 Action fadeDone;Timer fadeTimer;
 internal void FadeOut(Bitmap still,int ms,Action done){
  fadeDone=done;renderer.FadeOut(still,ms);
  fadeTimer=new Timer{Interval=ms+2000};fadeTimer.Tick+=delegate{FadeFinished();};fadeTimer.Start();   // in case the renderer never reports back
 }
 void FadeFinished(){if(fadeTimer!=null){fadeTimer.Stop();fadeTimer.Dispose();fadeTimer=null;}var d=fadeDone;fadeDone=null;if(d!=null)d();}
 // Renderer thread: handed to the UI thread.
 void OnNativeEvent(int kind,int sequence){
  // SnLastError is renderer-owned history: copy it before dispatch, including for a superseded host.
  var reason=NativeSpatial.FailureReason.None;string detail=null;bool error=kind==NativeSpatial.LoadError||kind==NativeSpatial.LoadFailed;
  if(error){var text=new StringBuilder(512);renderer.LastError(sequence,out reason,text);detail=text.ToString();}
  if(IsDisposed||!IsHandleCreated)return;
  try{BeginInvoke((Action)delegate{
   if(IsDisposed)return;leases.NativeEvent(kind,sequence);
   if(kind==NativeSpatial.TransitionDone)foreach(int n in loads.Keys.Where(n=>n<sequence).ToArray())loads.Remove(n);
   if(kind==NativeSpatial.FadeOutDone)FadeFinished();
   else if(error){SpatialLoad load;if(loads.TryGetValue(sequence,out load)){loads.Remove(sequence);failed(load,kind,reason,detail);}}});}catch{}
 }
 internal NativeSpatial.Stats NativeStats{get{return renderer.GetStats();}}
 // The desktop cannot be seen (SpatialPower.cs): the pointer stops driving redraws (slide changes and fades still draw).
 internal void SetPaused(bool on){renderer.SetPaused(on);}
 
 protected override void OnPaintBackground(PaintEventArgs e){}
 protected override void OnPaint(PaintEventArgs e){}
 // the renderer stops before the window goes (hidden first by DestroyWindow, so nothing lingers on screen)
 protected override void OnHandleDestroyed(EventArgs e){try{if(renderer!=null)renderer.Dispose();}finally{leases.Dispose();}base.OnHandleDestroyed(e);}
 protected override void Dispose(bool disposing){if(disposing){if(fadeTimer!=null){fadeTimer.Stop();fadeTimer.Dispose();fadeTimer=null;}try{if(renderer!=null)renderer.Dispose();}finally{leases.Dispose();}}base.Dispose(disposing);}
}
partial class App {
 SpatialDesktopHost spatialDesktop;
 // The same photo is also the Windows wallpaper underneath Spatial: what the panel's blur sees, and what is left when
 // Spatial closes. While Spatial is showing, it is set a few seconds after a slide change, not during it: Explorer runs
 // its own wallpaper animation behind the 3D layer, and on the same frames it halves the cross-fade's frame rate.
 IDisposable wallpaperDisplay;
 void PinWallpaper(string path){var lease=DisplayCache.Pin(path);if(wallpaperDisplay!=null)wallpaperDisplay.Dispose();wallpaperDisplay=lease;}
 string underPath;uint underColor;System.Windows.Forms.Timer underTimer;
 void SetUnderneath(string path,uint color){
  if(underTimer!=null){underTimer.Stop();underTimer.Dispose();underTimer=null;}underPath=null;
  wall.SetBackgroundColor(color);if(!string.Equals(wall.GetWallpaper(wall.GetMonitorDevicePathAt(0)),path,StringComparison.OrdinalIgnoreCase))wall.SetWallpaper(null,path);PinWallpaper(path);
 }
 void SetUnderneathLater(string path,uint color){
  underPath=path;underColor=color;
  if(underTimer==null){underTimer=new System.Windows.Forms.Timer{Interval=3500};underTimer.Tick+=delegate{FlushUnderneath();};}
  underTimer.Stop();underTimer.Start();
 }
 // Applied now (leaving Spatial for the flat photo).
 void FlushUnderneath(){if(underPath==null)return;if(spatialDesktop==null){CancelUnderneath();return;}try{SetUnderneath(underPath,underColor);}catch(Exception e){Log("Wallpaper underneath: "+e.Message);}}
 void CancelUnderneath(){if(underTimer!=null)underTimer.Stop();underPath=null;}
 // path: what the desktop shows flat (the photo, or its composite); backdropFile: the background drawn behind the 3D
 // scene (AmbientWallpaper.cs / ExtendWallpaper.cs), or null for the colour.
 bool StartSpatial(string path,SpatialVerification verified,uint color,string backdropFile){Bitmap backdrop=null;try{CancelBlend();
  if(backdropFile!=null)try{backdrop=DesktopBlend.Compose(backdropFile,Unpack(color),SystemInformation.VirtualScreen,Screen.AllScreens);}catch(Exception e){Log("Spatial backdrop: "+e.Message);}
  if(spatialDesktop!=null){SetUnderneathLater(path,color);spatialDesktop.Navigate(verified,Unpack(color),backdrop);}
  else{
   // the layer starts as what the desktop shows now and fades into the first scene: from the same photo (Spatial
   // switched on) quickly, from the photo before (a flat photo, then this 3D one) as a slide's cross-fade. The new
   // photo goes underneath only once the layer covers the desktop, so the change is never a cut.
   string before=wall.GetWallpaper(wall.GetMonitorDevicePathAt(0));uint beforeColor=wall.GetBackgroundColor();
   int inMs=string.Equals(before,path,StringComparison.OrdinalIgnoreCase)?700:1600;
   using(var still=DesktopBlend.Compose(before,Unpack(beforeColor),SystemInformation.VirtualScreen,Screen.AllScreens)){
    spatialDesktop=new SpatialDesktopHost(verified,SystemInformation.VirtualScreen,Unpack(color),still,backdrop,l=>spatialLoad=l,SpatialLoadFailed,inMs);}
   spatialDesktop.SetMotion(MotionCode);if(spatialPaused)spatialDesktop.SetPaused(true);
   SetUnderneath(path,color);
  }
  blendPath=path;last=Key(path);return true;}
  catch(Exception e){Log("Spatial wallpaper failed: "+e.Message);verified.Dispose();FlatSpatialFailure(verified.Image);RejectSpatial(verified.Pair,verified.ArtifactDigest,false,verified.Image.Source,e.Message);return false;}
  finally{if(backdrop!=null)backdrop.Dispose();}}   // the renderer keeps its own copy
 // Layers still fading out after an animated close. A close without animation (e.g. quitting) takes these too,
 // so no scene outlives it.
 readonly List<SpatialDesktopHost> closingSpatial=new List<SpatialDesktopHost>();
 void CloseSpatial(){CancelSlideRequests();CloseSpatial(false);}
 // animate: the renderer fades to the photo that is the Windows wallpaper underneath, then the layer goes.
 void CloseSpatial(bool animate,int duration=700){CancelUnderneath();spatialLoad=null;
  if(!animate)foreach(var c in closingSpatial.ToArray()){closingSpatial.Remove(c);try{c.Close();c.Dispose();}catch(Exception e){Log("Spatial close: "+e.Message);}}
  if(spatialDesktop==null)return;var d=spatialDesktop;spatialDesktop=null;
  Action close=()=>{if(closingSpatial.Remove(d)||!animate){d.Close();d.Dispose();}};
  if(!animate){close();return;}
  closingSpatial.Add(d);
  try{using(var still=DesktopBlend.Compose(wall.GetWallpaper(wall.GetMonitorDevicePathAt(0)),Unpack(wall.GetBackgroundColor()),SystemInformation.VirtualScreen,Screen.AllScreens))d.FadeOut(still,duration,close);}
  catch(Exception e){Log("Spatial close: "+e.Message);close();}}
}
