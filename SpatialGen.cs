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
 internal static readonly object StartGate=new object();
 internal static Process StartDirect(ProcessStartInfo psi){lock(StartGate)return Process.Start(psi);}
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
 GenProcess gen;int genRun;int genTotal,genDone,genFailed;GenProgress genProgress=new GenProgress();string genPhase;bool genStopped;
 // Busy from a run's start until its end is handled (its output read to the end), not only while its process runs: the
 // generator's last lines can come after it exited, and nothing else (another run, a store move, a sweep) may start then.
 bool genActive,genStuck;   // genStuck: the last run's end could not be confirmed (it may still be running)
 bool Generating{get{return genActive;}}

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
  genTotal=genDone=genFailed=0;genProgress=new GenProgress();genPhase="正在准备…";genStopped=false;genHeld=null;genNotice=null;
  string why=RunGenerator("powershell.exe","-NoProfile -ExecutionPolicy Bypass -File \""+GeneratorScript+"\" -RootsFile \""+list+"\" -Cache \""+SpatialCacheDir+"\"");
  if(why!=null)return why;
  Log("Spatial generation started");
  GenChanged();return null;
 }
 void StopGeneration(){
  if(!Generating)return;
  if(genStuck){   // its end could not be confirmed: end its job once more, and let go if it is empty now
   GenJob.Terminate(gen.Job);for(int i=0;i<20&&GenJob.Active(gen.Job)>0;i++)System.Threading.Thread.Sleep(100);
   if(GenJob.Active(gen.Job)!=0){Log("Spatial generation: still not ended");return;}
   genStuck=false;genActive=false;gen.Close();genPhase="已停止";Log("Spatial generation: ended after all");
   GenChanged();if(panel!=null)RefreshSpatial(true);RunSpatialSweep();return;}
  genStopped=true;if(genHeld!=null){try{ResumeTree(gen.Id);}catch{}genHeld=null;}
  // the whole job: the generator and everything it started, children it already left behind included
  if(!GenJob.Terminate(gen.Job))Log("Stop generation: its job could not be ended");
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
 // One run: the generator started inside its job (GenProcess), each line of its output to the UI thread in order while
 // the run is current, and its end handled once, after its last line and once everything it started is over. genRun
 // numbers the runs: an earlier run's lines and end are dropped. Returns why it could not start, or null.
 internal Action<Action> genPost;   // where a run's lines and end go: the app's window (null), or a test's dispatcher
 internal int genActiveAtRelease=-1;   // processes left in the job as the run let go (tests: must be 0)
 string RunGenerator(string exe,string args){
  int run=++genRun;Func<bool> current=()=>run==genRun;Action<Action> toUi=genPost??(a=>ui.BeginInvoke(a));
  string why;var p=GenProcess.Start(exe,args,l=>toUi(()=>{if(current())GenLine(l);}),out why);
  if(p==null){Log("Spatial generation: "+why);return why;}
  gen=p;genActive=true;genStuck=false;
  Watch(p,toUi,current,(drained,ended,left)=>{
   genActiveAtRelease=left;
   if(!drained)Log("Spatial generation: its output may not have been read to the end");
   if(!ended)Log("Spatial generation: it could not be confirmed that everything it started has ended");
   try{GenFinished(drained,ended);}finally{if(ended)p.Close();}
  });
  return null;
 }
 internal static Action<GenProcess> WaitForEnd=p=>p.Wait();   // replaced only by tests (a wait that fails)
 // Whether everything the run started is over: its job empty, ended first if it is not. Replaced only by tests.
 internal static Func<GenProcess,bool> EnsureEnded=p=>{
  if(GenJob.Active(p.Job)>0){GenJob.Terminate(p.Job);for(int i=0;i<50&&GenJob.Active(p.Job)>0;i++)System.Threading.Thread.Sleep(100);}
  return GenJob.Active(p.Job)==0;
 };
 // The end, on its own thread: the wait (the process exited and its output read to the end), then the job emptied; then
 // done(drained, ended, processes still in the job) once on the UI thread, after the last line. drained false: the
 // wait failed, output may be short; ended false: something of the run may still be running.
 internal static bool FailWatcher;   // tests only: as if the end thread could not be started
 internal static void Watch(GenProcess p,Action<Action> post,Func<bool> current,Action<bool,bool,int> done){
  Action end=()=>{
   bool drained=true;try{WaitForEnd(p);}catch{drained=false;}
   bool ended=false;try{ended=EnsureEnded(p);}catch{}
   int left=GenJob.Active(p.Job);
   post(()=>{if(current())done(drained,ended,left);});
  };
  try{if(FailWatcher)throw new OutOfMemoryException("test: no thread");new System.Threading.Thread(()=>end()){IsBackground=true,Name="Spatial generation end"}.Start();}
  catch{   // no end thread: the run is ended here and now, as a failed drain
   GenJob.Terminate(p.Job);bool ended=false;try{ended=EnsureEnded(p);}catch{}int left=GenJob.Active(p.Job);
   post(()=>{if(current())done(false,ended,left);});
  }
 }
 static string Short(string s){return s.Length>60?s.Substring(0,59)+"…":s;}
 // drained false: the run's output may be incomplete. The photo it was on is not taken as made, nothing is announced
 // as done, and the run is closed, so lines that still arrive are not counted.
 // ended false: something the run started may still be running (and writing to the store): Matte stays busy (no new run,
 // store move or sweep) until it is restarted, and says so.
 void GenFinished(bool drained=true,bool ended=true){
  genActive=!ended;genStuck=!ended;genRun++;   // the run is closed: anything of it still on its way is dropped
  try{File.Delete(Path.Combine(Dir,"spatial-roots.txt"));}catch{}   // the list of what to make was only for this run
  int code=-1;try{code=gen.ExitCode;}catch{}
  if(genStopped||!drained)genProgress.Cancel();else genProgress.Exit(code);genDone=genProgress.Done;genFailed=genProgress.Failed;
  Log("Spatial generation "+(genStopped?"stopped":"ended")+", exit "+code+", "+genDone+" made, "+genFailed+" failed"+(genProgress.LastFailure!=null?" (last: "+genProgress.LastFailure+")":""));
  string failed=genFailed>0?" · "+genFailed+" 张没成功"+(genProgress.LastFailure!=null?"（"+Short(genProgress.LastFailure)+"）":""):"";
  genPhase=!ended?"生成没能完全结束 · 请重新启动 Matte 后再生成或移动存储":!drained&&!genStopped?"生成中断（输出没有读完）· 已完成 "+genDone+" 张"+failed:(genStopped?"已停止 · 已完成 "+genDone+" 张":code==0&&genFailed==0?"完成 · 生成了 "+genDone+" 张":genFailed>0?"生成了 "+genDone+" 张":"出错了（退出码 "+code+"）· 已完成 "+genDone+" 张")+failed;
  try{LoadSpatialCatalog();if(spatialEnabled)RebuildSlides();}catch(Exception e){Log("After generation: "+e.Message);}
  if(ended&&drained&&!genStopped&&code==0&&genDone>0)try{tray.ShowBalloonTip(3000,"Matte","Spatial 图片生成完成：新增 "+genDone+" 张",System.Windows.Forms.ToolTipIcon.Info);}catch{}
  GenChanged();if(panel!=null)RefreshSpatial(true);
  if(ended)RunSpatialSweep();   // the new pictures into the index   // the counts again, now with the new pictures
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
 public void Cancel(){open=false;}   // stopped: the photo it was on neither made nor failed; earlier counts stay
 void Close(bool ok){if(!open)return;open=false;if(ok)Done++;else Failed++;}
}
// A Windows job for one generation run: the generator and every process it starts (Python under PowerShell) belong to
// it, so ending the job ends all of them, children a parent has already left behind included, and an empty job means
// the run is over. Closed with Matte, the job ends its processes too (KILL_ON_JOB_CLOSE): no generator runs on unseen.
static class GenJob {
 [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs,string name);
 [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job,int cls,ref ExtendedLimits info,int size);
 [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool QueryInformationJobObject(IntPtr job,int cls,out Accounting info,int size,IntPtr ret);
 [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job,IntPtr process);
 [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool TerminateJobObject(IntPtr job,uint code);
 [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct BasicLimits{public long PerProcessUserTimeLimit,PerJobUserTimeLimit;public uint LimitFlags;public UIntPtr MinimumWorkingSetSize,MaximumWorkingSetSize;public uint ActiveProcessLimit;public UIntPtr Affinity;public uint PriorityClass,SchedulingClass;}
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct IoCounters{public ulong ReadOperationCount,WriteOperationCount,OtherOperationCount,ReadTransferCount,WriteTransferCount,OtherTransferCount;}
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct ExtendedLimits{public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed;}
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct Accounting{public long TotalUserTime,TotalKernelTime,ThisPeriodTotalUserTime,ThisPeriodTotalKernelTime;public uint TotalPageFaultCount,TotalProcesses,ActiveProcesses,TotalTerminatedProcesses;}
 const uint KillOnJobClose=0x2000;
 public static IntPtr Create(){
  var job=CreateJobObject(IntPtr.Zero,null);if(job==IntPtr.Zero)return IntPtr.Zero;
  var info=new ExtendedLimits();info.Basic.LimitFlags=KillOnJobClose;
  if(!SetInformationJobObject(job,9,ref info,System.Runtime.InteropServices.Marshal.SizeOf(typeof(ExtendedLimits)))){CloseHandle(job);return IntPtr.Zero;}
  return job;
 }
 public static bool Assign(IntPtr job,IntPtr process){return AssignProcessToJobObject(job,process);}
 public static bool Terminate(IntPtr job){return TerminateJobObject(job,1);}
 public static int Active(IntPtr job){Accounting a;return QueryInformationJobObject(job,1,out a,System.Runtime.InteropServices.Marshal.SizeOf(typeof(Accounting)),IntPtr.Zero)?(int)a.ActiveProcesses:-1;}
 public static void Close(IntPtr job){if(job!=IntPtr.Zero)CloseHandle(job);}
 internal static int[] Layout(){return new[]{System.Runtime.InteropServices.Marshal.SizeOf(typeof(BasicLimits)),System.Runtime.InteropServices.Marshal.SizeOf(typeof(IoCounters)),System.Runtime.InteropServices.Marshal.SizeOf(typeof(ExtendedLimits)),System.Runtime.InteropServices.Marshal.SizeOf(typeof(Accounting)),(int)System.Runtime.InteropServices.Marshal.OffsetOf(typeof(ExtendedLimits),"ProcessMemoryLimit")};}
}
// A generator run's process: created suspended, put in a new job (GenJob), and only then let run, so everything it
// starts is in the job from its first instruction (a process started first and put in the job afterwards could start a
// child outside it in between). No job, or no way into it: no run (the suspended process is ended unrun). Its output
// and error streams are pipes read here: each output line to `line` (on a reader thread), errors read and dropped.
// Wait returns once the process has exited and both pipes are read to the end.
sealed class GenProcess {
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct SecurityAttributes{public int nLength;public IntPtr lpSecurityDescriptor;public bool bInheritHandle;}
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,CharSet=System.Runtime.InteropServices.CharSet.Unicode)] struct StartupInfo{public int cb;public string lpReserved,lpDesktop,lpTitle;public int dwX,dwY,dwXSize,dwYSize,dwXCountChars,dwYCountChars,dwFillAttribute,dwFlags;public short wShowWindow,cbReserved2;public IntPtr lpReserved2,hStdInput,hStdOutput,hStdError;}
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct StartupInfoEx{public StartupInfo StartupInfo;public IntPtr lpAttributeList;}
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] struct ProcessInformation{public IntPtr hProcess,hThread;public int dwProcessId,dwThreadId;}
 [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)] static extern bool CreatePipe(out IntPtr read,out IntPtr write,ref SecurityAttributes sa,int size);
 [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)] static extern bool SetHandleInformation(IntPtr h,int mask,int flags);
 [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true,CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr CreateFile(string name,uint access,uint share,ref SecurityAttributes sa,uint creation,uint flags,IntPtr template);
 [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true,CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern bool CreateProcess(string app,System.Text.StringBuilder cmd,IntPtr pa,IntPtr ta,bool inherit,uint flags,IntPtr env,string dir,ref StartupInfoEx si,out ProcessInformation pi);
[System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)] static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref IntPtr size);
[System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)] static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
[System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
 [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr thread);
 [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process,uint code);
 [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h,uint ms);
 [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
 [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
 const uint CreateSuspended=0x4,CreateNoWindow=0x08000000,ExtendedStartupInfo=0x80000,WaitObject0=0,Infinite=0xFFFFFFFF;
 static readonly IntPtr Invalid=new IntPtr(-1);
 // tests only: the startup's failure points, and a pause while the process is created but not yet in its job
 internal static Action BeforeAssign,InWindow;
 internal static Action<IntPtr[]> PipesCreated;
 internal static int FailReaderStage;
 internal static bool FailJob,FailAssign,FailResume,FailTerminate,FailRead,FailHandleList;
 public int Id{get;private set;}
 public IntPtr Job{get;private set;}
 IntPtr process;System.Threading.Thread outReader,errReader;volatile Exception readError;bool closed;

 // Everything the start has acquired, owned here until the start succeeds; Rollback ends the (never resumed) process
 // and confirms it is gone, then releases what is still owned. A pipe end moves to its reader once that reader runs.
 sealed class Startup {
  public IntPtr Job,OutRead,OutWrite,ErrRead,ErrWrite,Nul=Invalid,Process,Thread,Attributes,Handles;public bool Assigned;public string Why;
  // the attribute list naming the only handles the child inherits; it and the array it points at live until Release
  public bool InheritOnly(IntPtr[] hs){
   if(FailHandleList)return false;
   IntPtr size=IntPtr.Zero;InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);
   Attributes=System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
   if(!InitializeProcThreadAttributeList(Attributes,1,0,ref size)){System.Runtime.InteropServices.Marshal.FreeHGlobal(Attributes);Attributes=IntPtr.Zero;return false;}
   Handles=System.Runtime.InteropServices.Marshal.AllocHGlobal(IntPtr.Size*hs.Length);
   System.Runtime.InteropServices.Marshal.Copy(hs,0,Handles,hs.Length);
   return UpdateProcThreadAttribute(Attributes,0,(IntPtr)0x20002,Handles,(IntPtr)(IntPtr.Size*hs.Length),IntPtr.Zero,IntPtr.Zero);   // PROC_THREAD_ATTRIBUTE_HANDLE_LIST
  }
  public void Release(){
   if(Attributes!=IntPtr.Zero){DeleteProcThreadAttributeList(Attributes);System.Runtime.InteropServices.Marshal.FreeHGlobal(Attributes);Attributes=IntPtr.Zero;}
   if(Handles!=IntPtr.Zero){System.Runtime.InteropServices.Marshal.FreeHGlobal(Handles);Handles=IntPtr.Zero;}
  }
  public void Rollback(){
   if(Process!=IntPtr.Zero){
    // ended (through its job once it is in one), and seen to end; a second try if the first is refused or slow
    bool gone=false;
    for(int attempt=0;attempt<2&&!gone;attempt++){
     bool asked=attempt==0&&FailTerminate?false:Assigned?GenJob.Terminate(Job):TerminateProcess(Process,1);
     gone=asked&&WaitForSingleObject(Process,5000)==WaitObject0;
    }
    if(!gone)Why=(Why??"")+"（启动失败后没能确认生成程序已经结束"+(Assigned?"；它在作业里，Matte 关闭时会被结束":"")+"）";
   }
   foreach(var h in new[]{Thread,Process,OutRead,OutWrite,ErrRead,ErrWrite})if(h!=IntPtr.Zero)CloseHandle(h);
   if(Nul!=Invalid&&Nul!=IntPtr.Zero)CloseHandle(Nul);
   GenJob.Close(Job);   // with KILL_ON_JOB_CLOSE: an assigned process that would not end goes with it
   Thread=Process=OutRead=OutWrite=ErrRead=ErrWrite=Job=IntPtr.Zero;Nul=Invalid;
  }
 }

 public static GenProcess Start(string exe,string args,Action<string> line,out string why){
  var a=new Startup();GenProcess g=null;bool ok=false;
  try{
   a.Job=FailJob?IntPtr.Zero:GenJob.Create();
   if(a.Job==IntPtr.Zero){a.Why="不能为生成准备隔离环境（作业对象），没有开始";return null;}
   ProcessInformation pi;
   lock(App.StartGate){try{
   var sa=new SecurityAttributes{nLength=System.Runtime.InteropServices.Marshal.SizeOf(typeof(SecurityAttributes)),bInheritHandle=true};
   if(!CreatePipe(out a.OutRead,out a.OutWrite,ref sa,0)||!CreatePipe(out a.ErrRead,out a.ErrWrite,ref sa,0)){a.Why="不能启动生成程序（管道）";return null;}
   if(PipesCreated!=null)PipesCreated(new[]{a.OutRead,a.OutWrite,a.ErrRead,a.ErrWrite});
   // the reading ends stay here; only the child's ends may be inherited
   if(!SetHandleInformation(a.OutRead,1,0)||!SetHandleInformation(a.ErrRead,1,0)){a.Why="不能启动生成程序（管道设置）";return null;}
   a.Nul=CreateFile("NUL",0x80000000,3,ref sa,3,0,IntPtr.Zero);   // no input
   if(a.Nul==Invalid){a.Why="不能启动生成程序（输入）";return null;}
   var si=new StartupInfoEx{StartupInfo=new StartupInfo{cb=System.Runtime.InteropServices.Marshal.SizeOf(typeof(StartupInfoEx)),dwFlags=0x100,hStdInput=a.Nul,hStdOutput=a.OutWrite,hStdError=a.ErrWrite}};
   // only these three are inherited: anything else Matte holds open (another pipe, a file) stays out of the generator
   if(!a.InheritOnly(new[]{a.Nul,a.OutWrite,a.ErrWrite})){a.Why="不能启动生成程序（句柄列表）";return null;}
   si.lpAttributeList=a.Attributes;
   if(!CreateProcess(null,new System.Text.StringBuilder("\""+exe+"\" "+args),IntPtr.Zero,IntPtr.Zero,true,CreateSuspended|CreateNoWindow|ExtendedStartupInfo,IntPtr.Zero,null,ref si,out pi)){
    a.Why="不能启动生成程序（错误 "+System.Runtime.InteropServices.Marshal.GetLastWin32Error()+"）";return null;}
   a.Process=pi.hProcess;a.Thread=pi.hThread;
   if(InWindow!=null)InWindow();
   }finally{
    // No inheritable handle escapes the gate, even when pipe setup or process creation fails.
    if(a.OutWrite!=IntPtr.Zero){CloseHandle(a.OutWrite);a.OutWrite=IntPtr.Zero;}
    if(a.ErrWrite!=IntPtr.Zero){CloseHandle(a.ErrWrite);a.ErrWrite=IntPtr.Zero;}
    if(a.Nul!=Invalid&&a.Nul!=IntPtr.Zero){CloseHandle(a.Nul);a.Nul=Invalid;}
    if(a.Process==IntPtr.Zero){
     if(a.OutRead!=IntPtr.Zero){CloseHandle(a.OutRead);a.OutRead=IntPtr.Zero;}
     if(a.ErrRead!=IntPtr.Zero){CloseHandle(a.ErrRead);a.ErrRead=IntPtr.Zero;}
    }
   }}
   if(BeforeAssign!=null)BeforeAssign();
   if(FailAssign||!GenJob.Assign(a.Job,a.Process)){a.Why="不能把生成程序放进隔离环境，没有开始";return null;}
   a.Assigned=true;
   g=new GenProcess{Id=pi.dwProcessId,Job=a.Job,process=a.Process};
   g.outReader=g.Reader(ref a.OutRead,line,0);
   g.errReader=g.Reader(ref a.ErrRead,null,FailReaderStage);
   if(FailResume||ResumeThread(a.Thread)==Infinite){a.Why="不能让生成程序开始运行";return null;}
   CloseHandle(a.Thread);a.Thread=IntPtr.Zero;
   ok=true;return g;
  }
  catch(Exception e){a.Why="不能启动生成程序："+e.Message;return null;}
  finally{
   a.Release();
   if(!ok)a.Rollback();   // a reader already running sees its pipe end when the process is gone, and stops
   why=ok?null:a.Why;
  }
 }

 // A pipe read to its end on its own thread. A failure (reading, or `line` throwing) is kept for Wait to report; after
 // it the pipe is still read to its end, lines dropped, so the generator never blocks on a full pipe.
 System.Threading.Thread Reader(ref IntPtr pipe,Action<string> line,int failStage){
  Microsoft.Win32.SafeHandles.SafeFileHandle handle=null;FileStream fs=null;
  try{
  if(failStage==1)throw new OutOfMemoryException("test: before the reader owns its handle");
  handle=new Microsoft.Win32.SafeHandles.SafeFileHandle(pipe,true);pipe=IntPtr.Zero;
  fs=new FileStream(handle,FileAccess.Read,4096);
  if(failStage==2)throw new OutOfMemoryException("test: before the reader thread exists");
  var t=new System.Threading.Thread(()=>{
   try{
    using(var r=new StreamReader(fs,new UTF8Encoding(false))){
     string l;int count=0;
     while((l=r.ReadLine())!=null){
      if(FailRead&&++count==2)throw new IOException("test: the read fails");
      if(line==null)continue;
      try{line(l);}catch(Exception e){if(readError==null)readError=e;line=null;}
     }
    }
   }
   catch(Exception e){if(readError==null)readError=e;try{fs.Dispose();}catch{}}
  }){IsBackground=true,Name="Spatial generation output"};
  if(failStage==3)throw new OutOfMemoryException("test: before the reader thread starts");
  t.Start();
  return t;
  }catch{try{if(fs!=null)fs.Dispose();}finally{if(handle!=null)handle.Dispose();}throw;}
 }
 // Returns once the process has exited and both pipes are read to their end; throws if any of that failed.
 public void Wait(){
  if(WaitForSingleObject(process,Infinite)!=WaitObject0)throw new IOException("the wait for the generator failed");
  outReader.Join();errReader.Join();
  if(readError!=null)throw new IOException("its output could not be read to the end: "+readError.Message,readError);
 }
 public bool HasExited{get{return WaitForSingleObject(process,0)==WaitObject0;}}
 public int ExitCode{get{uint c;return GetExitCodeProcess(process,out c)?(int)c:-1;}}
 public void Close(){if(closed)return;closed=true;CloseHandle(process);GenJob.Close(Job);}
 // the x64 layouts of the native structures (the regression tests pin them)
 internal static int[] Layout(){return new[]{System.Runtime.InteropServices.Marshal.SizeOf(typeof(SecurityAttributes)),System.Runtime.InteropServices.Marshal.SizeOf(typeof(StartupInfo)),System.Runtime.InteropServices.Marshal.SizeOf(typeof(ProcessInformation)),
  System.Runtime.InteropServices.Marshal.SizeOf(typeof(StartupInfoEx)),(int)System.Runtime.InteropServices.Marshal.OffsetOf(typeof(StartupInfo),"hStdInput"),(int)System.Runtime.InteropServices.Marshal.OffsetOf(typeof(StartupInfo),"hStdOutput"),(int)System.Runtime.InteropServices.Marshal.OffsetOf(typeof(StartupInfo),"hStdError")};}
}
