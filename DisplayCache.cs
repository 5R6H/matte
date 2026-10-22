using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

// LastWriteTime records use, not source freshness. Pins cover preparation, display and native transitions.
static class DisplayCache {
 internal const long DefaultLimit=1024L*1024*1024;
 static readonly object gate=new object();
 static readonly Dictionary<string,int> pins=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
 static readonly Dictionary<string,long> budgets=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
 static readonly HashSet<string> oversized=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
 static readonly HashSet<string> folders=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
 internal sealed class Lease:IDisposable {
  internal readonly string Path;bool closed;internal Lease(string path){Path=path;}
  public void Dispose(){lock(gate){if(closed)return;closed=true;int n=pins[Path]-1;if(n==0)pins.Remove(Path);else pins[Path]=n;string folder=System.IO.Path.GetDirectoryName(Path);if(oversized.Contains(folder))TrimLocked(folder,budgets[folder]);}}
 }
 static Lease PinLocked(string path){int count;pins.TryGetValue(path,out count);pins[path]=count+1;try{File.SetLastWriteTimeUtc(path,DateTime.UtcNow);}catch(IOException){}catch(UnauthorizedAccessException){}return new Lease(path);}
 internal static void Register(string folder){lock(gate)folders.Add(Path.GetFullPath(folder));}
 internal static Lease Pin(string path){if(string.IsNullOrEmpty(path))return null;path=Path.GetFullPath(path);lock(gate){if(!folders.Contains(System.IO.Path.GetDirectoryName(path))||!File.Exists(path))return null;return PinLocked(path);}}
 internal static Lease Acquire(string folder,string name,Action<string> write,long limit=DefaultLimit){
  folder=Path.GetFullPath(folder);string path=Path.Combine(folder,name);
  lock(gate){folders.Add(folder);if(File.Exists(path)){var held=PinLocked(path);TrimLocked(folder,limit);return held;}}
  // Conversion/copying does not hold the pin lock: presenting another slide never waits for these bytes.
  Directory.CreateDirectory(folder);string temp=path+".tmp-"+Guid.NewGuid().ToString("N");
  try{write(temp);lock(gate){if(!File.Exists(path))File.Move(temp,path);var lease=PinLocked(path);TrimLocked(folder,limit);return lease;}}
  finally{try{File.Delete(temp);}catch{}}
 }

 internal static void Trim(string folder,long limit=DefaultLimit){lock(gate)TrimLocked(Path.GetFullPath(folder),limit);}
 static void TrimLocked(string folder,long limit){
  budgets[folder]=limit;try{if(!Directory.Exists(folder))return;var files=new DirectoryInfo(folder).GetFiles().Where(f=>!f.Name.Contains(".tmp-")).OrderBy(f=>f.LastWriteTimeUtc).ThenBy(f=>f.Name,StringComparer.Ordinal).ToArray();long size=files.Sum(f=>f.Length);
   foreach(var file in files){if(size<=limit)break;if(pins.ContainsKey(file.FullName))continue;try{long bytes=file.Length;file.Delete();size-=bytes;}catch(IOException){}catch(UnauthorizedAccessException){}}
   if(size>limit)oversized.Add(folder);else oversized.Remove(folder);
  }catch(IOException){}catch(UnauthorizedAccessException){}
 }
}
