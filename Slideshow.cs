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
  if(File.Exists(SettingsFile))settings=json.Deserialize<MatteSettings>(File.ReadAllText(SettingsFile));else{
   settings=new MatteSettings();settings.Folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),"Wallpaper");settings.Playlist=new List<string>();
   settings.Position=wall.GetPosition();wall.GetSlideshowOptions(out settings.Options,out settings.Interval);if(settings.Interval<10000)settings.Interval=1800000;settings.Options|=1;
   File.WriteAllText(SettingsFile,json.Serialize(settings));
  }
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
 // WebP is shown through a JPEG copy (Windows' wallpaper service does not take WebP), named by a hash of the path.
 string DisplayPath(string path){if(!path.EndsWith(".webp",StringComparison.OrdinalIgnoreCase))return path;string folder=Path.Combine(Dir,"display-cache");Directory.CreateDirectory(folder);string key=path+File.GetLastWriteTimeUtc(path).Ticks;string hash;using(var sha=System.Security.Cryptography.SHA256.Create())hash=BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key))).Replace("-","");string output=Path.Combine(folder,hash+".jpg");if(!File.Exists(output)){var decoder=System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri(path),System.Windows.Media.Imaging.BitmapCreateOptions.None,System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);var encoder=new System.Windows.Media.Imaging.JpegBitmapEncoder();encoder.QualityLevel=96;encoder.Frames.Add(decoder.Frames[0]);using(var stream=File.Create(output))encoder.Save(stream);}return output;}
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct SHFILEOPSTRUCT{public IntPtr hwnd;public uint wFunc;public string pFrom;public string pTo;public ushort fFlags;public bool fAnyOperationsAborted;public IntPtr hNameMappings;public string lpszProgressTitle;}
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
 static void Recycle(string path){if(string.IsNullOrEmpty(path)||!File.Exists(path))return;var op=new SHFILEOPSTRUCT{wFunc=3,pFrom=path+"\0\0",fFlags=0x40|0x10};int rc=SHFileOperation(ref op);if(rc!=0||op.fAnyOperationsAborted)throw new IOException("无法将文件移到回收站: "+Path.GetFileName(path));}
 void DeleteCurrentWallpaper(){DeleteCurrentWallpaper(false);}
 // confirmed: the panel's 编辑壁纸 page already asked (its button needs a second click), so no dialog
 void DeleteCurrentWallpaper(bool confirmed){
  if(string.IsNullOrEmpty(currentSourcePath)||!File.Exists(currentSourcePath)){if(!confirmed)MessageBox.Show("当前没有可删除的壁纸。","删除壁纸",MessageBoxButtons.OK,MessageBoxIcon.Information);return;}
  string source=currentSourcePath, map=SpatialPathFor(source), display=DisplayPath(source);
  if(map==null){string any;if(spatialFiles.TryGetValue(source,out any)&&File.Exists(any))map=any;}   // an outdated one goes too
  string name=Path.GetFileName(source);if(!confirmed&&MessageBox.Show("确定把当前壁纸移到回收站吗？\n\n"+name+"\n位于："+Path.GetFileName(Path.GetDirectoryName(source))+" 文件夹\n\n这会删除文件夹里的原图（可从回收站恢复），不只是从播放列表移除。对应的 Spatial 文件也会一起移入回收站。","删除当前壁纸",MessageBoxButtons.YesNo,MessageBoxIcon.Warning,MessageBoxDefaultButton.Button2)!=DialogResult.Yes)return;
  try{
   DeleteAmbient(display);   // before the photo goes (its key needs the file); a copy made from it, not the photo
   Recycle(source);if(!string.Equals(display,source,StringComparison.OrdinalIgnoreCase))Recycle(display);if(!string.IsNullOrEmpty(map))Recycle(map);
   slideFiles.RemoveAll(p=>string.Equals(p,source,StringComparison.OrdinalIgnoreCase));spatialSlides.RemoveAll(p=>string.Equals(p,source,StringComparison.OrdinalIgnoreCase));spatialFiles.Remove(source);spatialFiles.Remove(display);currentSourcePath=null;last="";
   if(slideFiles.Count==0){ShowEmpty();return;}AdvanceSlide();   // the last photo gone: the empty state
  }catch(Exception e){Log("Delete wallpaper: "+e.Message);MessageBox.Show("删除失败：\n"+e.Message,"删除壁纸",MessageBoxButtons.OK,MessageBoxIcon.Error);}
 }
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
 void ShowSlide(string path){bool newPhoto=!string.Equals(currentSourcePath,path,StringComparison.OrdinalIgnoreCase);currentSourcePath=path;showingEmpty=false;
 string display=DisplayPath(path);if(spatialEnabled&&display!=path)spatialFiles[display]=SpatialPathFor(path);fade.Stop();
 uint color=ColorFor(display);
 shownColor=color;   // before the panel refreshes, so its swatch shows this photo's colour
 PanelWallpaperChanged(display,color,1600,newPhoto);   // the open panel's backdrop and card follow, in step with the 1.6 s cross-fade
 // Energy saver (SpatialPower.cs): a Spatial slide is shown as the flat photo until it ends.
 shownFlat=spatialFlat;
 // on its ambience (AmbientWallpaper.cs) or the photo itself on the colour
 string backdrop,shown=WallpaperImage(display,out backdrop);
 if(SpatialPlays&&!spatialFlat&&SpatialPathFor(path)!=null){StartSpatial(shown,SpatialPathFor(path),color,backdrop);}
 else {
  // Leaving Spatial: the same photo is already the Windows wallpaper underneath, so the Spatial layer just fades away.
  FlushUnderneath();   // a slide change's wallpaper may still be waiting (SetUnderneathLater)
  bool same=spatialDesktop!=null&&string.Equals(wall.GetWallpaper(wall.GetMonitorDevicePathAt(0)),shown,StringComparison.OrdinalIgnoreCase);
  CloseSpatial(true);
  if(same){wall.SetBackgroundColor(color);last=Key(shown);}
  else if(!StartBlend(shown,color)){wall.SetBackgroundColor(color);wall.SetWallpaper(null,shown);last=Key(shown);}}
 shownColor=color;nextSlide=DateTime.Now.AddMilliseconds(settings.Interval);
 PrepareNextAmbient();
 UpdateTray();}   // tray and desktop shortcut take this photo's colour now, not at the next 2 s poll
 // Every 2 s (App.Check): a second start of Matte asks for the panel (show.request), and the next photo when it is due.
 void CheckRequests(){
  string f=Path.Combine(Dir,"show.request");if(File.Exists(f)){try{File.Delete(f);}catch{}TogglePanel(false);}
  if(!EditingPhoto&&!holdSlide&&DateTime.Now>=nextSlide){try{AdvanceSlide();}catch(Exception e){Log("Slideshow: "+e.Message);nextSlide=DateTime.Now.AddMilliseconds(settings.Interval);}}
 }
 // A small decoded copy for the menu card, so the full image is not kept in memory.
 static Image Thumb(string path){
  using(var im=Image.FromFile(path)){double k=Math.Min(1,Math.Min(240.0/im.Width,150.0/im.Height));var b=new Bitmap(Math.Max(1,(int)(im.Width*k)),Math.Max(1,(int)(im.Height*k)));
   using(var g=Graphics.FromImage(b)){g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;g.DrawImage(im,0,0,b.Width,b.Height);}return b;}
 }
}
