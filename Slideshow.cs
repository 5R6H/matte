using System;using System.IO;using System.Drawing;using System.Windows.Forms;using System.Runtime.InteropServices;using System.Collections.Generic;using Microsoft.Win32;using System.Web.Script.Serialization;
[ComImport,Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IShellItemArray{void BindToHandler();void GetPropertyStore();void GetPropertyDescriptionList();void GetAttributes();uint GetCount();}
// settings.json, next to the exe.
class MatteSettings {
 public string Folder;public int Position=3;public uint Interval=1800000;public uint Options=1;
 // Playback (Playback.cs): what plays (folders, with their subfolders, and single images; null = just Folder), how the
 // Spatial camera moves (follow / reverse / drift / random) and the panel's background (ambient = the photo, simple = system theme).
 public List<string> Playlist;public string Motion="follow";public string PanelStyle="ambient";
 public string MotionMouse,MotionAuto;   // the last kind of each way (follow / reverse, drift / random)
 public string Order="shuffle";   // shuffle | sequential (playlist order, then file names)
 public List<string> SpatialSkip;   // playlist entries left out of 生成 Spatial 图片 (unticked on the Spatial page)
 // Spatial generation (optional, see SpatialGen.cs and spatial\README.md): a generator of your own instead of SHARP
 public string SpatialGenerator=null;   // the PowerShell script that makes Spatial pictures (-RootsFile <list> -Cache <folder>)
 public string SpatialPython=null;      // the python.exe it needs, only checked to be there
 public string SpatialFolder=null;      // where the .spatial pictures are kept
 public string SpatialStoreTarget;   // where the Spatial store really is when moved (the default path is then a junction to it)
 public List<string> SpatialStoreOld;   // old copies left by a move, until cleared on 存储与清理
 public string Background="color";   // the wallpaper around the photo: color | extend | ambient
 // Values Matte does not offer (a hand-edited or damaged file) go back to the defaults. The photo is always shown whole
 // (Windows' "fit", 3): Matte has no other choice, so anything else came from Windows' own setting when Matte first
 // started, and "fill" crops the photo and keeps the synchronized cross-fade (SynchronizedFade) from running.
 public void Normalize(){
  Position=3;if(Interval<10000)Interval=1800000;
  if(Background!="color"&&Background!="extend"&&Background!="ambient")Background="color";
  if(PanelStyle!="ambient"&&PanelStyle!="simple")PanelStyle="ambient";
  if(Order!="shuffle"&&Order!="sequential")Order="shuffle";
  if(Motion!="follow"&&Motion!="reverse"&&Motion!="drift"&&Motion!="random")Motion="follow";
 }
}
// A hidden window for the app's own messages: Explorer restarting (the tray icon and the wallpaper service come back),
// and work handed back to the UI thread from background threads.
class MessageWindow:Form {
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int RegisterWindowMessage(string name);
 static readonly int TaskbarCreated=RegisterWindowMessage("TaskbarCreated");
 public Action ShellRestarted;
 public MessageWindow(){ShowInTaskbar=false;FormBorderStyle=FormBorderStyle.None;Opacity=0;var h=Handle;}
 protected override void WndProc(ref Message m){
  try{if(m.Msg==TaskbarCreated&&TaskbarCreated!=0&&ShellRestarted!=null)BeginInvoke(ShellRestarted);}
  catch(Exception e){App.Log("Window message: "+e.Message);}
  base.WndProc(ref m);}
}
// The slideshow: the playlist's photos, one at a time, each on its own colour (or extended, or on its ambience), in 3D
// when it has a Spatial picture and Spatial is on.
partial class App {
 MessageWindow ui;MatteSettings settings;DateTime nextSlide=DateTime.MaxValue;List<string> slideFiles=new List<string>();int slideIndex=0;Random random=new Random();
 static JavaScriptSerializer json=new JavaScriptSerializer();
 string SettingsFile{get{return Path.Combine(Dir,"settings.json");}}
 void InitSlideshow(){
  // read safely (SettingsStore, Theme.cs): a damaged file falls back to the copy before it, or to fresh settings
  settings=SettingsStore.Load(SettingsFile,t=>json.Deserialize<MatteSettings>(t),Log);
  if(settings!=null){settings.Normalize();SaveSettings();}else{
   settings=new MatteSettings();settings.Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),"Wallpaper");settings.Playlist=new List<string>();
   wall.GetSlideshowOptions(out settings.Options,out settings.Interval);settings.Normalize();   // the photo whole, not Windows' own fillif(settings.Interval<10000)settings.Interval=1800000;settings.Options|=1;
   SaveSettings();
  }
  DisplayCache.Register(DisplayCacheDir);PinWallpaper(wall.GetWallpaper(wall.GetMonitorDevicePathAt(0)));
  LoadSpatialMode();ui=new MessageWindow();ui.ShellRestarted=OnShellRestarted;
  InitSpatialPower();
  try{UseSlideshow();}catch(Exception e){Log("Slideshow start: "+e.Message);}
 }
 static bool IsImage(string path){string ext=Path.GetExtension(path).ToLowerInvariant();return ext==".jpg"||ext==".jpeg"||ext==".png"||ext==".bmp"||ext==".webp";}
 // Everything on the playlist plays: every image under its folders (subfolders included) and its single images, each
 // once. Any folder name listed in excluded-folders.txt (next to the exe) is skipped.
 List<string> SourceImages(){
  var excluded=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  string list=Path.Combine(Dir,"excluded-folders.txt");if(File.Exists(list))foreach(string line in File.ReadAllLines(list)){string n=line.Trim().TrimEnd('\\');if(n.Length>0&&!n.StartsWith("#"))excluded.Add(Path.GetFileName(n));}
  var result=new List<string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var pending=new Stack<string>();
  foreach(string entry in PlaylistEntries()){
   if(File.Exists(entry)){if(IsImage(entry)&&seen.Add(entry))result.Add(entry);}
   else if(Directory.Exists(entry))pending.Push(entry);
  }
  while(pending.Count>0){string dir=pending.Pop();
   try{foreach(string f in Directory.GetFiles(dir))if(IsImage(f)&&seen.Add(f))result.Add(f);
    foreach(string d in Directory.GetDirectories(dir))if(!excluded.Contains(Path.GetFileName(d)))pending.Push(d);}
   catch(Exception e){Log("A playlist folder could not be read: "+e.Message);}}
  return result;
 }
 // keep: the image to stay on (switching Spatial on/off keeps the same photo, and its countdown, when it can).
 void UseSlideshow(){UseSlideshow(null);}
 void UseSlideshow(string keep){
  slideFiles.Clear();slideFiles.AddRange(RotationFiles());if(slideFiles.Count==0){ShowEmpty();return;}   // nothing to play: the empty state, not an error
  ArrangeRotation(slideFiles);slideIndex=0;wall.SetPosition(settings.Position);
  int k=keep==null?-1:slideFiles.FindIndex(p=>string.Equals(p,keep,StringComparison.OrdinalIgnoreCase));var timer=nextSlide;
  if(k>=0&&!Sequential){slideFiles.RemoveAt(k);slideFiles.Insert(0,keep);k=0;}   // in order, the rotation carries on from where this photo sits
  if(k>=0&&Playable(keep)){slideIndex=k+1;ShowSlide(keep);if(timer>DateTime.Now)nextSlide=timer;}else AdvanceSlide();
 }
 // Flat photos use their source; WebP and verified 3D underlays use captured display copies.
 string DisplayPath(string path){if(currentImage!=null&&currentImage.Source==path&&File.Exists(currentImage.Display))return currentImage.Display;if(!path.EndsWith(".webp",StringComparison.OrdinalIgnoreCase))return path;using(var image=new SlideSnapshot(path,File.ReadAllBytes(path)))return image.MakeDisplay(DisplayCacheDir);}
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct SHFILEOPSTRUCT{public IntPtr hwnd;public uint wFunc;public string pFrom;public string pTo;public ushort fFlags;public bool fAnyOperationsAborted;public IntPtr hNameMappings;public string lpszProgressTitle;}
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
 static void Recycle(string path){if(string.IsNullOrEmpty(path)||!File.Exists(path))return;var op=new SHFILEOPSTRUCT{wFunc=3,pFrom=path+"\0\0",fFlags=0x40|0x10};int rc=SHFileOperation(ref op);if(rc!=0||op.fAnyOperationsAborted)throw new IOException("无法将文件移到回收站: "+Path.GetFileName(path));}
 // History for 上一张 / 下一张: back = photos shown before the current one; ahead = photos stepped back over (下一张 replays them).
 readonly List<string> slideBack=new List<string>(),slideAhead=new List<string>();uint shownColor;
 // Spatial 3D is a preference for the whole list, not a filter: every photo plays, in 3D when it has an up-to-date
 // Spatial file and Spatial is on, flat otherwise. SpatialPlays = some photo in the list will actually show in 3D.
 bool SpatialPlays{get{return spatialEnabled&&spatialSlides.Count>0;}}
 bool Playable(string p){return File.Exists(p);}
 List<string> RotationFiles(){LoadSpatialCatalog();return SourceImages();}
 void Remember(string next){if(currentSourcePath!=null&&!string.Equals(currentSourcePath,next,StringComparison.OrdinalIgnoreCase)){slideBack.Add(currentSourcePath);if(slideBack.Count>100)slideBack.RemoveAt(0);}}
 void AdvanceSlide(){
  while(slideAhead.Count>0){string p=slideAhead[slideAhead.Count-1];slideAhead.RemoveAt(slideAhead.Count-1);if(Playable(p)){Remember(p);ShowSlide(p);return;}}
  for(int tries=0;tries<slideFiles.Count;tries++){string path=slideFiles[slideIndex++%slideFiles.Count];if(!Playable(path))continue;Remember(path);ShowSlide(path);return;}
  // Nothing playable (playlist emptied or its files gone): the photo on screen stays and it tries again next time.
  if(slideFiles.Count==0){ShowEmpty();return;}
  Log("Slideshow: nothing playable in the playlist; staying on the current photo");nextSlide=DateTime.Now.AddMilliseconds(settings.Interval);}
 void PreviousSlide(){
  while(slideBack.Count>0){string p=slideBack[slideBack.Count-1];slideBack.RemoveAt(slideBack.Count-1);if(!Playable(p))continue;if(currentSourcePath!=null)slideAhead.Add(currentSourcePath);ShowSlide(p);return;}}
 void ShowSlide(string path){BeginSlide(path,false);}
 // Every 2 s (App.Check): a second start of Matte asks for the panel (show.request), and the next photo when it is due.
 void CheckRequests(){
  PinWallpaper(wall.GetWallpaper(wall.GetMonitorDevicePathAt(0))); // Includes delayed/flat wallpaper changes after a transition.
  ReevaluateCurrentSpatial();
  string f=Path.Combine(Dir,"show.request");if(File.Exists(f)){try{File.Delete(f);}catch{}TogglePanel(false);}
  if(!holdSlide&&DateTime.Now>=nextSlide){try{AdvanceSlide();}catch(Exception e){Log("Slideshow: "+e.Message);nextSlide=DateTime.Now.AddMilliseconds(settings.Interval);}}
 }
 // A small decoded copy for the menu card, so the full image is not kept in memory.
 static Image Thumb(string path){
  using(var im=Image.FromFile(path)){double k=Math.Min(1,Math.Min(240.0/im.Width,150.0/im.Height));var b=new Bitmap(Math.Max(1,(int)(im.Width*k)),Math.Max(1,(int)(im.Height*k)));
   using(var g=Graphics.FromImage(b)){g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;g.DrawImage(im,0,0,b.Width,b.Height);}return b;}
 }
}
