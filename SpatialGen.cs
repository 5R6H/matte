using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// 生成 Spatial 图片: runs the Spatial generator for the playlist - Matte's own (spatial\SpatialBatch.ps1, SHARP once
// installed by SharpSetup.cs), or one set as SpatialGenerator in the settings. Matte passes its playlist (-RootsFile),
// so the generator works on those folders and images only and never recycles anything. Photos whose Spatial file is
// older than the photo (rotated / flipped) are made again.
partial class App {
 static string OwnSpatialDir{get{return Path.Combine(Dir,"spatial");}}
 string OwnGenerator{get{return Path.Combine(OwnSpatialDir,"SpatialBatch.ps1");}}
 bool CustomGenerator{get{return settings!=null&&!string.IsNullOrEmpty(settings.SpatialGenerator);}}
 string GeneratorScript{get{return CustomGenerator?settings.SpatialGenerator:OwnGenerator;}}
 bool GeneratorAvailable{get{
  if(!CustomGenerator)return SharpInstalled&&File.Exists(OwnGenerator);
  return File.Exists(settings.SpatialGenerator)&&(string.IsNullOrEmpty(settings.SpatialPython)||File.Exists(settings.SpatialPython));}}
 // the pictures: spatial\pictures next to the exe, or the folder in the settings (moving them is 存储与清理's job;
 // a moved store leaves a junction at the old path, so this path keeps working)
 string DefaultSpatialCacheDir{get{return Path.Combine(OwnSpatialDir,"pictures");}}
 string SpatialCacheDir{get{return settings!=null&&!string.IsNullOrEmpty(settings.SpatialFolder)?settings.SpatialFolder:DefaultSpatialCacheDir;}}
 Process gen;int genTotal,genDone,genFailed;GenProgress genProgress=new GenProgress();string genPhase;bool genStopped;
 bool Generating{get{return gen!=null&&!gen.HasExited;}}

 // What the playlist still needs: photos with no Spatial file, and photos changed since theirs was made. Names used by
 // two different photos are left out: the cache is keyed by name, so they can never get a Spatial file of their own.
 struct Needs{public int Photos,Missing,Outdated;}
 Needs SpatialNeeds(){
  var images=SourceImages();var stems=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
  foreach(var p in images){string s=Path.GetFileNameWithoutExtension(p);int n;stems[s]=stems.TryGetValue(s,out n)?n+1:1;}
  string cache=SpatialCacheDir;var n2=new Needs{Photos=images.Count};
  foreach(var p in images){
   if(stems[Path.GetFileNameWithoutExtension(p)]>1)continue;
   string map=Path.Combine(cache,Path.GetFileNameWithoutExtension(p)+".spatial");
   if(!File.Exists(map))n2.Missing++;
   else if(File.GetLastWriteTimeUtc(p)>File.GetLastWriteTimeUtc(map).AddSeconds(2))n2.Outdated++;
  }
  return n2;
 }
 // The same per playlist entry (the Spatial page's list): its photos (each counted under the first entry it is in, as
 // the rotation does), how many have an up-to-date Spatial file, how many still need one.
 internal class EntryNeeds{public string Entry;public int Photos,Have,Todo;}
 double spatialFileBytes=3.6e6;long spatialCacheBytes;
 static string SizeText(double bytes){double mb=bytes/1048576;return mb<1000?"约 "+Math.Max(1,Math.Round(mb))+" MB":"约 "+(mb/1024).ToString("0.0")+" GB";}
 List<EntryNeeds> SpatialNeedsByEntry(){
  var excluded=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  string ex=Path.Combine(Dir,"excluded-folders.txt");if(File.Exists(ex))foreach(string line in File.ReadAllLines(ex)){string n=line.Trim().TrimEnd('\\');if(n.Length>0&&!n.StartsWith("#"))excluded.Add(Path.GetFileName(n));}
  var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var per=new List<Tuple<string,List<string>>>();
  foreach(string entry in PlaylistEntries()){var imgs=new List<string>();
   if(File.Exists(entry)){if(IsImage(entry)&&seen.Add(entry))imgs.Add(entry);}
   else if(Directory.Exists(entry)){var pending=new Stack<string>();pending.Push(entry);
    while(pending.Count>0){string dir=pending.Pop();
     try{foreach(string f in Directory.GetFiles(dir))if(IsImage(f)&&seen.Add(f))imgs.Add(f);
      foreach(string d in Directory.GetDirectories(dir))if(!excluded.Contains(Path.GetFileName(d)))pending.Push(d);}catch{}}}
   else continue;
   per.Add(Tuple.Create(entry,imgs));}
  var stems=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
  foreach(var e in per)foreach(var p in e.Item2){string s=Path.GetFileNameWithoutExtension(p);int n;stems[s]=stems.TryGetValue(s,out n)?n+1:1;}
  string cache=SpatialCacheDir;var result=new List<EntryNeeds>();
  // the size one Spatial file takes, from those already made (a fair guess before any exist)
  try{var sizes=new DirectoryInfo(cache).GetFiles("*.spatial").Select(x=>x.Length).ToList();spatialFileBytes=sizes.Count>=3?sizes.Average():3.6e6;spatialCacheBytes=sizes.Sum();}catch{spatialFileBytes=3.6e6;}
  foreach(var e in per){var r=new EntryNeeds{Entry=e.Item1,Photos=e.Item2.Count};
   foreach(var p in e.Item2){if(stems[Path.GetFileNameWithoutExtension(p)]>1)continue;
    string map=Path.Combine(cache,Path.GetFileNameWithoutExtension(p)+".spatial");
    if(File.Exists(map)&&File.GetLastWriteTimeUtc(p)<=File.GetLastWriteTimeUtc(map).AddSeconds(2))r.Have++;else r.Todo++;}
   result.Add(r);}
  return result;
 }
 // Entries left out of 生成 (unticked on the Spatial page); everything else on the playlist is in.
 bool SpatialSkipped(string entry){return settings.SpatialSkip!=null&&settings.SpatialSkip.Any(e=>e.Equals(entry,StringComparison.OrdinalIgnoreCase));}
 void SetSpatialSkipped(string entry,bool skip){
  if(settings.SpatialSkip==null)settings.SpatialSkip=new List<string>();
  settings.SpatialSkip.RemoveAll(e=>e.Equals(entry,StringComparison.OrdinalIgnoreCase));if(skip)settings.SpatialSkip.Add(entry);SaveSettings();
 }
 // Returns a message for the panel when it cannot start. only: just this one photo (仅当前这张); otherwise the ticked
 // playlist entries.
 string StartGeneration(){return StartGeneration(null);}
 string StartGeneration(string only){
  if(Generating)return null;
  if(storeMoving)return "正在移动 Spatial 图片的位置，结束后再生成";
  if(!GeneratorAvailable)return "还没有安装 SHARP";
  string list=Path.Combine(Dir,"spatial-roots.txt");
  var roots=only!=null?new[]{only}:PlaylistEntries().Where(e=>(File.Exists(e)||Directory.Exists(e))&&!SpatialSkipped(e)).ToArray();
  if(roots.Length==0)return "没有勾选要生成的项目";
  File.WriteAllLines(list,roots,new UTF8Encoding(false));
  var psi=new ProcessStartInfo("powershell.exe","-NoProfile -ExecutionPolicy Bypass -File \""+GeneratorScript+"\" -RootsFile \""+list+"\" -Cache \""+SpatialCacheDir+"\""){
   UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
  gen=new Process{StartInfo=psi,EnableRaisingEvents=true};genTotal=genDone=genFailed=0;genProgress=new GenProgress();genPhase="正在准备…";genStopped=false;genHeld=null;genNotice=null;
  gen.OutputDataReceived+=(s,a)=>{if(a.Data!=null)ui.BeginInvoke((Action)(()=>GenLine(a.Data)));};
  gen.ErrorDataReceived+=(s,a)=>{};   // Python's ordinary progress warnings
  gen.Exited+=delegate{ui.BeginInvoke((Action)GenFinished);};
  gen.Start();gen.BeginOutputReadLine();gen.BeginErrorReadLine();Log("Spatial generation started");
  GenChanged();return null;
 }
 void StopGeneration(){
  if(!Generating)return;genStopped=true;if(genHeld!=null){try{ResumeTree(gen.Id);}catch{}genHeld=null;}
  try{Process.Start(new ProcessStartInfo("taskkill.exe","/PID "+gen.Id+" /T /F"){UseShellExecute=false,CreateNoWindow=true});}catch(Exception e){Log("Stop generation: "+e.Message);}
 }
 // Generating holds still while a fullscreen app is in front or the computer runs on battery (the whole process tree is
 // suspended, so it costs nothing meanwhile) and goes on by itself afterwards. Called with the power check (500 ms).
 string genHeld,genNotice;bool genCpuNoted;
 void PaceGeneration(bool fullscreen,bool battery){
  if(!Generating){genHeld=null;return;}
  string want=fullscreen?"全屏中":battery?"正在用电池":null;
  if(want==genHeld)return;
  try{if(want!=null&&genHeld==null)SuspendTree(gen.Id);else if(want==null)ResumeTree(gen.Id);}catch(Exception e){Log("Generation pause: "+e.Message);}
  Log("Spatial generation "+(want!=null?"paused: "+want:"resumed"));genHeld=want;GenChanged();
 }
 // Progress from the generator's own lines (spatial\README.md lists them).
 void GenLine(string line){
  if(line.StartsWith("Preparing contain-size proxies"))genPhase="正在准备图片…";
  else if(line.StartsWith("Updating ")){genPhase="正在更新改过的图片…";}
  genProgress.Line(line);genTotal=genProgress.Total;genDone=genProgress.Done;genFailed=genProgress.Failed;
  if(line.StartsWith("Predicting gaussians for")||Regex.IsMatch(line,@"Processing (\d+) valid"))genPhase="正在生成";
  if(line.StartsWith("Nothing to generate"))genPhase="没有需要生成的图片";
  if(line.StartsWith("GPU memory too small")&&!genCpuNoted){genCpuNoted=true;genNotice="显存不够，这次改用 CPU";}
  GenChanged();
 }
 // Done (or stopped): the new files count at once; with Spatial on, the rotation takes them in.
 static string Short(string s){return s.Length>60?s.Substring(0,59)+"…":s;}
 void GenFinished(){
  try{File.Delete(Path.Combine(Dir,"spatial-roots.txt"));}catch{}   // the list of what to make was only for this run
  int code=-1;try{code=gen.ExitCode;}catch{}
  genProgress.Exit(genStopped?-1:code);genDone=genProgress.Done;genFailed=genProgress.Failed;
  if(genStopped&&genFailed>0)genFailed--;   // the photo it was stopped on did not fail
  Log("Spatial generation "+(genStopped?"stopped":"ended")+", exit "+code+", "+genDone+" made, "+genFailed+" failed"+(genProgress.LastFailure!=null?" (last: "+genProgress.LastFailure+")":""));
  string failed=genFailed>0?" · "+genFailed+" 张没成功"+(genProgress.LastFailure!=null?"（"+Short(genProgress.LastFailure)+"）":""):"";
  genPhase=(genStopped?"已停止 · 已完成 "+genDone+" 张":code==0&&genFailed==0?"完成 · 生成了 "+genDone+" 张":genFailed>0?"生成了 "+genDone+" 张":"出错了（退出码 "+code+"）· 已完成 "+genDone+" 张")+failed;
  try{LoadSpatialCatalog();if(spatialEnabled)RebuildSlides();}catch(Exception e){Log("After generation: "+e.Message);}
  if(!genStopped&&code==0&&genDone>0)try{tray.ShowBalloonTip(3000,"Matte","Spatial 图片生成完成：新增 "+genDone+" 张",System.Windows.Forms.ToolTipIcon.Info);}catch{}
  GenChanged();if(panel!=null)RefreshSpatial(true);
  RunSpatialSweep();   // the new pictures into the index   // the counts again, now with the new pictures
 }
}
// What the generator's output says about the photos so far. A photo counts as made only once it is written (Matte's
// generator says "Saved: <name>"); "Failed: <name>: <why>" counts it as failed, the reason kept. A generator of one's own
// that only says "Predicting gaussians for <photo>" has a photo counted as made when the next one starts or when it exits
// cleanly; exiting with an error counts the photo it was on as failed.
class GenProgress {
 public int Total,Done,Failed;public string LastFailure;bool open;
 public int Current{get{return System.Math.Min(Total,Done+Failed+1);}}   // the photo being made now, from 1
 public void Line(string line){
  var m=Regex.Match(line,@"Processing (\d+) valid");if(m.Success)Total+=int.Parse(m.Groups[1].Value);
  if(line.StartsWith("Predicting gaussians for")){Close(true);open=true;}
  else if(line.StartsWith("Saved: ")){if(open){open=false;Done++;}}
  else if(line.StartsWith("Failed: ")){open=false;Failed++;LastFailure=line.Substring(8).Trim();}
 }
 public void Exit(int code){Close(code==0);}
 void Close(bool ok){if(!open)return;open=false;if(ok)Done++;else Failed++;}
}
