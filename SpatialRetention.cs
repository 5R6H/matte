using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

// v1 only. Location history is local housekeeping, never provenance or proof that a source is absent.
static class SpatialRetention {
 internal const int MissingDays=30;
 internal sealed class Record {public string sourceSha,recipeSha,missingSince;public Dictionary<string,string> locations=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);}
 internal sealed class History {public int version=1;public string lastCompleteScan;public Dictionary<string,Record> pairs=new Dictionary<string,Record>(StringComparer.Ordinal);}
 internal sealed class Result {
  internal int InUse,Missing,Unknown,Unsupported,Quarantined,QuarantinedThisSweep,Recycled,RecycledPairs,RecycledQuarantine;
  internal long MissingBytes;internal DateTime? EarliestClearUtc,LastCompleteScan;internal bool Complete,Skipped,Cancelled;internal DateTime When;
 }
 // Fault seams use the same enumeration/decision code; production always reads fresh sizes and full photo bytes.
 internal sealed class IO {
  internal Func<string,bool> DriveThere=DrivePresent;
  internal Func<string,string[]> Files=Directory.GetFiles,Directories=Directory.GetDirectories;
  internal Func<string,long> Size=p=>new FileInfo(p).Length;
  internal Func<string,string> Hash=HashPhoto;
  internal Func<bool> Cancelled=()=>false;
  internal void Check(){if(Cancelled())throw new OperationCanceledException();}
 }
 sealed class Entry {internal string Source,Recipe,Artifact,Sidecar;internal long SourceSize,Bytes;internal SpatialIdentity.Classification State;internal string Key{get{return Source+"/"+Recipe;}}}
 static readonly Regex Digest=new Regex(@"\A[0-9a-f]{64}\z"),PairFile=new Regex(@"\A([0-9a-f]{64})\.spatial(?:\.json)?\z"),QuarantineFile=new Regex(@"\A([0-9]{8}T[0-9]{6}Z)-[0-9a-f]{64}-[0-9a-f]{64}\.(spatial|json)\z");
 static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
 static string Time(DateTime t){return t.ToUniversalTime().ToString("o",Inv);}
 static DateTime Date(string s){DateTime t;if(s==null||!DateTime.TryParseExact(s,"o",Inv,DateTimeStyles.RoundtripKind,out t)||t.Kind!=DateTimeKind.Utc)throw new FormatException("invalid retention date");return t;}
 static bool DrivePresent(string path){try{string root=Path.GetPathRoot(path);return !string.IsNullOrEmpty(root)&&Directory.Exists(root);}catch{return false;}}
 static string HashPhoto(string path){using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();}
 static bool Image(string path){string e=Path.GetExtension(path).ToLowerInvariant();return e==".jpg"||e==".jpeg"||e==".png"||e==".bmp"||e==".webp";}
 static bool Exists(string path){try{File.GetAttributes(path);return true;}catch(FileNotFoundException){return false;}catch(DirectoryNotFoundException){return false;}}
 static bool Link(string path){return Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0;}
 static void Note(Action<string> log,Exception e){if(log!=null)log("Spatial retention: "+e.Message);}
 internal static History Load(string store,DateTime now){try{
   bool reset=Exists(Path.Combine(store,"v1","retention-reset"));
   string text=File.ReadAllText(Path.Combine(store,"v1","retention.json"),new UTF8Encoding(false,true));var raw=SpatialIdentity.ParseStrict(text) as Dictionary<string,object>;
   if(raw==null||raw.Count!=3||!raw.ContainsKey("version")||!(raw["version"] is SpatialIdentity.Integer)||raw["version"].ToString()!="1"||!raw.ContainsKey("lastCompleteScan")||!raw.ContainsKey("pairs"))throw new FormatException();
   var h=new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Deserialize<History>(text);if(h.pairs==null)throw new FormatException();DateTime? last=h.lastCompleteScan==null?(DateTime?)null:Date(h.lastCompleteScan);if(last>now)throw new FormatException();
   foreach(var kv in h.pairs){var r=kv.Value;if(r==null||!Digest.IsMatch(r.sourceSha??"")||!Digest.IsMatch(r.recipeSha??"")||kv.Key!=r.sourceSha+"/"+r.recipeSha||r.locations==null)throw new FormatException();
    if(r.missingSince!=null&&(!last.HasValue||Date(r.missingSince)>last.Value))throw new FormatException();
    foreach(var p in r.locations){if(!Path.IsPathRooted(p.Key)||!string.Equals(Path.GetFullPath(p.Key),p.Key,StringComparison.OrdinalIgnoreCase)||Date(p.Value)>now)throw new FormatException();}
    r.locations=new Dictionary<string,string>(r.locations,StringComparer.OrdinalIgnoreCase);
   }if(reset)foreach(var rec in h.pairs.Values)rec.missingSince=null;return h;
  }catch{return new History();}}
 static void Save(string store,History history){string path=Path.Combine(store,"v1","retention.json"),temp=SpatialIdentity.TempName(path);Directory.CreateDirectory(Path.GetDirectoryName(path));
  try{byte[] bytes=new UTF8Encoding(false).GetBytes(new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Serialize(history));using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes,0,bytes.Length);file.Flush(true);}
   if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
  }finally{try{File.Delete(temp);}catch{}}
 }
 internal static Result Sweep(string store,IEnumerable<string> roots,Action<string> recycle,Action<string> log,DateTime now,IO io=null,string generatorLock=null,string storeLock=null){
  if(now.Kind!=DateTimeKind.Utc)throw new ArgumentException("UTC required","now");var result=new Result{When=now};SpatialStoreLock run;
  // Global order, held through inventory, evidence, publication of clocks and all mutations. No work on a busy run.
  try{run=SpatialStoreLock.Acquire(generatorLock??SpatialStoreLock.GeneratorPath,0);}catch(TimeoutException){result.Skipped=true;return result;}
  using(run){SpatialStoreLock held;try{held=SpatialStoreLock.Acquire(storeLock??SpatialStoreLock.DefaultPath,0);}catch(TimeoutException){result.Skipped=true;return result;}
   using(held){try{return SweepLocked(store,roots,recycle,log,now,io??new IO(),result);}
    catch(OperationCanceledException){result.Complete=false;result.Cancelled=true;result.Unknown+=result.Missing;result.Missing=0;result.MissingBytes=0;result.EarliestClearUtc=null;
     var history=Load(store,now);foreach(var rec in history.pairs.Values)rec.missingSince=null;
     result.LastCompleteScan=history.lastCompleteScan==null?(DateTime?)null:Date(history.lastCompleteScan);
     try{Save(store,history);}catch(OperationCanceledException){throw;}catch(Exception ex){Note(log,ex);}return result;}}}
 }
 static List<Entry> Inventory(string store,IO io,Result result,Action<string> log){var entries=new List<Entry>();string root=Path.Combine(store,"v1");
  try{if(!Exists(root))return entries;if(Link(root)){result.Complete=false;result.Unknown++;return entries;}
   foreach(string dir in io.Directories(root)){io.Check();string source=Path.GetFileName(dir);if(!Digest.IsMatch(source))continue;if(Link(dir)){result.Complete=false;result.Unknown++;continue;}
    try{var names=new HashSet<string>(StringComparer.Ordinal);foreach(string file in io.Files(dir)){var match=PairFile.Match(Path.GetFileName(file));if(match.Success)names.Add(match.Groups[1].Value);}
     foreach(string recipe in names){io.Check();var e=new Entry{Source=source,Recipe=recipe,Artifact=Path.Combine(dir,recipe+".spatial"),Sidecar=Path.Combine(dir,recipe+".spatial.json")};
      try{if(Link(e.Artifact)||Link(e.Sidecar))e.State=SpatialIdentity.Classification.Unreadable;
       else if(!Exists(e.Sidecar))e.State=SpatialIdentity.Classification.Invalid;
       else{SpatialIdentity.Pair pair;e.State=SpatialIdentity.Classify(e.Sidecar,false,out pair);if(e.State==SpatialIdentity.Classification.Valid){var d=SpatialIdentity.ValidateSidecar(SpatialIdentity.ParseStrict(File.ReadAllText(e.Sidecar)));e.SourceSize=long.Parse(((Dictionary<string,object>)d["source"])["size"].ToString(),Inv);e.Bytes=new FileInfo(e.Artifact).Length;}}
      }catch(OperationCanceledException){throw;}catch(Exception ex){Note(log,ex);e.State=SpatialIdentity.Classification.Unreadable;}
      if(e.State==SpatialIdentity.Classification.Unreadable){result.Complete=false;result.Unknown++;}else if(e.State==SpatialIdentity.Classification.Unsupported){result.Unsupported++;result.Unknown++;}entries.Add(e);
     }
    }catch(OperationCanceledException){throw;}catch(Exception ex){Note(log,ex);result.Complete=false;result.Unknown++;}
   }
  }catch(OperationCanceledException){throw;}catch(Exception ex){Note(log,ex);result.Complete=false;result.Unknown++;}return entries;
 }
 static HashSet<string> Photos(IEnumerable<string> roots,History history,IO io,Result result,Action<string> log){var photos=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var dirs=new Stack<string>();
  foreach(string raw in roots){io.Check();try{string path=Path.GetFullPath(raw);if(!io.DriveThere(path)){result.Complete=false;continue;}var attr=File.GetAttributes(path);if((attr&FileAttributes.Directory)!=0)dirs.Push(path);else if(Image(path))photos.Add(path);
   }catch(OperationCanceledException){throw;}catch(Exception ex){Note(log,ex);result.Complete=false;}}
  while(dirs.Count>0){io.Check();string dir=dirs.Pop();if(!seen.Add(dir))continue;try{if(!io.DriveThere(dir)||Link(dir)){result.Complete=false;continue;}
    foreach(string file in io.Files(dir))if(Image(file))photos.Add(Path.GetFullPath(file));foreach(string sub in io.Directories(dir))dirs.Push(sub);
   }catch(OperationCanceledException){throw;}catch(Exception ex){Note(log,ex);result.Complete=false;}}
  foreach(var rec in history.pairs.Values)foreach(string path in rec.locations.Keys)photos.Add(path);return photos;
 }
 static Result SweepLocked(string store,IEnumerable<string> roots,Action<string> recycle,Action<string> log,DateTime now,IO io,Result result){
  result.Complete=true;var history=Load(store,now);result.LastCompleteScan=history.lastCompleteScan==null?(DateTime?)null:Date(history.lastCompleteScan);
  var previous=history.pairs.ToDictionary(k=>k.Key,k=>k.Value.missingSince,StringComparer.Ordinal);
  // Persist reset first. A crash or an unreadable/writable history cannot preserve deletion authority from an older scan.
  foreach(var rec in history.pairs.Values)rec.missingSince=null;
  try{if(Link(Path.Combine(store,"v1"))){result.Complete=false;result.Unknown++;return result;}
   string reset=Path.Combine(store,"v1","retention-reset");if(Link(reset))throw new IOException("retention reset is a link");Directory.CreateDirectory(Path.GetDirectoryName(reset));
   // A locked retention.json may prevent replacement. This marker survives so a later scan cannot reuse its old clocks.
   using(var marker=new FileStream(reset,FileMode.Create,FileAccess.Write,FileShare.None)){marker.WriteByte(1);marker.Flush(true);}
   Save(store,history);File.Delete(reset);
  }catch(OperationCanceledException){throw;}catch(Exception ex){Note(log,ex);result.Complete=false;result.Unknown++;return result;}
  var entries=Inventory(store,io,result,log);var valid=entries.Where(e=>e.State==SpatialIdentity.Classification.Valid).ToList();
  foreach(var e in valid)if(!history.pairs.ContainsKey(e.Key))history.pairs[e.Key]=new Record{sourceSha=e.Source,recipeSha=e.Recipe};
  var photos=Photos(roots,history,io,result,log);var matches=new Dictionary<string,List<string>>(StringComparer.Ordinal);
  // Reuse the catalogue's size/mtime/file-id/volume checks. A stale positive can only delay cleanup.
  var catalog=SpatialCatalog.Load(store,photos,null,p=>{io.Check();return SpatialCatalog.Probe(p);});
  var sources=new HashSet<string>(valid.Select(e=>e.Source),StringComparer.Ordinal);
  foreach(var p in catalog.Photos){io.Check();if(p.Value.Status==SpatialCatalog.State.Hinted&&sources.Contains(p.Value.Digest)&&io.DriveThere(p.Key))Match(matches,p.Value.Digest,p.Key);}
  // In-use pairs need no artifact read either. Fully classify unresolved pairs before cleanup decisions.
  foreach(var e in valid.Where(e=>!matches.ContainsKey(e.Source))){io.Check();e.State=Recheck(e);if(e.State==SpatialIdentity.Classification.Unreadable){result.Complete=false;result.Unknown++;}else if(e.State==SpatialIdentity.Classification.Unsupported){result.Unsupported++;result.Unknown++;}}
  valid=valid.Where(e=>e.State==SpatialIdentity.Classification.Valid).ToList();
  var sizes=new HashSet<long>(valid.Where(e=>!matches.ContainsKey(e.Source)).Select(e=>e.SourceSize));
  foreach(string photo in photos){io.Check();try{if(!io.DriveThere(photo)){result.Complete=false;continue;}long size;
    try{size=io.Size(photo);}catch(FileNotFoundException){if(!io.DriveThere(photo))result.Complete=false;continue;}catch(DirectoryNotFoundException){if(!io.DriveThere(photo))result.Complete=false;continue;}
    // A hint never excludes this path: it may now contain a different unresolved source of the same size.
    if(!sizes.Contains(size))continue;string hash=io.Hash(photo);if(!Digest.IsMatch(hash??""))throw new IOException("invalid photo hash");Match(matches,hash,photo);
   }catch(OperationCanceledException){throw;}catch(Exception ex){Note(log,ex);result.Complete=false;}}
  // Cancellation ends at this commit boundary. Finish artifact/sidecar actions together under both locks;
  // a generation requested during commit still waits for lock release, never an interrupted half-pair.
  io.Check();
  foreach(var e in valid){var rec=history.pairs[e.Key];List<string> locations;if(matches.TryGetValue(e.Source,out locations)){foreach(string p in locations)rec.locations[p]=Time(now);rec.missingSince=null;}
   else if(result.Complete){string old;rec.missingSince=previous.TryGetValue(e.Key,out old)&&old!=null?old:Time(now);}
  }
  if(result.Complete){history.lastCompleteScan=Time(now);result.LastCompleteScan=now;}else foreach(var rec in history.pairs.Values)rec.missingSince=null;
  foreach(var e in valid){var rec=history.pairs[e.Key];if(matches.ContainsKey(e.Source)){result.InUse++;continue;}if(!result.Complete){result.Unknown++;continue;}
   DateTime eligible=Date(rec.missingSince).AddDays(MissingDays);
   if(now>=eligible&&Recheck(e)==SpatialIdentity.Classification.Valid&&!matches.ContainsKey(e.Source)&&RecyclePair(e.Artifact,e.Sidecar,recycle,log)){result.Recycled++;result.RecycledPairs++;rec.missingSince=null;continue;}
   result.Missing++;result.MissingBytes+=e.Bytes;if(!result.EarliestClearUtc.HasValue||eligible<result.EarliestClearUtc.Value)result.EarliestClearUtc=eligible;
  }
  // Unknown photo evidence does not grant deletion. Invalid/orphan store files can still be isolated, without destruction.
  foreach(var e in entries.Where(e=>e.State==SpatialIdentity.Classification.Invalid)){try{if(Recheck(e)==SpatialIdentity.Classification.Invalid){Quarantine(store,e,now);result.QuarantinedThisSweep++;}}
   catch(OperationCanceledException){throw;}catch(Exception ex){Note(log,ex);result.Unknown++;}}
  AgeQuarantine(store,now,recycle,log,result);
  // Commit surviving clocks last. An interruption during recycling leaves the durable reset from the start.
  try{Save(store,history);}catch(OperationCanceledException){throw;}catch(Exception ex){Note(log,ex);result.Complete=false;result.Unknown+=result.Missing;result.Missing=0;result.MissingBytes=0;result.EarliestClearUtc=null;result.LastCompleteScan=null;}
  return result;
 }
 static void Match(Dictionary<string,List<string>> matches,string hash,string photo){List<string> paths;if(!matches.TryGetValue(hash,out paths))matches[hash]=paths=new List<string>();paths.Add(photo);}
 static SpatialIdentity.Classification Recheck(Entry e){if(Link(e.Artifact)||Link(e.Sidecar))return SpatialIdentity.Classification.Unreadable;return Exists(e.Sidecar)?SpatialIdentity.Classify(e.Sidecar,true):SpatialIdentity.Classification.Invalid;}
 static bool RecyclePair(string artifact,string sidecar,Action<string> recycle,Action<string> log){try{
   // Artifact first: the player's no-delete sharing handle must leave its sidecar untouched too.
   if(Exists(artifact)){recycle(artifact);if(Exists(artifact))return false;}
   if(Exists(sidecar)){recycle(sidecar);if(Exists(sidecar))return false;}return true;
  }catch(OperationCanceledException){throw;}catch(Exception ex){Note(log,ex);return false;}}
 static void Quarantine(string store,Entry e,DateTime now){string dest=null,json=null;string folder=Path.Combine(store,"v1","quarantine");if(Link(folder))throw new IOException("quarantine is a link");Directory.CreateDirectory(folder);
  for(int n=0;n<86400;n++){string candidate=SpatialIdentity.QuarantinePath(store,e.Source,e.Recipe,now.AddSeconds(n));string side=Path.ChangeExtension(candidate,".json");if(!Exists(candidate)&&!Exists(side)){dest=candidate;json=side;break;}}
  if(dest==null)throw new IOException("quarantine names exhausted");bool moved=false;
  try{if(Exists(e.Artifact)){File.Move(e.Artifact,dest);moved=true;}if(Exists(e.Sidecar))File.Move(e.Sidecar,json);}
  catch{if(moved)try{File.Move(dest,e.Artifact);}catch{}throw;}
 }
 static void AgeQuarantine(string store,DateTime now,Action<string> recycle,Action<string> log,Result result){string folder=Path.Combine(store,"v1","quarantine");try{
   if(!Exists(folder))return;if(Link(folder)){result.Unknown++;return;}var groups=new HashSet<string>(StringComparer.Ordinal);
   foreach(string file in Directory.GetFiles(folder)){var m=QuarantineFile.Match(Path.GetFileName(file));if(!m.Success){result.Quarantined++;continue;}groups.Add(Path.Combine(folder,Path.GetFileNameWithoutExtension(file)));}
   foreach(string group in groups){string artifact=group+".spatial",sidecar=group+".json";DateTime created;
    if(!DateTime.TryParseExact(Path.GetFileName(group).Substring(0,16),"yyyyMMdd'T'HHmmss'Z'",Inv,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out created)){result.Quarantined++;continue;}
    if(Exists(sidecar)&&SpatialIdentity.Classify(sidecar,false)==SpatialIdentity.Classification.Unsupported){result.Quarantined++;continue;}
    if(!Link(artifact)&&!Link(sidecar)&&now>=created.AddDays(MissingDays)&&RecyclePair(artifact,sidecar,recycle,log)){result.Recycled++;result.RecycledQuarantine++;}else result.Quarantined++;
   }
  }catch(OperationCanceledException){throw;}catch(Exception ex){Note(log,ex);result.Unknown++;}}
}
