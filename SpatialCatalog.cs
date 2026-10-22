using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

// Rebuildable hints, never proof of the bytes a player will consume. T2c2 must verify every attach.
sealed class SpatialCatalog {
 internal class Hint {
  public long size{get;set;}public long mtimeTicks{get;set;}public string fileId{get;set;}public string volumeSerial{get;set;}public string sha256{get;set;}
 }
 class Index {public int version{get;set;}public Dictionary<string,Hint> photos{get;set;}}
 internal enum State{Hinted,Unchecked,Error}
 internal sealed class Photo {internal State Status;internal string Digest;internal SpatialIdentity.Pair Pair;internal bool Needs;}
 internal readonly Dictionary<string,Photo> Photos=new Dictionary<string,Photo>(StringComparer.OrdinalIgnoreCase);
 internal readonly Dictionary<string,List<SpatialIdentity.Pair>> Pairs=new Dictionary<string,List<SpatialIdentity.Pair>>(StringComparer.Ordinal);
 internal int Valid,Unsupported,Invalid,Unreadable,Legacy;internal long Bytes;
 internal readonly string Store,Desired;
 Dictionary<string,Hint> hints;readonly Func<string,Hint> probe;
 readonly HashSet<string> hashErrors=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
 static readonly Regex Digest=new Regex(@"\A[0-9a-f]{64}\z");
 static readonly Regex PairName=new Regex(@"\A[0-9a-f]{64}\.spatial\.json\z");
 [StructLayout(LayoutKind.Sequential)]struct Info{internal uint Attributes;internal System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written;internal uint Volume,SizeHigh,SizeLow,Links,IdHigh,IdLow;}
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandle(SafeFileHandle file,out Info info);
 static Hint Stamp(FileStream file){Info i;if(!GetFileInformationByHandle(file.SafeFileHandle,out i))return null;
  long time=((long)(uint)i.Written.dwHighDateTime<<32)|(uint)i.Written.dwLowDateTime;
  return new Hint{size=((long)i.SizeHigh<<32)|i.SizeLow,mtimeTicks=DateTime.FromFileTimeUtc(time).Ticks,fileId=i.IdHigh.ToString("x8")+i.IdLow.ToString("x8"),volumeSerial=i.Volume.ToString("x8")};}
 internal static Hint Probe(string path){using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))return Stamp(f);}
 static bool Same(Hint a,Hint b){return a!=null&&b!=null&&a.fileId!=null&&a.volumeSerial!=null&&b.fileId!=null&&b.volumeSerial!=null&&a.size==b.size&&a.mtimeTicks==b.mtimeTicks&&a.fileId==b.fileId&&a.volumeSerial==b.volumeSerial;}
 static string Hash(Stream stream){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
 SpatialCatalog(string store,string desired,Func<string,Hint> stat){Store=store;Desired=desired;probe=stat??Probe;}
 internal static SpatialCatalog Load(string store,IEnumerable<string> photos,string desired,Func<string,Hint> stat=null){
  return Read(store,photos,desired,stat,null);
 }
 static SpatialCatalog Read(string store,IEnumerable<string> photos,string desired,Func<string,Hint> stat,Dictionary<string,Hint> known){
  var c=new SpatialCatalog(store,desired,stat);if(known==null)c.ReadIndex();else c.hints=new Dictionary<string,Hint>(known,StringComparer.OrdinalIgnoreCase);c.Inventory();
  foreach(string path in photos.Distinct(StringComparer.OrdinalIgnoreCase)){
   var p=new Photo{Status=State.Unchecked,Needs=true};c.Photos[path]=p;
   try{Hint h;var stamp=c.probe(path);if(c.hints.TryGetValue(path,out h)&&Same(h,stamp)&&Digest.IsMatch(h.sha256??"")){
     p.Status=State.Hinted;p.Digest=h.sha256;List<SpatialIdentity.Pair> pairs;
     if(c.Pairs.TryGetValue(h.sha256,out pairs)){p.Pair=SpatialIdentity.Select(h.sha256,desired,pairs);p.Needs=desired==null||!pairs.Any(x=>x.RecipeSha==desired);}
    }
   }catch(IOException){p.Status=State.Error;}catch(UnauthorizedAccessException){p.Status=State.Error;}catch(System.Security.SecurityException){p.Status=State.Error;}
  }return c;
 }
 void ReadIndex(){hints=new Dictionary<string,Hint>(StringComparer.OrdinalIgnoreCase);
  try{var j=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};var index=j.Deserialize<Index>(File.ReadAllText(Path.Combine(Store,"v1","index.json"),Encoding.UTF8));
   if(index==null||index.version!=1||index.photos==null)return;
   foreach(var kv in index.photos){var h=kv.Value;if(h==null||h.size<0||h.mtimeTicks<0||!Digest.IsMatch(h.sha256??"")||!Regex.IsMatch(h.fileId??"",@"\A[0-9a-f]{16}\z")||!Regex.IsMatch(h.volumeSerial??"",@"\A[0-9a-f]{8}\z"))throw new FormatException("invalid hint");}
   hints=new Dictionary<string,Hint>(index.photos,StringComparer.OrdinalIgnoreCase);
  }catch{hints.Clear();}
 }
 void Inventory(){
  try{if(Directory.Exists(Store))foreach(var f in new DirectoryInfo(Store).GetFiles("*.spatial",SearchOption.TopDirectoryOnly)){Legacy++;Bytes+=f.Length;}}
  catch(IOException){Unreadable++;}catch(UnauthorizedAccessException){Unreadable++;}
  string root=Path.Combine(Store,"v1");if(!Directory.Exists(root))return;
  try{foreach(string dir in Directory.EnumerateDirectories(root)){
    if(!Digest.IsMatch(Path.GetFileName(dir)))continue; // Never descend into quarantine or arbitrary folders.
    try{foreach(string path in Directory.EnumerateFiles(dir,"*.spatial.json",SearchOption.TopDirectoryOnly)){
      if(!PairName.IsMatch(Path.GetFileName(path)))continue;
      SpatialIdentity.Pair pair;var result=SpatialIdentity.Classify(path,false,out pair);
      if(result==SpatialIdentity.Classification.Valid){Valid++;List<SpatialIdentity.Pair> list;if(!Pairs.TryGetValue(pair.SourceSha,out list))Pairs[pair.SourceSha]=list=new List<SpatialIdentity.Pair>();list.Add(pair);try{Bytes+=new FileInfo(pair.Artifact).Length;}catch(IOException){} }
      else if(result==SpatialIdentity.Classification.Unsupported)Unsupported++;else if(result==SpatialIdentity.Classification.Invalid)Invalid++;else Unreadable++;
    }}catch(IOException){Unreadable++;}catch(UnauthorizedAccessException){Unreadable++;}
   }}catch(IOException){Unreadable++;}catch(UnauthorizedAccessException){Unreadable++;}
 }
 // The app schedules one worker at a time. Hash with a read-only sharing handle; never keep photo buffers.
 internal void HashMissing(Func<bool> cancelled){
  foreach(var kv in Photos){if(cancelled())break;if(kv.Value.Status!=State.Unchecked)continue;
   try{using(var f=new FileStream(kv.Key,FileMode.Open,FileAccess.Read,FileShare.Read)){
     var before=Stamp(f);string hash=Hash(f);var after=Stamp(f);
     if(Same(before,after)&&Same(after,probe(kv.Key))){after.sha256=hash;hints[kv.Key]=after;}
    }}catch(IOException){hashErrors.Add(kv.Key);}catch(UnauthorizedAccessException){hashErrors.Add(kv.Key);}catch(System.Security.SecurityException){hashErrors.Add(kv.Key);}
  }SaveIndex();
 }
 // The worker never shares mutable hints/photos with the UI catalogue it has just published.
 internal SpatialCatalog CopyForHash(){var copy=new SpatialCatalog(Store,Desired,probe);copy.hints=new Dictionary<string,Hint>(hints,StringComparer.OrdinalIgnoreCase);
  foreach(var p in Photos)copy.Photos[p.Key]=new Photo{Status=p.Value.Status,Digest=p.Value.Digest,Pair=p.Value.Pair,Needs=p.Value.Needs};return copy;}
 internal SpatialCatalog Complete(Func<bool> cancelled){HashMissing(cancelled);var next=Read(Store,Photos.Keys,Desired,probe,hints);
  foreach(string path in hashErrors){next.Photos[path].Status=State.Error;next.Photos[path].Pair=null;next.Photos[path].Needs=true;}return next;}
 internal void DropHint(string path){hints.Remove(path);Photo p;if(Photos.TryGetValue(path,out p)){p.Status=State.Unchecked;p.Digest=null;p.Pair=null;p.Needs=true;}}
 internal void SuppressPairs(IEnumerable<string> rejected){var bad=new HashSet<string>(rejected,StringComparer.OrdinalIgnoreCase);
  foreach(var list in Pairs.Values){int removed=list.RemoveAll(p=>bad.Contains(p.Artifact));Valid-=removed;Invalid+=removed;}
  foreach(var p in Photos.Values)if(p.Digest!=null){List<SpatialIdentity.Pair> pairs;if(Pairs.TryGetValue(p.Digest,out pairs)){p.Pair=SpatialIdentity.Select(p.Digest,Desired,pairs);p.Needs=Desired==null||!pairs.Any(x=>x.RecipeSha==Desired);}}
 }
 internal void SaveIndex(){string path=Path.Combine(Store,"v1","index.json"),temp=path+".tmp-"+Guid.NewGuid().ToString("N");
  try{Directory.CreateDirectory(Path.GetDirectoryName(path));var j=new JavaScriptSerializer{MaxJsonLength=int.MaxValue};
   File.WriteAllText(temp,j.Serialize(new Index{version=1,photos=hints}),new UTF8Encoding(false));
   if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
  }catch(IOException){}catch(UnauthorizedAccessException){}finally{try{File.Delete(temp);}catch{}}
 }
 internal string StoreText(){var lines=new List<string>();if(Valid>0)lines.Add("已有 "+Valid+" 张 3D 图片");if(Legacy>0)lines.Add(Legacy+" 张旧格式 · 需要重新生成才会显示立体效果");return string.Join("\n",lines);}

 // Called only on the catalogue worker. Desired recipe is independent of any last generation run.
 internal sealed class RecipeCache {
  string key,sha;DateTime retry;
  internal string Resolve(string checkpoint,int width,int height,bool installed,Func<string> describe){
   if(!installed){key=sha=null;return null;}
   try{var f=new FileInfo(checkpoint);string next=f.FullName+"|"+f.Length+"|"+f.LastWriteTimeUtc.Ticks+"|"+width+"x"+height;
    if(next==key&&(sha!=null||DateTime.UtcNow<retry))return sha;
    key=next;sha=null;retry=DateTime.UtcNow.AddSeconds(30);sha=ParseRecipe(describe());return sha;
   }catch{sha=null;return null;}
  }
 }
 internal static string ParseRecipe(string output){string recipe=null,sha=null;
  foreach(string raw in (output??"").Split('\n')){string line=raw.TrimEnd('\r');
   if(line.StartsWith("Recipe: ")){if(recipe!=null)return null;recipe=line.Substring(8);}
   if(line.StartsWith("RecipeSha: ")){if(sha!=null)return null;sha=line.Substring(11);}}
  try{var fields=SpatialIdentity.ParseStrict(recipe);return SpatialIdentity.Supported(fields)&&SpatialIdentity.RecipeSha256(fields)==sha?sha:null;}catch{return null;}
 }
 internal static string Describe(string script){
  var psi=new ProcessStartInfo("powershell.exe","-NoProfile -ExecutionPolicy Bypass -File \""+script+"\" -Describe"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
  using(var p=App.StartDirect(psi)){var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();
   if(!p.WaitForExit(60000)){try{p.Kill();}catch{}return null;}if(!System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[]{output,error},5000)||p.ExitCode!=0)return null;return output.Result;}
 }
}
