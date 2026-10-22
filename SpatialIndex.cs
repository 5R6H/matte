using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

// Legacy inventory only: name/last path are status hints, never proof of identity. Pictures are never renamed
// or recycled here. The top-level spatial-index.json remains version 1; old fingerprint/photo fields are ignored.
// v1 has its own identity and retention contract and is never enumerated by this sweep.
static class SpatialIndex {
 const string FileName="spatial-index.json";
 internal class Record{public string path;public string missingSince;}
 internal class Index{public int version=1;public Dictionary<string,Record> pictures=new Dictionary<string,Record>(StringComparer.OrdinalIgnoreCase);}
 internal class Result{public int InUse,Offline,Missing;public long MissingBytes;public DateTime When=DateTime.Now;}

 // Names used twice are left out, as in the legacy catalogue.
 internal static Result Sweep(string cache,IEnumerable<string> photos,Action<string> log,DateTime now){
  var r=new Result{When=now};if(!Directory.Exists(cache))return r;
  var idx=Load(cache);
  var byName=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var twice=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var p in photos){string s=Path.GetFileNameWithoutExtension(p);if(byName.ContainsKey(s)){if(!string.Equals(byName[s],p,StringComparison.OrdinalIgnoreCase))twice.Add(s);}else byName[s]=p;}
  foreach(var s in twice)byName.Remove(s);
  var pictures=new DirectoryInfo(cache).GetFiles("*.spatial",SearchOption.TopDirectoryOnly).ToDictionary(f=>Path.GetFileNameWithoutExtension(f.Name),f=>f,StringComparer.OrdinalIgnoreCase);
  foreach(var kv in pictures){string stem=kv.Key;Record rec;idx.pictures.TryGetValue(stem,out rec);
   string path;
   if(byName.TryGetValue(stem,out path)||(rec!=null&&rec.path!=null&&File.Exists(rec.path)&&(path=rec.path)!=null)){
    idx.pictures[stem]=new Record{path=path};r.InUse++;continue;
   }
   if(rec==null)rec=new Record();
   if(rec.path!=null&&!DriveThere(rec.path)){rec.missingSince=null;idx.pictures[stem]=rec;r.Offline++;continue;}
   // Missing is status only, even after many years. Only the owner can clear a legacy picture.
   if(rec.missingSince==null)rec.missingSince=now.ToString("o");idx.pictures[stem]=rec;
   r.Missing++;r.MissingBytes+=kv.Value.Length;
  }
  foreach(var k in idx.pictures.Keys.Where(k=>!File.Exists(Path.Combine(cache,k+".spatial"))).ToList())idx.pictures.Remove(k);
  Save(cache,idx);return r;
 }
 static bool DriveThere(string path){try{string root=Path.GetPathRoot(path);return !string.IsNullOrEmpty(root)&&Directory.Exists(root);}catch{return false;}}
 static Index Load(string cache){
  try{string p=Path.Combine(cache,FileName);if(File.Exists(p)){var i=new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Deserialize<Index>(File.ReadAllText(p));
    if(i!=null){i.pictures=new Dictionary<string,Record>(i.pictures??new Dictionary<string,Record>(),StringComparer.OrdinalIgnoreCase);return i;}}}
  catch{}
  return new Index();
 }
 static void Save(string cache,Index i){
  string p=Path.Combine(cache,FileName),tmp=p+".tmp";File.WriteAllText(tmp,new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Serialize(i));
  if(File.Exists(p)){File.SetAttributes(p,FileAttributes.Normal);File.Replace(tmp,p,null);}else File.Move(tmp,p);
  try{File.SetAttributes(p,FileAttributes.Hidden);}catch{}   // it names the photos' paths: kept out of sight in the folder
 }
}
