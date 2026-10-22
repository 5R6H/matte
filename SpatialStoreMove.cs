using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

// 存储与清理 → 移动位置: the Spatial picture store to a folder the user picks. The pictures are copied there and
// checked (every file's size, plus SHA-256 for v1 pictures and JSON); then the old path becomes a directory junction to the new place, so the generator
// and Matte keep using the same path and simply land in the new folder. A junction needs no administrator rights. Moving back to the default does the
// reverse (copy back into a real folder at the path, junction gone).
// All subfolders travel too; publication temporaries and the v1 lock file stay behind.
// The old copy is not deleted here: it stays (renamed) until the user clears it on the page, so a failure halfway can
// never lose pictures. Any failure before the switch leaves everything as it was; a failure in the switch puts the old
// folder back.
static class SpatialStoreMove {
 internal class Result{public string Target;public string Backup;}
 internal static Action<string> BeforeVerify;   // test seam: stage complete, before any verification or switch
 // progress: 0..1 of the bytes copied; log: one line per step (App.Log)
 internal static Result Move(string defaultDir,string currentTarget,string dest,Action<double> progress,Action<string> log){
  // Global order: generator.lock, then store.lock. Hold both until the move is complete.
  SpatialStoreLock run;try{run=SpatialStoreLock.Acquire(SpatialStoreLock.GeneratorPath,0);}catch(TimeoutException){throw new IOException("正在生成立体照片，请结束后再移动");}
  using(run){
   SpatialStoreLock held;try{held=SpatialStoreLock.Acquire(SpatialStoreLock.DefaultPath,2000);}catch(TimeoutException){throw new IOException("存储正在使用，请稍后再移动");}
   using(held)return MoveLocked(defaultDir,currentTarget,dest,progress,log);
  }
 }
 static Result MoveLocked(string defaultDir,string currentTarget,string dest,Action<double> progress,Action<string> log){
  string cur=Full(currentTarget??defaultDir);
  bool toDefault=string.Equals(Full(dest),Full(defaultDir),StringComparison.OrdinalIgnoreCase);
  if(toDefault&&currentTarget==null)throw new IOException("已经在默认位置");
  if(currentTarget!=null&&string.Equals(Full(dest),Full(currentTarget),StringComparison.OrdinalIgnoreCase))throw new IOException("已经在这个位置");
  if(!toDefault&&IsUnder(Full(dest),Full(cur)))throw new IOException("新位置不能在现在的文件夹里面");
  if(!toDefault&&Directory.Exists(dest)&&Directory.EnumerateFileSystemEntries(dest).Any())throw new IOException("新位置需要是一个空文件夹");
  var files=StoreFiles(cur);
  long total=files.Sum(f=>f.Length);
  string root=Path.GetPathRoot(Full(toDefault?Path.GetDirectoryName(Full(defaultDir)):dest));
  try{var drive=new DriveInfo(root);if(drive.AvailableFreeSpace<total+(200L<<20))throw new IOException("目标磁盘空间不够（需要 "+(total>>20)+" MB）");}catch(ArgumentException){}
  // 1. copy, then check every file
  string stage=toDefault?Full(defaultDir)+".moving":Full(dest);
  if(Directory.Exists(stage)&&Directory.EnumerateFileSystemEntries(stage).Any())throw new IOException("临时位置不为空，请先检查："+stage);
  Directory.CreateDirectory(stage);
  long done=0;var copied=new List<string>();var directories=new List<string>{stage};
  try{
   foreach(var f in files){string to=Path.Combine(stage,Relative(cur,f.FullName));CopyDirectory(Path.GetDirectoryName(to),directories);
    using(var input=new FileStream(f.FullName,FileMode.Open,FileAccess.Read,FileShare.Read))using(var output=new FileStream(to,FileMode.CreateNew,FileAccess.Write,FileShare.None)){copied.Add(to);input.CopyTo(output);}
    done+=f.Length;if(progress!=null)progress(total>0?done/(double)total:1);}
   if(files.Length==0&&progress!=null)progress(1);
   if(BeforeVerify!=null)BeforeVerify(stage);
   foreach(var f in files){string relative=Relative(cur,f.FullName);var c=new FileInfo(Path.Combine(stage,relative));
    if(!c.Exists||c.Length!=f.Length)throw new IOException("复制核对失败："+relative);
    if(NeedsHash(relative)&&!Hash(f.FullName).SequenceEqual(Hash(c.FullName)))throw new IOException("复制内容核对失败："+relative);}
  }catch{try{SafeDeleteCopy(copied,directories);}catch{}throw;}   // only files and empty directories this copy made
  log("Spatial store: "+files.Length+" files ("+(total>>20)+" MB) copied and checked in "+stage);
  // 2. switch the path over
  string backup;
  if(toDefault){
   RemoveJunction(defaultDir);
   try{Directory.Move(stage,defaultDir);}catch{MakeJunction(defaultDir,cur);throw;}
   backup=cur;
  }else if(IsJunction(defaultDir)){
   RemoveJunction(defaultDir);
   try{MakeJunction(defaultDir,dest);}catch{MakeJunction(defaultDir,cur);throw;}
   backup=cur;
  }else{
   backup=Full(defaultDir)+".old-"+DateTime.Now.ToString("yyyyMMdd-HHmmss");
   Directory.Move(defaultDir,backup);
   try{MakeJunction(defaultDir,dest);}catch{try{RemoveJunction(defaultDir);}catch{}Directory.Move(backup,defaultDir);throw;}
  }
  // 3. read back through the path the apps use
  int seen=Directory.Exists(defaultDir)?StoreFiles(defaultDir).Length:-1;
  if(seen!=files.Length)throw new IOException("切换后核对失败（"+seen+" / "+files.Length+"）");
  log("Spatial store now at "+(toDefault?defaultDir:dest)+"; old copy kept at "+backup);
  return new Result{Target=toDefault?null:Full(dest),Backup=backup};
 }
 static string Relative(string root,string path){return path.Substring(Full(root).Length+1);}
 static FileInfo[] StoreFiles(string root){return new DirectoryInfo(root).GetFiles("*",SearchOption.AllDirectories)
  .Where(f=>f.Name.IndexOf(".tmp-",StringComparison.OrdinalIgnoreCase)<0&&!Relative(root,f.FullName).Equals(@"v1\.lock",StringComparison.OrdinalIgnoreCase)).ToArray();}
 static bool NeedsHash(string relative){string ext=Path.GetExtension(relative);return relative.StartsWith(@"v1\",StringComparison.OrdinalIgnoreCase)&&(ext.Equals(".spatial",StringComparison.OrdinalIgnoreCase)||ext.Equals(".json",StringComparison.OrdinalIgnoreCase));}
 static byte[] Hash(string path){using(var f=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))using(var sha=SHA256.Create())return sha.ComputeHash(f);}
 static void CopyDirectory(string directory,List<string> made){if(Directory.Exists(directory))return;CopyDirectory(Path.GetDirectoryName(directory),made);Directory.CreateDirectory(directory);made.Add(directory);}
 static void SafeDeleteCopy(List<string> files,List<string> directories){foreach(string p in files)if(File.Exists(p))File.Delete(p);
  foreach(string d in directories.AsEnumerable().Reverse())if(Directory.Exists(d)&&!Directory.EnumerateFileSystemEntries(d).Any())Directory.Delete(d);}
 static string Full(string p){return Path.GetFullPath(p).TrimEnd('\\');}
 static bool IsUnder(string p,string dir){return p.Equals(dir,StringComparison.OrdinalIgnoreCase)||p.StartsWith(dir+"\\",StringComparison.OrdinalIgnoreCase);}
 internal static bool IsJunction(string p){try{return Directory.Exists(p)&&(new DirectoryInfo(p).Attributes&FileAttributes.ReparsePoint)!=0;}catch{return false;}}
 // cmd's own mklink / rmdir: a junction made or removed without touching what it points to
 static void MakeJunction(string link,string target){Cmd("mklink /J \""+Full(link)+"\" \""+Full(target)+"\"");if(!IsJunction(link))throw new IOException("无法在原位置建立链接");}
 static void RemoveJunction(string link){if(!IsJunction(link))return;Cmd("rmdir \""+Full(link)+"\"");if(Directory.Exists(link))throw new IOException("无法移除原位置的链接");}
 static void Cmd(string args){
  var psi=new ProcessStartInfo("cmd.exe","/d /c "+args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
  using(var p=App.StartDirect(psi)){p.StandardOutput.ReadToEnd();p.StandardError.ReadToEnd();p.WaitForExit(15000);}
 }
}
