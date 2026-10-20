using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

// Which photo each Spatial picture belongs to, so they can be kept, re-attached and cleared out without asking.
// A Spatial picture is matched to its photo by file name (the generator's rule), which breaks when
// a photo is renamed and cannot tell "deleted" from "not on the playlist right now". So the store keeps an index
// (spatial-index.json, next to the pictures) with, per picture: the photo's last known path,
// a fingerprint of its content (size + the first and last 64 KB: unchanged by renaming or moving, changed by editing,
// which needs a new picture anyway) and since when the photo has been missing.
// The sweep (Sweep) sorts every picture into:
//  in use      the photo is found (by name on the playlist, or still at its last path)
//  re-attached the photo is gone from its path, but a photo with the same fingerprint and no picture of its own is
//              found under another name: the picture is renamed to it (no new generation needed)
//  offline     the photo's drive is not there (an external disk unplugged): kept, no clock running
//  missing     the photo is gone and nowhere to be found: kept for MissingDays, then to the Recycle Bin
static class SpatialIndex {
 internal const int MissingDays=30;
 const string FileName="spatial-index.json";
 internal class Record{public string path;public string fp;public string missingSince;}
 internal class Photo{public long size;public long ticks;public string fp;}
 internal class Index{public int version=1;public Dictionary<string,Record> pictures=new Dictionary<string,Record>(StringComparer.OrdinalIgnoreCase);public Dictionary<string,Photo> photos=new Dictionary<string,Photo>(StringComparer.OrdinalIgnoreCase);}
 internal class Result{public int InUse,Reattached,Offline,Missing,Cleaned;public long MissingBytes;public DateTime When=DateTime.Now;}

 // The photos it can see: name → path (names used twice are left out, as the catalogue does).
 internal static Result Sweep(string cache,IEnumerable<string> photos,Action<string> log,Action<List<string>> recycle,DateTime now){
  var r=new Result();if(!Directory.Exists(cache))return r;
  var idx=Load(cache);
  var byName=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var twice=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var p in photos){string s=Path.GetFileNameWithoutExtension(p);if(byName.ContainsKey(s)){if(!string.Equals(byName[s],p,StringComparison.OrdinalIgnoreCase))twice.Add(s);}else byName[s]=p;}
  foreach(var s in twice)byName.Remove(s);
  var pictures=new DirectoryInfo(cache).GetFiles("*.spatial").ToDictionary(f=>Path.GetFileNameWithoutExtension(f.Name),f=>f,StringComparer.OrdinalIgnoreCase);
  var seenPhotos=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  Func<string,string> fpOf=path=>{seenPhotos.Add(path);var fi=new FileInfo(path);Photo ph;
   if(idx.photos.TryGetValue(path,out ph)&&ph.size==fi.Length&&ph.ticks==fi.LastWriteTimeUtc.Ticks)return ph.fp;
   string fp=Fingerprint(path);idx.photos[path]=new Photo{size=fi.Length,ticks=fi.LastWriteTimeUtc.Ticks,fp=fp};return fp;};
  // 1. pictures whose photo is found by name, or still at its last path
  var lost=new List<string>();
  foreach(var kv in pictures){string stem=kv.Key;Record rec;idx.pictures.TryGetValue(stem,out rec);
   string path;
   if(byName.TryGetValue(stem,out path)||(rec!=null&&rec.path!=null&&File.Exists(rec.path)&&(path=rec.path)!=null)){
    try{idx.pictures[stem]=new Record{path=path,fp=fpOf(path)};r.InUse++;}catch(Exception e){log("Spatial index: a photo could not be read: "+e.GetType().Name);r.InUse++;}
   }else lost.Add(stem);
  }
  // 2. the others: renamed (same fingerprint under a name with no picture), offline, or missing
  Dictionary<string,string> byFp=null;
  foreach(var stem in lost){
   Record rec;idx.pictures.TryGetValue(stem,out rec);if(rec==null)rec=new Record();
   if(rec.fp!=null){
    if(byFp==null){byFp=new Dictionary<string,string>();
     foreach(var kv in byName)if(!pictures.ContainsKey(kv.Key))try{var fp=fpOf(kv.Value);if(!byFp.ContainsKey(fp))byFp[fp]=kv.Value;}catch{}}
    string to;
    if(byFp.TryGetValue(rec.fp,out to)){string newStem=Path.GetFileNameWithoutExtension(to),dest=Path.Combine(cache,newStem+".spatial");
     if(!File.Exists(dest)){try{File.Move(pictures[stem].FullName,dest);idx.pictures.Remove(stem);idx.pictures[newStem]=new Record{path=to,fp=rec.fp};byFp.Remove(rec.fp);r.Reattached++;
       continue;}catch(Exception e){log("Spatial index: a re-attach failed: "+e.GetType().Name);}}}
   }
   if(rec.path!=null&&!DriveThere(rec.path)){rec.missingSince=null;idx.pictures[stem]=rec;r.Offline++;continue;}
   // missing (also a picture made before the index with no photo found: its clock starts now)
   if(rec.missingSince==null)rec.missingSince=now.ToString("o");idx.pictures[stem]=rec;
   DateTime since;if(DateTime.TryParse(rec.missingSince,null,System.Globalization.DateTimeStyles.RoundtripKind,out since)&&(now-since).TotalDays>=MissingDays){
    try{recycle(new List<string>{pictures[stem].FullName});idx.pictures.Remove(stem);r.Cleaned++;continue;}
    catch(Exception e){log("Spatial index: a clear failed: "+e.GetType().Name);}}
   r.Missing++;r.MissingBytes+=pictures[stem].Length;
  }
  // forget pictures that are gone and photos no longer seen
  foreach(var k in idx.pictures.Keys.Where(k=>!File.Exists(Path.Combine(cache,k+".spatial"))).ToList())idx.pictures.Remove(k);
  foreach(var k in idx.photos.Keys.Where(k=>!seenPhotos.Contains(k)).ToList())idx.photos.Remove(k);
  Save(cache,idx);
  return r;
 }
 // the drive (or share) the photo was on is not there at all: a disk unplugged, not a photo deleted
 static bool DriveThere(string path){try{string root=Path.GetPathRoot(path);return !string.IsNullOrEmpty(root)&&Directory.Exists(root);}catch{return false;}}
 internal static string Fingerprint(string path){
  using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var sha=SHA1.Create()){
   const int Chunk=65536;long len=f.Length;var head=new byte[Math.Min(Chunk,len)];Read(f,head);
   var tail=new byte[len>Chunk?Math.Min(Chunk,len-Chunk):0];if(tail.Length>0){f.Seek(len-tail.Length,SeekOrigin.Begin);Read(f,tail);}
   var all=new byte[8+head.Length+tail.Length];BitConverter.GetBytes(len).CopyTo(all,0);head.CopyTo(all,8);tail.CopyTo(all,8+head.Length);
   return BitConverter.ToString(sha.ComputeHash(all),0,10).Replace("-","");}
 }
 static void Read(Stream s,byte[] b){int o=0;while(o<b.Length){int n=s.Read(b,o,b.Length-o);if(n<=0)break;o+=n;}}
 static Index Load(string cache){
  try{string p=Path.Combine(cache,FileName);if(File.Exists(p)){var i=new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Deserialize<Index>(File.ReadAllText(p));
    if(i!=null){i.pictures=new Dictionary<string,Record>(i.pictures??new Dictionary<string,Record>(),StringComparer.OrdinalIgnoreCase);i.photos=new Dictionary<string,Photo>(i.photos??new Dictionary<string,Photo>(),StringComparer.OrdinalIgnoreCase);return i;}}}
  catch{}
  return new Index();
 }
 static void Save(string cache,Index i){
  string p=Path.Combine(cache,FileName),tmp=p+".tmp";File.WriteAllText(tmp,new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Serialize(i));
  if(File.Exists(p)){File.SetAttributes(p,FileAttributes.Normal);File.Replace(tmp,p,null);}else File.Move(tmp,p);
  try{File.SetAttributes(p,FileAttributes.Hidden);}catch{}   // it names the photos' paths: kept out of sight in the folder
 }
}
