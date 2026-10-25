using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

// Playback settings: the playlist (what plays), the Spatial camera motion, the panel background, and how long each photo
// stays. Kept in settings.json with the rest of the settings (MatteSettings).
partial class App {
 // ------------------------------------------------------------ playlist
 // Entries are folders (their subfolders play too) and single images. With no list yet (never edited), the Wallpaper
 // folder alone; a list emptied on purpose stays empty (ShowEmpty).
 IEnumerable<string> PlaylistEntries(){
  if(settings==null)yield break;
  if(settings.Playlist==null){if(!string.IsNullOrEmpty(settings.Folder))yield return settings.Folder;yield break;}
  foreach(string e in settings.Playlist)yield return e;
 }
 List<string> Playlist{get{if(settings.Playlist==null)settings.Playlist=PlaylistEntries().ToList();return settings.Playlist;}}

 // Nothing to play (the playlist is empty, or none of it can be read): Spatial closes and the desktop
 // shows Matte's own neutral colour, a plain "ready" state; the panel says why and leads to the playlist.
 static readonly uint EmptyColor=Pack(Swatch.Neutral);
 bool showingEmpty;
 void ShowEmpty(){
  CancelSlideRequests();currentImage=null;
 currentSourcePath=null;slideFiles=new List<string>();slideIndex=0;slideBack.Clear();slideAhead.Clear();   // 上一张 must not bring back a removed photo
  if(!showingEmpty){
   showingEmpty=true;Log("Playlist empty: neutral wallpaper");
   try{
    string png=Path.Combine(Dir,"matte-empty.png");
    if(!File.Exists(png))using(var b=new System.Drawing.Bitmap(64,64)){using(var g=System.Drawing.Graphics.FromImage(b))g.Clear(Swatch.Neutral);b.Save(png,System.Drawing.Imaging.ImageFormat.Png);}
    CancelBlend();fade.Stop();CancelUnderneath();
    wall.SetBackgroundColor(EmptyColor);wall.SetWallpaper(null,png);ReleaseDisplayPins();PinWallpaper(png);last=Key(png);   // first, so a closing Spatial layer fades to it
    CloseSpatial(true);
   }catch(Exception e){Log("Empty wallpaper: "+e.Message);}
  }
  shownColor=EmptyColor;nextSlide=DateTime.Now.AddMilliseconds(settings.Interval);
  UpdateTray();PanelWallpaperChanged(null,EmptyColor,600);
 }
 void SaveSettings(){SettingsStore.Save(SettingsFile,json.Serialize(settings));}

 static bool IsUnder(string path,string folder){
  if(string.IsNullOrEmpty(path)||string.IsNullOrEmpty(folder))return false;string f=folder.TrimEnd('\\');
  return path.Equals(f,StringComparison.OrdinalIgnoreCase)||path.StartsWith(f+"\\",StringComparison.OrdinalIgnoreCase);
 }
 // Folders that hold the rest of the computer are refused (scanning them would take ages and play anything):
 // drive roots, the user folder and its standard folders (Desktop, Documents, Pictures, Downloads, …), Windows,
 // program folders. A folder inside them is fine.
 string PlaylistRefusal(string path){
  string full;try{full=Path.GetFullPath(path).TrimEnd('\\');}catch{return "路径无效";}
  if(Directory.Exists(full)){
   string root=Path.GetPathRoot(full);if(root!=null&&root.TrimEnd('\\').Equals(full,StringComparison.OrdinalIgnoreCase))return "不能添加整个磁盘，请选择其中的文件夹";
   string profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
   var special=new List<string>{profile,Path.Combine(profile,"Downloads"),Path.Combine(profile,"OneDrive"),Environment.GetEnvironmentVariable("OneDrive")};
   foreach(var f in new[]{Environment.SpecialFolder.Desktop,Environment.SpecialFolder.DesktopDirectory,Environment.SpecialFolder.MyDocuments,Environment.SpecialFolder.MyPictures,Environment.SpecialFolder.MyMusic,Environment.SpecialFolder.MyVideos,
    Environment.SpecialFolder.Windows,Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86,Environment.SpecialFolder.ApplicationData,Environment.SpecialFolder.LocalApplicationData,Environment.SpecialFolder.CommonDesktopDirectory})
    special.Add(Environment.GetFolderPath(f));
   foreach(string s in special)if(!string.IsNullOrEmpty(s)&&s.TrimEnd('\\').Equals(full,StringComparison.OrdinalIgnoreCase))return "不能添加系统文件夹（"+Path.GetFileName(full)+"），请选择其中的子文件夹";
   if(IsUnder(Environment.GetFolderPath(Environment.SpecialFolder.Windows),full)||IsUnder(full,Environment.GetFolderPath(Environment.SpecialFolder.Windows)))return "不能添加 Windows 系统文件夹";
     if(IsUnder(Dir,full)||IsUnder(full,Dir))return "不能添加 Matte 自己的文件夹";
  }else if(File.Exists(full)){
   if(!IsImage(full))return "只能添加图片（JPG、PNG、BMP、WebP）";
    }else return "找不到："+(Path.GetFileName(full).Length>0?Path.GetFileName(full):full);
  return null;
 }
 // Adds folders / images; returns a message for the panel (null when everything was added).
 string AddToPlaylist(IEnumerable<string> paths){
  var list=Playlist;var notes=new List<string>();int added=0;
  foreach(string raw in paths){
   string p;try{p=Path.GetFullPath(raw).TrimEnd('\\');}catch{continue;}
   string why=PlaylistRefusal(p);if(why!=null){notes.Add(why);continue;}
   if(list.Any(e=>IsUnder(p,e))){notes.Add(Path.GetFileName(p)+" 已经在播放列表里");continue;}
   list.RemoveAll(e=>IsUnder(e,p));   // a folder replaces the entries inside it
   list.Add(p);added++;
 }
  if(added>0)PlaylistChanged();
  return notes.Count==0?null:string.Join("；",notes.Distinct());
 }
 // The last entry can go too: an empty playlist shows the empty state (ShowEmpty).
 string RemoveFromPlaylist(string entry){
  Playlist.RemoveAll(e=>e.Equals(entry,StringComparison.OrdinalIgnoreCase));PlaylistChanged();return null;
 }
 // Dragged in the panel: the order is the order 顺序播放 plays in.
 void MovePlaylistEntry(int from,int to){
  var list=Playlist;if(from<0||from>=list.Count||to<0||to>=list.Count||from==to)return;
  string e=list[from];list.RemoveAt(from);list.Insert(to,e);PlaylistChanged();
 }
 void PlaylistChanged(){SaveSettings();try{RebuildSlides();}catch(Exception e){Log("Playlist: "+e.Message);}}
 // A new rotation from the playlist without re-showing the photo on screen; it moves on only if that photo left the list.
 void RebuildSlides(){
  List<string> files;if(!TryRotationFiles(out files))return;
  if(files.Count==0){ShowEmpty();return;}
  ArrangeRotation(files);
  var inList=new HashSet<string>(files,StringComparer.OrdinalIgnoreCase);
  slideBack.RemoveAll(p=>!inList.Contains(p));slideAhead.RemoveAll(p=>!inList.Contains(p));
  string keep=currentSourcePath;int k=keep==null?-1:files.FindIndex(p=>string.Equals(p,keep,StringComparison.OrdinalIgnoreCase));
  if(k>=0&&!Sequential){files.RemoveAt(k);files.Insert(0,keep);k=0;}   // in order, it carries on from where this photo sits
  slideFiles=files;slideIndex=k>=0?k+1:0;
  if(k<0)AdvanceSlide();
 }

 // ------------------------------------------------------------ order: shuffled, or in order
 // In order = the playlist's order, and inside each folder by path (so subfolders and file names in sequence).
 bool Sequential{get{return settings!=null&&settings.Order=="sequential";}}
 void ArrangeRotation(List<string> files){
  if(!Sequential){for(int i=files.Count-1;i>0;i--){int j=random.Next(i+1);string t=files[i];files[i]=files[j];files[j]=t;}return;}
  files.Sort(PhotoComparison());
 }
 Comparison<string> PhotoComparison(){var entries=PlaylistEntries().ToList();
  Func<string,int> entryOf=p=>{for(int i=0;i<entries.Count;i++)if(IsUnder(p,entries[i]))return i;return entries.Count;};
  return (a,b)=>{int entry=entryOf(a).CompareTo(entryOf(b));return entry!=0?entry:StringComparer.OrdinalIgnoreCase.Compare(a,b);};
 }
 void SetOrder(string order){settings.Order=order;SaveSettings();try{RebuildSlides();}catch(Exception e){Log("Order: "+e.Message);}}
 // Image count of one entry (a folder with its subfolders, or 1), for the panel.
 int CountImages(string entry){
  if(File.Exists(entry))return 1;if(!Directory.Exists(entry))return 0;
  int n=0;var pending=new Stack<string>();pending.Push(entry);
  while(pending.Count>0){string d=pending.Pop();try{foreach(string f in Directory.GetFiles(d))if(IsImage(f))n++;foreach(string s in Directory.GetDirectories(d))pending.Push(s);}catch{}}
  return n;
 }

 // ------------------------------------------------------------ Spatial camera motion
 // follow: leans toward the cursor; reverse: the opposite way; drift: moves on its own, slowly round the whole scene.
 // 0 follow, 1 reverse (with the mouse); 2 drift (a slow circle), 3 random = 手持 (a handheld camera's gentle wander): SpatialNative
 int MotionCode{get{string m=settings==null?null:settings.Motion;return m=="reverse"?1:m=="drift"?2:m=="random"?3:0;}}
 bool MotionIsMouse{get{string m=settings==null?null:settings.Motion;return m!="drift"&&m!="random";}}
 // each kind is remembered for its way, so switching between 跟随鼠标 and 自动 comes back to the kind chosen before
 void SetMotion(string m){settings.Motion=m;if(m=="follow"||m=="reverse")settings.MotionMouse=m;else settings.MotionAuto=m;SaveSettings();if(spatialDesktop!=null)spatialDesktop.SetMotion(MotionCode);}

 // ------------------------------------------------------------ how long each photo stays
 internal static readonly uint[] IntervalSteps={30000,60000,300000,900000,1800000,3600000};   // 30s 1min 5min 15min 30min 1h
 static string IntervalText(uint ms){return ms<60000?(ms/1000)+" 秒":ms<3600000?(ms/60000)+" 分钟":(ms/3600000)+" 小时";}
 // The photo on screen keeps the time it has already had: the next one comes when the new length is up (or soon).
 void SetInterval(uint ms){
  uint old=settings.Interval;settings.Interval=ms;SaveSettings();
  if(nextSlide==DateTime.MaxValue)return;
  var shown=nextSlide.AddMilliseconds(-(double)old);var next=shown.AddMilliseconds(ms);
  nextSlide=next<DateTime.Now.AddSeconds(3)?DateTime.Now.AddSeconds(3):next;
 }

 // ------------------------------------------------------------ pickers (the panel steps aside while one is open)
 [ComImport,Guid("D57C7288-D4AD-4768-BE02-9D969532D960"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IPickDialog {
  [PreserveSig]int Show(IntPtr parent);void SetFileTypes(uint count,[MarshalAs(UnmanagedType.LPArray)]FilterSpec[] specs);void SetFileTypeIndex();void GetFileTypeIndex();void Advise();void Unadvise();void SetOptions(uint options);void GetOptions(out uint options);void SetDefaultFolder();void SetFolder();void GetFolder();void GetCurrentSelection();void SetFileName();void GetFileName();void SetTitle([MarshalAs(UnmanagedType.LPWStr)]string title);void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)]string text);void SetFileNameLabel();void GetResult();void AddPlace();void SetDefaultExtension();void Close();void SetClientGuid();void ClearClientData();void SetFilter();void GetResults(out IPickArray items);void GetSelectedItems();
 }
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct FilterSpec{[MarshalAs(UnmanagedType.LPWStr)]public string Name;[MarshalAs(UnmanagedType.LPWStr)]public string Spec;}
 [ComImport,Guid("B63EA76D-1F85-456F-A19C-48159EFA858B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IPickArray {void BindToHandler();void GetPropertyStore();void GetPropertyDescriptionList();void GetAttributes();void GetCount(out uint count);void GetItemAt(uint i,out IPickItem item);void EnumItems();}
 [ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface IPickItem {void BindToHandler();void GetParent();void GetDisplayName(uint sigdn,out IntPtr name);void GetAttributes();void Compare();}
 // folders: several folders; otherwise several images. Empty when cancelled.
 static List<string> PickPaths(IntPtr owner,bool folders){
  var result=new List<string>();var dialog=(IPickDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")));
  try{
   // FOS_NOCHANGEDIR | FOS_FORCEFILESYSTEM | FOS_ALLOWMULTISELECT | FOS_PATHMUSTEXIST | FOS_DONTADDTORECENT (+ FOS_PICKFOLDERS)
   dialog.SetOptions(0x8u|0x40u|0x200u|0x800u|0x2000000u|(folders?0x20u:0x1000u));
   if(folders){dialog.SetTitle("添加文件夹到播放列表 · 子文件夹里的图片也会播放");dialog.SetOkButtonLabel("添加");}
   else{dialog.SetFileTypes(1,new[]{new FilterSpec{Name="图片",Spec="*.jpg;*.jpeg;*.png;*.bmp;*.webp"}});dialog.SetTitle("添加图片到播放列表");dialog.SetOkButtonLabel("添加");}
   int hr=dialog.Show(owner);if(hr==unchecked((int)0x800704C7))return result;Marshal.ThrowExceptionForHR(hr);
   IPickArray array;dialog.GetResults(out array);
   try{uint count;array.GetCount(out count);for(uint i=0;i<count;i++){IPickItem item;array.GetItemAt(i,out item);try{IntPtr text;item.GetDisplayName(0x80058000,out text);try{result.Add(Marshal.PtrToStringUni(text));}finally{Marshal.FreeCoTaskMem(text);}}finally{Marshal.ReleaseComObject(item);}}}
   finally{Marshal.ReleaseComObject(array);}
  }finally{Marshal.ReleaseComObject(dialog);}
  return result;
 }
}
