using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

// 存储与清理 → 移动位置: the Spatial picture store to a folder the user picks. The pictures are copied there and
// checked (every file, its size); then the old path becomes a directory junction to the new place, so the generator
// and Matte keep using the same path and simply land in the new folder. A junction needs no administrator rights. Moving back to the default does the
// reverse (copy back into a real folder at the path, junction gone).
// The generator's working copies (_proxy-*, _matte-singles) are not carried over: it makes them again.
// The old copy is not deleted here: it stays (renamed) until the user clears it on the page, so a failure halfway can
// never lose pictures. Any failure before the switch leaves everything as it was; a failure in the switch puts the old
// folder back.
static class SpatialStoreMove {
 internal class Result{public string Target;public string Backup;}
 // progress: 0..1 of the bytes copied; log: one line per step (App.Log)
 internal static Result Move(string defaultDir,string currentTarget,string dest,Action<double> progress,Action<string> log){
  string cur=currentTarget??defaultDir;
  bool toDefault=string.Equals(Full(dest),Full(defaultDir),StringComparison.OrdinalIgnoreCase);
  if(toDefault&&currentTarget==null)throw new IOException("已经在默认位置");
  if(currentTarget!=null&&string.Equals(Full(dest),Full(currentTarget),StringComparison.OrdinalIgnoreCase))throw new IOException("已经在这个位置");
  if(!toDefault&&IsUnder(Full(dest),Full(cur)))throw new IOException("新位置不能在现在的文件夹里面");
  if(!toDefault&&Directory.Exists(dest)&&Directory.EnumerateFileSystemEntries(dest).Any())throw new IOException("新位置需要是一个空文件夹");
  var files=new DirectoryInfo(cur).GetFiles();   // the pictures (top level); working folders stay behind
  long total=files.Sum(f=>f.Length);
  string root=Path.GetPathRoot(Full(toDefault?Path.GetDirectoryName(Full(defaultDir)):dest));
  try{var drive=new DriveInfo(root);if(drive.AvailableFreeSpace<total+(200L<<20))throw new IOException("目标磁盘空间不够（需要 "+(total>>20)+" MB）");}catch(ArgumentException){}
  // 1. copy, then check every file
  string stage=toDefault?Full(defaultDir)+".moving":dest;
  if(Directory.Exists(stage)&&toDefault)Directory.Delete(stage,true);
  Directory.CreateDirectory(stage);
  long done=0;
  try{
   foreach(var f in files){string to=Path.Combine(stage,f.Name);File.Copy(f.FullName,to,true);done+=f.Length;if(progress!=null)progress(total>0?done/(double)total:1);}
   foreach(var f in files){var c=new FileInfo(Path.Combine(stage,f.Name));if(!c.Exists||c.Length!=f.Length)throw new IOException("复制核对失败："+f.Name);}
  }catch{try{SafeDeleteCopy(stage,files);}catch{}throw;}   // only what this copy made
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
  int seen=Directory.Exists(defaultDir)?new DirectoryInfo(defaultDir).GetFiles().Length:-1;
  if(seen!=files.Length)throw new IOException("切换后核对失败（"+seen+" / "+files.Length+"）");
  log("Spatial store now at "+(toDefault?defaultDir:dest)+"; old copy kept at "+backup);
  return new Result{Target=toDefault?null:Full(dest),Backup=backup};
 }
 static void SafeDeleteCopy(string stage,FileInfo[] files){foreach(var f in files){var p=Path.Combine(stage,f.Name);if(File.Exists(p))File.Delete(p);}if(!Directory.EnumerateFileSystemEntries(stage).Any())Directory.Delete(stage);}
 static string Full(string p){return Path.GetFullPath(p).TrimEnd('\\');}
 static bool IsUnder(string p,string dir){return p.Equals(dir,StringComparison.OrdinalIgnoreCase)||p.StartsWith(dir+"\\",StringComparison.OrdinalIgnoreCase);}
 internal static bool IsJunction(string p){try{return Directory.Exists(p)&&(new DirectoryInfo(p).Attributes&FileAttributes.ReparsePoint)!=0;}catch{return false;}}
 // cmd's own mklink / rmdir: a junction made or removed without touching what it points to
 static void MakeJunction(string link,string target){Cmd("mklink /J \""+Full(link)+"\" \""+Full(target)+"\"");if(!IsJunction(link))throw new IOException("无法在原位置建立链接");}
 static void RemoveJunction(string link){if(!IsJunction(link))return;Cmd("rmdir \""+Full(link)+"\"");if(Directory.Exists(link))throw new IOException("无法移除原位置的链接");}
 static void Cmd(string args){
  var psi=new ProcessStartInfo("cmd.exe","/d /c "+args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
  using(var p=Process.Start(psi)){p.StandardOutput.ReadToEnd();p.StandardError.ReadToEnd();p.WaitForExit(15000);}
 }
}
