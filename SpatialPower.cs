using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Spatial backs off when it should not spend GPU or battery, and comes back by itself:
//  the desktop cannot be seen (a fullscreen app or game in front, presentation mode, the screen off, the session locked):
//   the renderer is paused (SpatialDesktopHost.SetPaused): the pointer no longer drives redraws, which is where the cost
//   is. Slide changes and fades still draw (a few seconds per slide), so the desktop never lags behind the panel, even
//   if a pause is wrong. Resuming carries on from the last frame;
//  energy saver / battery saver (and, with spatial-battery.txt = "flat", any time on battery): the layer closes with its
//   usual fade and the flat photo is shown (the same one, which is the Windows wallpaper underneath anyway); once the
//   saver has been off for a few seconds, Spatial fades back in over it. The desktop is still on screen here, so a frozen
//   3D frame would be wrong (no parallax, no slide changes), and closing the layer also frees its GPU memory.
// The slideshow timer keeps running throughout.

// Window for the power and session notifications (WM_POWERBROADCAST / WM_WTSSESSION_CHANGE). Invisible, top-level:
// power setting notifications are only sent to top-level windows.
sealed class PowerWatch : NativeWindow {
 [DllImport("user32.dll",SetLastError=true)] static extern IntPtr RegisterPowerSettingNotification(IntPtr h,ref Guid setting,int flags);
 [DllImport("wtsapi32.dll")] static extern bool WTSRegisterSessionNotification(IntPtr h,int flags);
 [StructLayout(LayoutKind.Sequential)] struct PowerSetting{public Guid Setting;public int Length;public int Data;}
 static readonly Guid SessionDisplay=new Guid("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5"),   // GUID_SESSION_DISPLAY_STATUS: 0 off, 1 on, 2 dimmed
  EnergySaver=new Guid("550E8400-E29B-41D4-A716-446655440000"),                           // GUID_ENERGY_SAVER_STATUS: 0 off, 1 standard, 2 high savings
  BatterySaver=new Guid("E00958C0-C213-4ACE-AC77-FECCED2EEEA5");                          // GUID_POWER_SAVING_STATUS: 0 off, 1 on
 readonly Action changed;
 internal bool DisplayOff,Locked,BatterySaverOn;internal int EnergySaverLevel;
 internal PowerWatch(Action changed){
  this.changed=changed;CreateHandle(new CreateParams{Caption="Matte power",ExStyle=0x80});
  foreach(var g in new[]{SessionDisplay,EnergySaver,BatterySaver}){var s=g;if(RegisterPowerSettingNotification(Handle,ref s,0)==IntPtr.Zero)App.Log("Power notification "+g+": "+Marshal.GetLastWin32Error());}
  if(!WTSRegisterSessionNotification(Handle,0))App.Log("Session notifications unavailable");
 }
 protected override void WndProc(ref Message m){
  try{
   if(m.Msg==0x218&&m.WParam.ToInt32()==0x8013){   // PBT_POWERSETTINGCHANGE (also sent once on registering, with the current value)
    var s=(PowerSetting)Marshal.PtrToStructure(m.LParam,typeof(PowerSetting));
    if(s.Setting==SessionDisplay)DisplayOff=s.Data==0;else if(s.Setting==EnergySaver)EnergySaverLevel=s.Data;else if(s.Setting==BatterySaver)BatterySaverOn=s.Data!=0;
    changed();
   }
   else if(m.Msg==0x218&&m.WParam.ToInt32()==0x0A)changed();   // PBT_APMPOWERSTATUSCHANGE: AC / battery
   else if(m.Msg==0x2B1){int e=m.WParam.ToInt32();   // WTS_SESSION_LOCK 7 / UNLOCK 8, CONSOLE_DISCONNECT 2 / CONNECT 1
    if(e==7||e==2)Locked=true;else if(e==8||e==1)Locked=false;changed();}
  }catch(Exception e){App.Log("Power watch: "+e.Message);}
  base.WndProc(ref m);
 }
}
partial class App {
 PowerWatch powerWatch;System.Windows.Forms.Timer powerTimer;
 // spatialPaused: the renderer stops presenting. spatialFlat: Spatial slides show as the flat photo; shownFlat is how
 // the current slide was shown (ShowSlide), so a change is applied once.
 bool spatialPaused,spatialFlat,shownFlat;string pauseReason="",flatReason="";DateTime flatClearSince=DateTime.MinValue;
 bool flatOnBattery;
 [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
 [StructLayout(LayoutKind.Sequential)] struct SystemPowerStatus{public byte ACLineStatus,BatteryFlag,BatteryLifePercent,SystemStatusFlag;public int BatteryLifeTime,BatteryFullLifeTime;}
 [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SystemPowerStatus s);
 string busyIgnored;
 // QUNS_BUSY / RUNNING_D3D_FULL_SCREEN alone are not enough (FullscreenCheck.cs).
 bool FullscreenInFront(int quns){
  string why=FullscreenCheck.WhyNot(FullscreenCheck.Foreground());
  if(why!=null&&why!=busyIgnored)Log("Spatial: QUNS "+quns+" ignored ("+why+")");
  busyIgnored=why;return why==null;
 }
 string BatteryFile{get{return Path.Combine(Dir,"spatial-battery.txt");}}
 void InitSpatialPower(){
  try{flatOnBattery=File.Exists(BatteryFile)&&File.ReadAllText(BatteryFile).Trim()=="flat";}catch{}
  try{powerWatch=new PowerWatch(UpdateSpatialPower);}catch(Exception e){Log("Power watch: "+e.Message);}
  // fullscreen apps have no notification of their own; 500 ms keeps a game's first seconds and the way back short
  powerTimer=new System.Windows.Forms.Timer{Interval=500};powerTimer.Tick+=delegate{UpdateSpatialPower();};powerTimer.Start();
  UpdateSpatialPower();
 }
 void UpdateSpatialPower(){try{
  int quns;if(SHQueryUserNotificationState(out quns)!=0)quns=5;
  SystemPowerStatus ps;bool known=GetSystemPowerStatus(out ps);
  bool battery=known&&ps.ACLineStatus==0;
  bool saver=powerWatch!=null&&(powerWatch.EnergySaverLevel>0||powerWatch.BatterySaverOn)||known&&(ps.SystemStatusFlag&1)!=0;
  if(quns!=2&&quns!=3)busyIgnored=null;
  string pause=quns==1?"away"   // QUNS_NOT_PRESENT: screen saver, locked, or another user's session in front
   :powerWatch!=null&&powerWatch.Locked?"locked"
   :powerWatch!=null&&powerWatch.DisplayOff?"display-off"
   :(quns==2||quns==3)&&FullscreenInFront(quns)||quns==4?"fullscreen"   // QUNS_BUSY, RUNNING_D3D_FULL_SCREEN, PRESENTATION_MODE
   :"";
  string flat=saver?"energy-saver":battery&&flatOnBattery?"battery":"";
  PaceGeneration(pause=="fullscreen",battery);   // making Spatial pictures waits too (SpatialGen.cs)
  bool p=pause!="";
  if(p!=spatialPaused||pause!=pauseReason){
   if(p!=spatialPaused)Log("Spatial "+(p?"paused: "+pause:"resumed"));
   spatialPaused=p;pauseReason=pause;
   if(spatialDesktop!=null)spatialDesktop.SetPaused(p);foreach(var c in closingSpatial)try{c.SetPaused(p);}catch{}
  }
  // into flat at once; back to Spatial only after the trigger has been gone for 3 s (AC plugged in and out, etc.)
  if(flat!=""){flatClearSince=DateTime.MinValue;if(!spatialFlat||flatReason!=flat){spatialFlat=true;flatReason=flat;Log("Spatial flat: "+flat);}}
  else if(spatialFlat){if(flatClearSince==DateTime.MinValue)flatClearSince=DateTime.Now;else if((DateTime.Now-flatClearSince).TotalSeconds>=3){spatialFlat=false;flatReason="";Log("Spatial flat: off");}}
  ApplySpatialFlat();
 }catch(Exception e){Log("Spatial power: "+e.Message);}}
 // Shows the current slide again the other way (Spatial or flat), keeping its countdown. Only with Spatial on; back to
 // Spatial waits until the desktop can be seen (no renderer is started for nobody).
 void ApplySpatialFlat(){
  if(!spatialEnabled||shownFlat==spatialFlat||currentSourcePath==null)return;
  if(!spatialFlat&&spatialPaused)return;
  var timer=nextSlide;
  try{if(Playable(currentSourcePath))ShowSlide(currentSourcePath);else shownFlat=spatialFlat;}catch(Exception e){shownFlat=spatialFlat;Log("Spatial power switch: "+e.Message);}
  nextSlide=timer;
 }
}