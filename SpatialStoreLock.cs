using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

// Stable coordination file, outside the relocatable picture store. Cooperates with publish.StoreLock.
sealed class SpatialStoreLock:IDisposable {
 FileStream stream;
 SpatialStoreLock(FileStream file){stream=file;}
 internal static string DefaultPath{get{return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"spatial","store.lock");}}
 internal static string GeneratorPath{get{return Path.Combine(Path.GetDirectoryName(DefaultPath),"generator.lock");}}
 internal static SpatialStoreLock Acquire(string path,int timeout){
  if(timeout<0)throw new ArgumentOutOfRangeException("timeout");
  string full=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(full));
  var file=new FileStream(full,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.ReadWrite);var watch=Stopwatch.StartNew();
  try{while(true){
   try{file.Lock(0,1);return new SpatialStoreLock(file);}catch(IOException){if(watch.ElapsedMilliseconds>=timeout)throw new TimeoutException("Spatial store lock timed out");}
   Thread.Sleep((int)Math.Min(100,Math.Max(1,timeout-watch.ElapsedMilliseconds)));
  }}catch{file.Dispose();throw;}
 }
 public void Dispose(){var file=Interlocked.Exchange(ref stream,null);if(file==null)return;try{file.Unlock(0,1);}finally{file.Dispose();}}
}
