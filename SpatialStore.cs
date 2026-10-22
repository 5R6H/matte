using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

// 存储与清理 (from the Spatial page): where the Spatial pictures are kept, and how they are kept tidy.
// They live in spatial\pictures next to the exe (or where they were moved to). Legacy inventory never renames or
// deletes pictures. The sweep runs a minute after Matte starts, every six hours, after each generation and when this
// page opens - never while pictures are being made or the store is being moved.
partial class App {
 class StoreInfo{public string Dir;public int Files;public long Bytes;}
 StoreInfo storeInfo;int storeGeneration;System.Windows.Threading.DispatcherTimer storeDisarm;
 StoreInfo ReadStore(){
  var s=new StoreInfo{Dir=SpatialCacheDir};if(!Directory.Exists(s.Dir))return s;
  var catalog=SpatialCatalog.Load(s.Dir,new string[0],null);s.Files=catalog.Legacy+catalog.Valid;s.Bytes=catalog.Bytes;
  long ob=0;foreach(var d in (settings.SpatialStoreOld??new List<string>()).Where(Directory.Exists))try{ob+=new DirectoryInfo(d).EnumerateFiles("*",SearchOption.AllDirectories).Sum(x=>x.Length);}catch{}storeOldBytes=ob;
  return s;
 }
 long storeOldBytes;
 // 存储与清理 opened: its figures, and a sweep (whose end shows its result with RefreshStoreView, never another sweep:
 // that was a loop, sweeping the whole library again and again while the page stayed open)
 void RefreshStore(){RefreshStoreView();RunSpatialSweep(false,true);}
 void RefreshStoreView(){
  if(panel==null)return;int g=++storeGeneration;
  if(storeInfo==null)P<TextBlock>("StoreTotal").Text="正在统计…";
  Task.Run(()=>ReadStore()).ContinueWith(t=>panel.Dispatcher.BeginInvoke(new Action(()=>{if(g!=storeGeneration)return;if(t.Exception!=null){Log("Spatial store: "+t.Exception.InnerException.Message);return;}storeInfo=t.Result;PaintStore();})));
 }
 void PaintStore(){
  var s=storeInfo;if(s==null)return;
  P<TextBlock>("StoreTotal").Text=s.Files+" 张 · "+SizeText(s.Bytes).Replace("约 ","");
  string where=settings.SpatialStoreTarget??s.Dir;P<TextBlock>("StorePath").Text=where.Replace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"%LOCALAPPDATA%");P<TextBlock>("StorePath").ToolTip=where;
  P<Border>("StoreBackRow").Visibility=settings.SpatialStoreTarget!=null&&!storeMoving?Visibility.Visible:Visibility.Collapsed;
  foreach(var n in new[]{"StoreMoveBtn","StoreBackBtn"})P<Button>(n).IsEnabled=!storeMoving&&!Generating;
  var old=(settings.SpatialStoreOld??new List<string>()).Where(Directory.Exists).ToList();
  P<StackPanel>("StoreOldBox").Visibility=old.Count>0?Visibility.Visible:Visibility.Collapsed;
  if(old.Count>0){P<TextBlock>("StoreOldInfo").Text=SizeText(storeOldBytes).Replace("约 ","")+" · 新位置核对无误后可以清掉";P<TextBlock>("StoreOldBtnText").Text=oldArmed?"确认移到回收站":"清理";P<Button>("StoreOldBtn").IsEnabled=!storeMoving;}
  P<TextBlock>("StoreRowSize").Text=SizeText(s.Bytes).Replace("约 ","");
  P<TextBlock>("StoreDefaultPath").Text=DefaultSpatialCacheDir.Replace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"%LOCALAPPDATA%");
  PaintSweep();PaintStoreSharp();
 }
 // ---- legacy inventory and conservative v1 retention, off the UI thread
 int sweepsStarted;   // how many sweeps this run has started (the regression test for the loop counts them)
 bool sweeping;DateTime lastSweep=DateTime.Now.AddHours(-6).AddMinutes(1);SpatialIndex.Result sweepResult;SpatialRetention.Result retentionResult;
 System.Threading.CancellationTokenSource sweepCancel;
 internal SpatialRetention.IO retentionIO; // tests can pause a real scan between photo reads
 string lastSweepStore;
 void RunSpatialSweep(bool afterGeneration=false,bool pageOpened=false){
  if(sweeping||Generating||storeMoving||settings==null||ui==null)return;   // (no ui window: not the running app, e.g. a test)
  string store=SpatialCacheDir;
  if(!afterGeneration&&lastSweepStore==store&&(DateTime.Now-lastSweep).TotalHours<(pageOpened?1:6))return;
  sweeping=true;lastSweep=DateTime.Now;lastSweepStore=store;sweepsStarted++;
  var cancel=sweepCancel=new System.Threading.CancellationTokenSource();
  var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;string[] roots=PlaylistEntries().ToArray();
  Task.Run(()=>{
   var io=retentionIO??new SpatialRetention.IO();io.Cancelled=()=>cancel.IsCancellationRequested;
   var retained=SpatialRetention.Sweep(store,roots,Recycle,Log,DateTime.UtcNow,io);
   if(cancel.IsCancellationRequested)return Tuple.Create<SpatialIndex.Result,SpatialRetention.Result>(null,retained);
   var photos=new List<string>();foreach(string photo in SourceImages()){if(cancel.IsCancellationRequested)break;photos.Add(photo);}
   var legacy=SpatialIndex.Sweep(store,photos.Distinct(StringComparer.OrdinalIgnoreCase),Log,DateTime.Now);
   return Tuple.Create(legacy,retained);
  }).ContinueWith(t=>dispatcher.BeginInvoke(new Action(()=>{
   sweeping=false;sweepCancel=null;cancel.Dispose();
   try{if(t.Exception!=null){Log("Spatial sweep: "+t.Exception.InnerException.Message);return;}if(t.Result==null)return;
   if(t.Result.Item1!=null)sweepResult=t.Result.Item1;if(!t.Result.Item2.Skipped)retentionResult=t.Result.Item2;
   if(t.Result.Item2.RecycledPairs>0||t.Result.Item2.QuarantinedThisSweep>0)LoadSpatialCatalog();
   if(panel!=null&&PanelOpen){if(page=="spatialstore")RefreshStoreView();PaintSweep();}
   }finally{ResumeQueuedGeneration();}
  })));
 }
 void PaintSweep(){
  if(panel==null)return;var r=sweepResult;
  string head="Spatial 图片不会自动删除 · 原图找不到时，图片仍保留，由你决定何时清理";
  if(r==null){P<TextBlock>("StoreAutoInfo").Text=head;return;}
  var parts=new List<string>();
  if(r.Missing>0)parts.Add(r.Missing+" 张原图找不到，图片已保留（"+SizeText(r.MissingBytes).Replace("约 ","")+"）");
  if(r.Offline>0)parts.Add(r.Offline+" 张的原图在未连接的磁盘上，保留");
  P<TextBlock>("StoreAutoInfo").Text=head+"\n上次检查 "+r.When.ToString("HH:mm")+(parts.Count>0?" · "+string.Join(" · ",parts):" · 一切对应正常");
 }
 // many files to the Recycle Bin in one go (silently: the owner confirmed with the button)
 static void RecycleMany(List<string> files){
  for(int i=0;i<files.Count;i+=200){var part=files.Skip(i).Take(200).ToList();if(part.Count==0)break;
   var op=new SHFILEOPSTRUCT{wFunc=3,pFrom=string.Join("\0",part)+"\0\0",fFlags=(ushort)(0x40|0x10|0x4|0x400)};
   int rc=SHFileOperation(ref op);if(rc!=0||op.fAnyOperationsAborted)throw new IOException("移到回收站失败 ("+rc+")");}
 }

 // ---- 移动 / 移回: SpatialStoreMove does the work, here off the UI thread with its progress on the page. Not while
 // pictures are being made.
 bool storeMoving,oldArmed;
 void StoreMoveNote(string text,double progress){
  var box=P<Border>("StoreMoveBox");if(string.IsNullOrEmpty(text)){box.Visibility=Visibility.Collapsed;return;}
  box.Visibility=Visibility.Visible;P<TextBlock>("StoreMoveNote").Text=text;var bar=P<Grid>("StoreMoveBar");
  bar.Visibility=progress<0?Visibility.Collapsed:Visibility.Visible;if(progress>=0){bar.ColumnDefinitions[0].Width=new GridLength(progress,GridUnitType.Star);bar.ColumnDefinitions[1].Width=new GridLength(1-progress,GridUnitType.Star);}
 }
 void MoveStore(bool back){
  if(storeMoving)return;
  if(Generating){StoreMoveNote("正在生成 Spatial 图片，结束后再移动",-1);return;}
  string dest;
  if(back)dest=SpatialCacheDir;
  else{
   var h=new System.Windows.Interop.WindowInteropHelper(panel).Handle;List<string> picked;dropping=true;
   try{picked=PickPaths(h,true);}catch(Exception e){Log("Store picker: "+e.Message);picked=new List<string>();}finally{dropping=false;}
   panel.Activate();if(picked.Count==0)return;
   string chosen=picked[0];
   // an empty folder is used as it is; otherwise the pictures get their own folder in it
   bool empty=!Directory.Exists(chosen)||!Directory.EnumerateFileSystemEntries(chosen).Any();
   dest=empty?chosen:Path.Combine(chosen,"Spatial 图片");
  }
  storeMoving=true;PaintStore();StoreMoveNote("正在复制…",0);
  string def=SpatialCacheDir,cur=settings.SpatialStoreTarget;
  System.Threading.Tasks.Task.Run(()=>SpatialStoreMove.Move(def,cur,dest,p=>panel.Dispatcher.BeginInvoke(new Action(()=>StoreMoveNote("正在复制 "+(int)(p*100)+"%",p))),Log))
   .ContinueWith(t=>panel.Dispatcher.BeginInvoke(new Action(()=>{
    storeMoving=false;
    if(t.Exception!=null){string why=t.Exception.InnerException.Message;Log("Spatial store move: "+why);StoreMoveNote("没有移动："+why,-1);}
    else{var r=t.Result;settings.SpatialStoreTarget=r.Target;if(settings.SpatialStoreOld==null)settings.SpatialStoreOld=new List<string>();
     if(r.Backup!=null&&!settings.SpatialStoreOld.Contains(r.Backup))settings.SpatialStoreOld.Add(r.Backup);
     // The complete old tree stays until the owner explicitly clears this backup.
     SaveSettings();try{LoadSpatialCatalog();}catch{}StoreMoveNote(back?"已移回默认位置":"已移到新位置 · 旧位置的副本保留在下面",-1);}
    RefreshStore();})));
 }
 // the old copies, to the Recycle Bin (a second click within 3 s)
 void CleanOldStore(){
  if(!oldArmed){oldArmed=true;if(storeDisarm==null){storeDisarm=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(3)};storeDisarm.Tick+=delegate{storeDisarm.Stop();oldArmed=false;PaintStore();};}storeDisarm.Stop();storeDisarm.Start();PaintStore();return;}
  oldArmed=false;var dirs=(settings.SpatialStoreOld??new List<string>()).Where(Directory.Exists).ToList();if(dirs.Count==0)return;
  P<Button>("StoreOldBtn").IsEnabled=false;
  System.Threading.Tasks.Task.Run(()=>RecycleMany(dirs)).ContinueWith(t=>panel.Dispatcher.BeginInvoke(new Action(()=>{
   if(t.Exception!=null){Log("Old store: "+t.Exception.InnerException.Message);StoreMoveNote("没有清掉旧副本："+t.Exception.InnerException.Message,-1);}
   else{settings.SpatialStoreOld.RemoveAll(d=>!Directory.Exists(d));SaveSettings();Log("Old Spatial store copies to the Recycle Bin");}
   RefreshStore();})));
 }
}
