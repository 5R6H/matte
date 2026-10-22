using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

// A digest belongs to a particular catalogue snapshot, never to a pathname. Verification replaces the hint
// with the hash of the held artifact. Keeping this outside Pair also keeps identity/recipe selection unchanged.
static class SpatialContent {
 sealed class Value {public Value(){}internal string Digest;}
 static readonly ConditionalWeakTable<SpatialIdentity.Pair,Value> digests=new ConditionalWeakTable<SpatialIdentity.Pair,Value>();
 internal static void Set(SpatialIdentity.Pair pair,string digest){digests.GetOrCreateValue(pair).Digest=digest;}
 internal static string Digest(SpatialIdentity.Pair pair){if(pair==null)return null;Value v;return digests.TryGetValue(pair,out v)?v.Digest:null;}
 internal static string Read(SpatialIdentity.Pair pair){try{var m=SpatialIdentity.ValidateSidecar(SpatialIdentity.ParseStrict(File.ReadAllText(pair.Artifact+".json",new UTF8Encoding(false,true))));
   string digest=(string)((Dictionary<string,object>)m["artifact"])["sha256"];Set(pair,digest);return digest;}catch{return null;}}
}

static class SpatialBad {
 internal sealed class Record {public string sourceSha,recipeSha,name,reason;public List<string> aliases;}
 internal sealed class FileData {public int version=1;public Dictionary<string,Record> artifacts=new Dictionary<string,Record>(StringComparer.Ordinal);}
 static readonly Regex Sha=new Regex(@"\A[0-9a-f]{64}\z");
 internal static Dictionary<string,Record> Load(string store){try{
   string text=File.ReadAllText(Path.Combine(store,"v1","bad.json"),new UTF8Encoding(false,true));
   var raw=SpatialIdentity.ParseStrict(text) as Dictionary<string,object>;if(raw==null||!raw.ContainsKey("version")||!(raw["version"] is SpatialIdentity.Integer)||raw["version"].ToString()!="1"||!raw.ContainsKey("artifacts"))throw new FormatException();var data=new JavaScriptSerializer().Deserialize<FileData>(text);
   if(data==null||data.version!=1||data.artifacts==null)throw new FormatException();
   foreach(var kv in data.artifacts){if(!Sha.IsMatch(kv.Key)||kv.Value==null||!Sha.IsMatch(kv.Value.sourceSha??"")||!Sha.IsMatch(kv.Value.recipeSha??""))throw new FormatException();
    if(kv.Value.aliases!=null)foreach(string alias in kv.Value.aliases){var parts=(alias??"").Split('/');if(parts.Length!=2||!Sha.IsMatch(parts[0])||!Sha.IsMatch(parts[1]))throw new FormatException();}}
   return data.artifacts;
  }catch{return new Dictionary<string,Record>(StringComparer.Ordinal);}}
 internal static Record For(SpatialIdentity.Pair pair,string name,string reason){return new Record{sourceSha=pair.SourceSha,recipeSha=pair.RecipeSha,name=Path.GetFileName(name),reason=reason};}
 internal static IEnumerable<string> Keys(Record r){return new[]{r.sourceSha+"/"+r.recipeSha}.Concat(r.aliases??new List<string>());}
 // Caller holds store.lock. Merge with the latest disk state; publish one complete JSON file atomically.
 internal static void SaveLocked(string store,IDictionary<string,Record> records){if(records.Count==0)return;var data=new FileData{artifacts=Load(store)};foreach(var kv in records){Record previous;var r=kv.Value;data.artifacts.TryGetValue(kv.Key,out previous);
   data.artifacts[kv.Key]=new Record{sourceSha=r.sourceSha,recipeSha=r.recipeSha,name=r.name,reason=r.reason,aliases=Keys(r).Concat(previous==null?new string[0]:Keys(previous)).Distinct(StringComparer.Ordinal).ToList()};}
  string path=Path.Combine(store,"v1","bad.json"),temp=SpatialIdentity.TempName(path);Directory.CreateDirectory(Path.GetDirectoryName(path));
  try{byte[] bytes=new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(data));using(var f=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(bytes,0,bytes.Length);f.Flush(true);}
   if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
  }finally{try{File.Delete(temp);}catch{}}
 }
 internal static void Save(string store,IDictionary<string,Record> records,string lockPath=null){using(SpatialStoreLock.Acquire(lockPath??SpatialStoreLock.DefaultPath,3000))SaveLocked(store,records);}
 // Both locks, in global order; a live generator or a held scene defers isolation to the next sweep.
 internal static int Quarantine(string store,Action<string> log,string generatorLock=null,string storeLock=null){try{
   using(SpatialStoreLock.Acquire(generatorLock??SpatialStoreLock.GeneratorPath,0))using(SpatialStoreLock.Acquire(storeLock??SpatialStoreLock.DefaultPath,0))return QuarantineLocked(store,DateTime.UtcNow,log);
  }catch(Exception e){if(log!=null)log("Spatial quarantine deferred: "+e.Message);return 0;}}
 internal static int QuarantineLocked(string store,DateTime now,Action<string> log){int count=0;var bad=Load(store);string root=Path.Combine(store,"v1");if(bad.Count==0||!Directory.Exists(root)||(File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)return 0;
  foreach(string dir in Directory.GetDirectories(root)){if(!Sha.IsMatch(Path.GetFileName(dir))||(File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0)continue;
   foreach(string side in Directory.GetFiles(dir,"*.spatial.json")){try{SpatialIdentity.Pair pair;if(SpatialIdentity.Classify(side,false,out pair)!=SpatialIdentity.Classification.Valid)continue;
     string digest=SpatialContent.Read(pair);if(digest!=null&&bad.ContainsKey(digest)){
      // One artifact can be shared by different source/recipe pairs. Keep their repair counts after isolation.
      if(!Keys(bad[digest]).Contains(pair.SourceSha+"/"+pair.RecipeSha)){if(bad[digest].aliases==null)bad[digest].aliases=new List<string>();bad[digest].aliases.Add(pair.SourceSha+"/"+pair.RecipeSha);SaveLocked(store,bad);}
      if(SpatialRetention.QuarantineBadLocked(store,pair,digest,now))count++;
     }
    }catch(Exception e){if(log!=null)log("Spatial quarantine deferred: "+e.Message);}}
  }return count;
 }
}

// UI-thread session policy. Runtime errors get one further attempt; only content records reach bad.json.
sealed class SpatialRejections {
 internal readonly Dictionary<string,SpatialBad.Record> Content=new Dictionary<string,SpatialBad.Record>(StringComparer.Ordinal);
 readonly Dictionary<string,int> runtime=new Dictionary<string,int>(StringComparer.Ordinal);
 internal bool Contains(string digest){int n;return digest!=null&&(Content.ContainsKey(digest)||(runtime.TryGetValue(digest,out n)&&n>=2));}
 internal void Failed(string digest,bool content,SpatialBad.Record record){if(digest==null)return;if(content)Content[digest]=record;else{int n;runtime.TryGetValue(digest,out n);runtime[digest]=n+1;}}
}

sealed class SpatialLoad {
 internal readonly object Host;internal readonly int Sequence;internal readonly long Request;
 internal readonly SpatialIdentity.Pair Pair;internal readonly SlideSnapshot Image;internal readonly string Digest;
 internal SpatialLoad(object host,int sequence,SpatialVerification verified){Host=host;Sequence=sequence;Request=verified.Request;Pair=verified.Pair;Image=verified.Image;Digest=verified.ArtifactDigest??SpatialContent.Digest(Pair);}
 internal bool Matches(SpatialLoad current,long request){return current!=null&&ReferenceEquals(Host,current.Host)&&Sequence==current.Sequence&&Request==request&&Request==current.Request;}
}
