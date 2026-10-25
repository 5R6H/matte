using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// Metadata is safe to inspect; a read which could recall remote bytes is never attempted.
// This is a best-effort attribute gate, not an atomic promise against an external dehydration race.
static class PhotoReadPolicy {
 internal const FileAttributes Remote=(FileAttributes)(0x1000|0x40000|0x400000);
 internal static bool Local(FileAttributes attributes){return (attributes&Remote)==0;}
 internal static void Demand(string path){Demand(File.GetAttributes(path));}
 internal static void Demand(FileAttributes attributes){if(!Local(attributes))throw new IOException("Photo data is not local");}
 internal static Stream Open(IPhotoFileSystem fs,string path){Demand(fs.Stat(path).Attributes);return fs.OpenRead(path);}
 internal static byte[] Read(IPhotoFileSystem fs,string path){using(var input=Open(fs,path))using(var output=new MemoryStream()){input.CopyTo(output);return output.ToArray();}}
}
internal sealed class PhotoStamp {
 internal readonly long Size,Ticks;internal readonly FileAttributes Attributes;
 internal PhotoStamp(long size,long ticks,FileAttributes attributes){Size=size;Ticks=ticks;Attributes=attributes;}
 internal bool Directory{get{return (Attributes&FileAttributes.Directory)!=0;}}
}
internal interface IPhotoFileSystem {
 PhotoStamp Stat(string path);
 string[] Children(string directory);
 Stream OpenRead(string path);
 IDisposable Watch(string directory,Action<string,string> changed,Action error);
}
internal sealed class PhotoFileSystem:IPhotoFileSystem {
 public PhotoStamp Stat(string path){var a=File.GetAttributes(path);var f=new FileInfo(path);return new PhotoStamp((a&FileAttributes.Directory)!=0?0:f.Length,f.LastWriteTimeUtc.Ticks,a);}
 public string[] Children(string path){return Directory.GetFileSystemEntries(path);}
 public Stream OpenRead(string path){PhotoReadPolicy.Demand(path);return new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);}
 public IDisposable Watch(string path,Action<string,string> changed,Action error){
  var w=new FileSystemWatcher(path){IncludeSubdirectories=true,Filter="*",InternalBufferSize=16384,
   NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|NotifyFilters.LastWrite|NotifyFilters.Size|NotifyFilters.Attributes};
  w.Created+=(s,e)=>changed(e.FullPath,null);w.Deleted+=(s,e)=>changed(e.FullPath,null);w.Changed+=(s,e)=>changed(e.FullPath,null);
  w.Renamed+=(s,e)=>changed(e.FullPath,e.OldFullPath);w.Error+=(s,e)=>error();
  try{w.EnableRaisingEvents=true;return w;}catch{w.Dispose();throw;}
 }
}
internal enum PhotoState{Settling,Ready,Unreadable}
internal enum InventoryAvailability{Enumerated,Absent,Unknown}
internal sealed class InventoryPhoto {
 internal readonly string Path;internal readonly long Size,Mtime;internal readonly PhotoState State;internal readonly DateTime Observed;
 internal InventoryPhoto(string path,long size,long mtime,PhotoState state,DateTime observed){Path=path;Size=size;Mtime=mtime;State=state;Observed=observed;}
 internal InventoryPhoto Reset(){return new InventoryPhoto(Path,Size,Mtime,PhotoState.Settling,DateTime.MaxValue);}
}
internal sealed class InventoryEntry {
 internal readonly string Path;internal readonly bool Directory,Scanned;
 internal readonly ReadOnlyCollection<InventoryPhoto> Members;
 internal readonly ReadOnlyDictionary<string,InventoryAvailability> Availability;
 internal InventoryEntry(string path,bool directory,bool scanned,IEnumerable<InventoryPhoto> members,IDictionary<string,InventoryAvailability> availability){
  Path=path;Directory=directory;Scanned=scanned;Members=Array.AsReadOnly(members.ToArray());Availability=new ReadOnlyDictionary<string,InventoryAvailability>(new Dictionary<string,InventoryAvailability>(availability,StringComparer.OrdinalIgnoreCase));
 }
}
internal sealed class InventorySnapshot {
 internal readonly long Revision,Sequence;internal readonly ReadOnlyCollection<InventoryEntry> Entries;
 internal readonly ReadOnlyCollection<string> ReadyPaths,UnknownRoots;internal readonly bool Scanned;
 internal InventorySnapshot(long revision,long sequence,IEnumerable<InventoryEntry> entries){
  Revision=revision;Sequence=sequence;Entries=Array.AsReadOnly(entries.ToArray());Scanned=Entries.All(e=>e.Scanned);
  // SourceImages historically lists explicit files first, then traverses folder roots as a stack.
  var ordered=Entries.Where(e=>!e.Directory).Concat(Entries.Where(e=>e.Directory).Reverse());
  ReadyPaths=Array.AsReadOnly(ordered.SelectMany(e=>e.Members).Where(p=>p.State==PhotoState.Ready).Select(p=>p.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
  UnknownRoots=Array.AsReadOnly(Entries.SelectMany(e=>e.Availability).Where(p=>p.Value==InventoryAvailability.Unknown).Select(p=>p.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
 }
}
internal sealed class InventoryChange {
 // Added/Removed describe membership. Changed includes readiness transitions; T4b must consult ReadyPaths.
 internal readonly InventorySnapshot Snapshot;internal long Revision{get{return Snapshot.Revision;}}
 internal readonly ReadOnlyCollection<string> Added,Removed,Changed;
 internal InventoryChange(InventorySnapshot before,InventorySnapshot after){
  Snapshot=after;var a=Flatten(before);var b=Flatten(after);
  Added=Array.AsReadOnly(b.Keys.Except(a.Keys,StringComparer.OrdinalIgnoreCase).ToArray());Removed=Array.AsReadOnly(a.Keys.Except(b.Keys,StringComparer.OrdinalIgnoreCase).ToArray());
  Changed=Array.AsReadOnly(b.Keys.Where(k=>a.ContainsKey(k)&&(a[k].Size!=b[k].Size||a[k].Mtime!=b[k].Mtime||a[k].State!=b[k].State||a[k].Observed!=b[k].Observed)).ToArray());
 }
 static Dictionary<string,InventoryPhoto> Flatten(InventorySnapshot s){return s.Entries.SelectMany(e=>e.Members).GroupBy(p=>p.Path,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);}
}

// One independent worker per root, never two scans of the same root. Watchers only invalidate;
// enumeration is authoritative only for portions actually enumerated. All snapshots are immutable.
internal sealed class PlaylistInventory:IDisposable {
 sealed class Root {
  internal string Path;internal InventoryEntry Value;internal bool Busy,WatchFailed;internal IDisposable Watcher;
  internal DateTime First=DateTime.MaxValue,Last,Next,WatchNext,RetryNext=DateTime.MaxValue;internal int Backoff=30,Retry=1;
  internal readonly HashSet<string> Dirty=new HashSet<string>(StringComparer.OrdinalIgnoreCase),During=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
 }
 readonly object gate=new object();readonly IPhotoFileSystem fs;readonly Func<DateTime> now;readonly Action<Action> post;readonly string exclusions;
 readonly Dictionary<string,Root> roots=new Dictionary<string,Root>(StringComparer.OrdinalIgnoreCase);
 string[] order=new string[0];HashSet<string> excluded=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
 long revision,sequence;bool disposed,exBusy,exLoaded,notificationPending;DateTime exNext;Timer timer;InventorySnapshot snapshot=new InventorySnapshot(0,0,new InventoryEntry[0]),delivered=new InventorySnapshot(0,0,new InventoryEntry[0]);
 internal event Action<InventoryChange> Changed;
 internal PlaylistInventory(IPhotoFileSystem fileSystem,Func<DateTime> clock,Action<Action> dispatch,string exclusionFile=null,bool automatic=true){
  fs=fileSystem;now=clock;post=dispatch;exclusions=exclusionFile;exLoaded=exclusionFile==null;
  if(automatic)timer=new Timer(x=>Tick(),null,250,250);
 }
 internal InventorySnapshot Snapshot{get{lock(gate)return snapshot;}}
 internal bool Idle{get{lock(gate)return !exBusy&&roots.Values.All(r=>!r.Busy);}}
 internal long Configure(IEnumerable<string> entries){lock(gate){
  string[] next=entries.Select(p=>Path.GetFullPath(p).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
  if(order.SequenceEqual(next,StringComparer.OrdinalIgnoreCase))return revision;
  order=next;revision++;
  foreach(var r in roots.Values)if(!next.Contains(r.Path,StringComparer.OrdinalIgnoreCase)){if(r.Watcher!=null)r.Watcher.Dispose();r.Watcher=null;}
  foreach(string path in next){Root r;if(!roots.TryGetValue(path,out r)){r=new Root{Path=path,Value=Empty(path)};roots.Add(path,r);}r.Value=new InventoryEntry(r.Path,r.Value.Directory,false,r.Value.Members,r.Value.Availability);r.Next=now();Dirty(r,path);}
  Publish();return revision;
 }}
 static InventoryEntry Empty(string p){return new InventoryEntry(p,!Image(p),false,new InventoryPhoto[0],new Dictionary<string,InventoryAvailability>{{p,InventoryAvailability.Unknown}});}
 internal static bool Image(string path){string x=Path.GetExtension(path).ToLowerInvariant();return x==".jpg"||x==".jpeg"||x==".png"||x==".bmp"||x==".webp";}
 static bool Under(string path,string root){return path.Equals(root,StringComparison.OrdinalIgnoreCase)||path.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);}
 static bool ExcludedPhoto(string root,string path,HashSet<string> skip){if(!Under(path,root)||path.Length<=root.Length)return false;
  var parts=path.Substring(root.TrimEnd('\\').Length).TrimStart('\\').Split('\\');return parts.Take(Math.Max(0,parts.Length-1)).Any(skip.Contains);}
 static void Bounded(HashSet<string> set,string path,string root){if(set.Contains(root))return;if(set.Count>=256){set.Clear();set.Add(root);}else set.Add(path);}
 void Dirty(Root r,string path){var time=now();if(r.Dirty.Count==0)r.First=time;r.Last=time;Bounded(r.Dirty,path,r.Path);if(r.Busy)Bounded(r.During,path,r.Path);}
 internal void Notify(string root,string path,string oldPath=null){lock(gate){Root r;if(disposed||!order.Contains(root,StringComparer.OrdinalIgnoreCase)||!roots.TryGetValue(root,out r))return;
  Dirty(r,path);if(oldPath!=null)Dirty(r,oldPath);r.Retry=1;
  r.Value=new InventoryEntry(r.Path,r.Value.Directory,r.Value.Scanned,r.Value.Members.Select(p=>Under(p.Path,path)||(oldPath!=null&&Under(p.Path,oldPath))?p.Reset():p),r.Value.Availability);
  Publish();
 }}
 internal void WatchError(string root){lock(gate){Root r;if(disposed||!roots.TryGetValue(root,out r))return;r.WatchFailed=true;r.WatchNext=now().AddSeconds(r.Backoff);Dirty(r,root);r.Next=now();}}
 internal void Reconcile(bool reconnect=false){lock(gate){foreach(string path in order){var r=roots[path];Dirty(r,path);r.Next=now();if(reconnect){r.WatchFailed=true;r.WatchNext=now();}}}}
 internal void SetExclusions(IEnumerable<string> names){lock(gate){var next=new HashSet<string>(names,StringComparer.OrdinalIgnoreCase);if(disposed||next.SetEquals(excluded))return;excluded=next;revision++;
  foreach(string path in order){var r=roots[path];r.Value=new InventoryEntry(r.Path,r.Value.Directory,false,r.Value.Members,r.Value.Availability);Dirty(r,path);r.Next=now();}Publish();
 }}
 internal void Tick(){lock(gate){if(disposed)return;var time=now();
  if(exclusions!=null&&!exBusy&&time>=exNext){exBusy=true;Task.Run((Action)ReadExclusions);}
  if(!exLoaded)return;
  foreach(string path in order){var r=roots[path];bool flush=r.Dirty.Count>0&&((time-r.Last).TotalSeconds>=2||(time-r.First).TotalSeconds>=15);
   if(r.Busy||(!flush&&time<r.Next&&time<r.RetryNext))continue;
   var scopes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   if(time>=r.Next)scopes.Add(r.Path);else{
    if(flush)scopes.UnionWith(r.Dirty);
    if(time>=r.RetryNext)scopes.UnionWith(r.Value.Members.Where(p=>p.State!=PhotoState.Ready).Select(p=>p.Path));
   }
   // If a retry is due before the debounce expires, leave unrelated queued events pending.
   bool clear=flush||scopes.Contains(r.Path);
   r.Busy=true;r.During.Clear();if(clear){r.Dirty.Clear();r.First=DateTime.MaxValue;}
   long rev=revision;var prior=r.Value;var skip=new HashSet<string>(excluded,StringComparer.OrdinalIgnoreCase);
   Task.Factory.StartNew(()=>Work(r,rev,prior,skip,scopes),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
  }
 }}
 void ReadExclusions(){try{string[] lines;
   try{PhotoReadPolicy.Demand(fs.Stat(exclusions).Attributes);using(var stream=fs.OpenRead(exclusions))using(var reader=new StreamReader(stream))lines=reader.ReadToEnd().Split('\n');}
   catch(FileNotFoundException){lines=new string[0];}catch(DirectoryNotFoundException){lines=new string[0];}
   SetExclusions(lines.Select(l=>l.Trim().TrimEnd('\\')).Where(l=>l.Length>0&&!l.StartsWith("#")).Select(Path.GetFileName));
  }catch(IOException){}catch(UnauthorizedAccessException){}finally{lock(gate){exBusy=false;exLoaded=true;exNext=now().AddSeconds(2);}}
 }
 void Work(Root r,long rev,InventoryEntry prior,HashSet<string> skip,HashSet<string> scopes){
  DateTime start=now();InventoryEntry result;
  try{
   EnsureWatch(r,prior.Directory);
   result=ScanScopes(r.Path,prior,skip,scopes);
   InventoryAvailability availability;if(result.Availability.TryGetValue(r.Path,out availability)&&availability!=InventoryAvailability.Enumerated)lock(gate){r.WatchFailed=true;r.WatchNext=now().AddSeconds(r.Backoff);}
   EnsureWatch(r,result.Directory);
  }catch(Exception){result=new InventoryEntry(r.Path,prior.Directory,true,prior.Members,new Dictionary<string,InventoryAvailability>{{r.Path,InventoryAvailability.Unknown}});}
  lock(gate){r.Busy=false;if(disposed)return;
   if(rev!=revision||!order.Contains(r.Path,StringComparer.OrdinalIgnoreCase)){r.Next=now();return;} // revision fence
   // Events delivered during enumeration win over that scan, but unrelated additions still publish.
   if(r.During.Count>0){var protectedPaths=r.During.ToArray();Func<string,bool> touched=p=>protectedPaths.Any(d=>Under(p,d));
    result=new InventoryEntry(r.Path,result.Directory,true,result.Members.Where(p=>!touched(p.Path)).Concat(r.Value.Members.Where(p=>touched(p.Path))),result.Availability);
   }
   r.Value=result;bool slowOrUnavailable=result.Availability.Values.Any(a=>a!=InventoryAvailability.Enumerated)||(now()-start).TotalSeconds>5;
   if(scopes.Contains(r.Path)){r.Backoff=slowOrUnavailable?Math.Min(600,r.Backoff*2):30;r.Next=now().AddSeconds(slowOrUnavailable?r.Backoff:r.Watcher==null||r.WatchFailed?60:600);}
   bool pending=result.Members.Any(p=>p.State!=PhotoState.Ready);
   r.RetryNext=pending?now().AddSeconds(r.Retry):DateTime.MaxValue;r.Retry=pending?Math.Min(60,r.Retry*2):1;
   Publish();
  }
 }
 void EnsureWatch(Root r,bool directory){
  bool create;lock(gate){if(disposed||!order.Contains(r.Path,StringComparer.OrdinalIgnoreCase))return;
   if(r.WatchFailed&&r.Watcher!=null){r.Watcher.Dispose();r.Watcher=null;}
   create=directory&&r.Watcher==null&&now()>=r.WatchNext;
  }
  if(!create)return;IDisposable watch=null;
  try{watch=fs.Watch(r.Path,(p,old)=>Notify(r.Path,p,old),()=>WatchError(r.Path));}
  catch{lock(gate){r.WatchFailed=true;r.WatchNext=now().AddSeconds(r.Backoff);}return;}
  lock(gate){if(disposed||!order.Contains(r.Path,StringComparer.OrdinalIgnoreCase)){watch.Dispose();return;}r.Watcher=watch;r.WatchFailed=false;}
 }
 InventoryEntry ScanScopes(string root,InventoryEntry prior,HashSet<string> skip,HashSet<string> scopes){
  prior=new InventoryEntry(prior.Path,prior.Directory,prior.Scanned,prior.Members.Where(p=>!ExcludedPhoto(root,p.Path,skip)),prior.Availability);
  if(scopes.Contains(root))return Scan(root,prior,skip);
  var members=prior.Members.ToList();var states=new Dictionary<string,InventoryAvailability>(prior.Availability,StringComparer.OrdinalIgnoreCase);
  foreach(string path in scopes.Where(p=>Under(p,root)&&!scopes.Any(q=>p!=q&&Under(p,q)))){
   var old=members.Where(p=>Under(p.Path,path)).ToArray();
   var availability=states.Where(p=>Under(p.Key,path)).ToDictionary(p=>p.Key,p=>p.Value,StringComparer.OrdinalIgnoreCase);
   var relative=path.Substring(root.TrimEnd('\\').Length).TrimStart('\\').Split('\\');
   bool excludedPath=relative.Take(Math.Max(0,relative.Length-(Image(path)?1:0))).Any(skip.Contains);
   var part=excludedPath?new InventoryEntry(path,!Image(path),true,new InventoryPhoto[0],new Dictionary<string,InventoryAvailability>()):Scan(path,new InventoryEntry(path,!Image(path),true,old,availability),skip);
   members.RemoveAll(p=>Under(p.Path,path));members.AddRange(part.Members);
   foreach(string key in states.Keys.Where(p=>Under(p,path)).ToArray())states.Remove(key);
   foreach(var kv in part.Availability)states[kv.Key]=kv.Value;
  }
  return new InventoryEntry(root,prior.Directory,prior.Scanned,members,states);
 }
 InventoryEntry Scan(string root,InventoryEntry prior,HashSet<string> skip){
  var members=new List<InventoryPhoto>();var states=new Dictionary<string,InventoryAvailability>(StringComparer.OrdinalIgnoreCase);
  var old=prior.Members.ToDictionary(p=>p.Path,StringComparer.OrdinalIgnoreCase);bool directory=prior.Directory;
  Action<string> unknown=p=>{states[p]=InventoryAvailability.Unknown;var kept=prior.Members.Where(m=>Under(m.Path,p)).ToArray();members.AddRange(kept);
   if(kept.Length==0&&Image(p))members.Add(new InventoryPhoto(p,0,0,PhotoState.Unreadable,now()));};
  PhotoStamp top;
  try{top=fs.Stat(root);directory=top.Directory;}
  catch(FileNotFoundException){return Missing(root,prior);}catch(DirectoryNotFoundException){return Missing(root,prior);}
  catch(IOException){unknown(root);return new InventoryEntry(root,directory,true,members,states);}catch(UnauthorizedAccessException){unknown(root);return new InventoryEntry(root,directory,true,members,states);}
  if(!directory){states[root]=InventoryAvailability.Enumerated;if(Image(root))members.Add(Observe(root,top,old));return new InventoryEntry(root,false,true,members,states);}
  var pending=new Stack<string>();pending.Push(root);
  while(pending.Count>0){string dir=pending.Pop();string[] children;
   try{PhotoReadPolicy.Demand(fs.Stat(dir).Attributes);children=fs.Children(dir);states[dir]=InventoryAvailability.Enumerated;}
   catch(IOException){unknown(dir);continue;}catch(UnauthorizedAccessException){unknown(dir);continue;}
   foreach(string path in children){
    try{var stamp=fs.Stat(path);if(stamp.Directory){if(!skip.Contains(Path.GetFileName(path))){
       // Do not follow directory junctions/symlinks into cycles or outside the selected tree.
       if((stamp.Attributes&FileAttributes.ReparsePoint)!=0){unknown(path);continue;}pending.Push(path);
      }}else if(Image(path))members.Add(Observe(path,stamp,old));
    }catch(IOException){unknown(path);}catch(UnauthorizedAccessException){unknown(path);}
   }
  }
  return new InventoryEntry(root,true,true,members,states);
 }
 InventoryEntry Missing(string root,InventoryEntry prior){
  bool absent=false;try{absent=fs.Stat(Path.GetPathRoot(root)).Directory;}catch{}
  return new InventoryEntry(root,prior.Directory,true,absent?new InventoryPhoto[0]:prior.Members.ToArray(),new Dictionary<string,InventoryAvailability>{{root,absent?InventoryAvailability.Absent:InventoryAvailability.Unknown}});
 }
 InventoryPhoto Observe(string path,PhotoStamp stamp,Dictionary<string,InventoryPhoto> old){
  var time=now();InventoryPhoto before;old.TryGetValue(path,out before);
  if(!PhotoReadPolicy.Local(stamp.Attributes))return new InventoryPhoto(path,stamp.Size,stamp.Ticks,PhotoState.Unreadable,time);
  bool same=before!=null&&before.Size==stamp.Size&&before.Mtime==stamp.Ticks&&before.Observed!=DateTime.MaxValue;
  var observed=same?before.Observed:time;
  try{using(var file=PhotoReadPolicy.Open(fs,path)){
    bool image=Header(file);var after=fs.Stat(path);
    if(after.Size!=stamp.Size||after.Ticks!=stamp.Ticks||!PhotoReadPolicy.Local(after.Attributes))return new InventoryPhoto(path,after.Size,after.Ticks,PhotoState.Settling,time);
    // A quiet interval and recognizable header are a heuristic, not proof that a producer is finished.
    return new InventoryPhoto(path,stamp.Size,stamp.Ticks,image&&same&&(time-observed).TotalSeconds>=1?PhotoState.Ready:PhotoState.Settling,observed);
   }}catch(IOException){return new InventoryPhoto(path,stamp.Size,stamp.Ticks,PhotoState.Unreadable,time);}catch(UnauthorizedAccessException){return new InventoryPhoto(path,stamp.Size,stamp.Ticks,PhotoState.Unreadable,time);}
 }
 static bool Header(Stream stream){var b=new byte[12];int n=0,k;while(n<b.Length&&(k=stream.Read(b,n,b.Length-n))>0)n+=k;
  return n>=3&&b[0]==255&&b[1]==216&&b[2]==255||n>=8&&b[0]==137&&b[1]==80&&b[2]==78&&b[3]==71&&b[4]==13&&b[5]==10&&b[6]==26&&b[7]==10||n>=2&&b[0]==66&&b[1]==77||n>=12&&b[0]==82&&b[1]==73&&b[2]==70&&b[3]==70&&b[8]==87&&b[9]==69&&b[10]==66&&b[11]==80;
 }
 void Publish(){snapshot=new InventorySnapshot(revision,++sequence,order.Select(p=>roots[p].Value));
  if(Changed==null||notificationPending)return;notificationPending=true;
  post(()=>{InventoryChange change;Action<InventoryChange> handler;lock(gate){notificationPending=false;if(disposed)return;change=new InventoryChange(delivered,snapshot);delivered=snapshot;handler=Changed;}if(handler!=null)handler(change);});
 }
 public void Dispose(){lock(gate){if(disposed)return;disposed=true;if(timer!=null)timer.Dispose();foreach(var r in roots.Values)if(r.Watcher!=null)r.Watcher.Dispose();Changed=null;}}
}
