using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

// 安装 SHARP: Matte's own Spatial generator, installed on demand into spatial\runtime next to the exe. uv (pinned,
// checked) makes Matte's own Python with the locked packages from spatial\requirements-*.txt; Apple's SHARP weights are
// downloaded from Apple only after the user accepts Apple's license on the panel (Matte never ships them). Then one
// stock Windows picture is made as a trial. Nothing outside spatial\runtime is touched; deleting it uninstalls.
// The work runs on its own thread and keeps going with the panel closed; downloads resume after a break or a restart.
partial class App {
 const string UvVersion="0.12.23",UvSha256="75D05DE6762778C31EE183398DE7DD15093FAD0ED90B1F236D8205EA5EC00C90";
 const string PythonBuild="cpython-3.12.15-windows-x86_64-none";
 const string WeightsUrl="https://ml-site.cdn-apple.com/models/sharp/sharp_2572gikvuh.pt",WeightsName="sharp_2572gikvuh.pt";
 const string WeightsSha256="94211A75198C47F61FCA7D739BA08A215418D8D398D48FDDF023BACCC24F073D";const long WeightsBytes=2809738232;
 internal const string SharpLicenseUrl="https://github.com/apple/ml-sharp/blob/main/LICENSE_MODEL";
 // what the packages download (measured from the locked lists) and what the whole install takes on disk
 const long PackagesCpu=200L<<20,PackagesGpu=2850L<<20,DiskCpu=3600L<<20,DiskGpu=7500L<<20;
 // how much the package step writes (uv's unpacked cache plus the environment it copies into): its progress. CPU
 // measured in the 2026-10 test install (650 + 670 MB); GPU estimated from the size of the CUDA build of torch.
 const long PackagesWorkCpu=1320L<<20,PackagesWorkGpu=8600L<<20;
 static string SharpRoot{get{return Path.Combine(OwnSpatialDir,"runtime");}}
 static string SharpPython{get{return Path.Combine(SharpRoot,"env","Scripts","python.exe");}}
 static string SharpModel{get{return Path.Combine(SharpRoot,"model",WeightsName);}}
 static string SharpDoneFile{get{return Path.Combine(SharpRoot,"installed.json");}}
 bool SharpInstalled{get{return File.Exists(SharpDoneFile)&&File.Exists(SharpPython)&&File.Exists(SharpModel);}}
 // An install was started and not finished (Matte closed meanwhile): the page offers to continue.
 bool SharpUnfinished{get{return !SharpInstalled&&File.Exists(Path.Combine(SharpRoot,"installing.txt"));}}

 // ---------------------------------------------------------------- the computer check
 internal class SharpCheck{public long Free,Need;public bool DiskOk;public long Ram;public bool RamOk;public string Gpu;public long Vram;public bool GpuCan;}
 [StructLayout(LayoutKind.Sequential)] class MemoryStatusEx{public uint dwLength=64,dwMemoryLoad;public ulong ullTotalPhys,ullAvailPhys,ullTotalPageFile,ullAvailPageFile,ullTotalVirtual,ullAvailVirtual,ullAvailExtendedVirtual;}
 [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In,Out] MemoryStatusEx m);
 internal SharpCheck CheckComputer(bool gpu){
  var c=new SharpCheck();
  try{var root=Path.GetPathRoot(Path.GetFullPath(Dir));c.Free=new DriveInfo(root).AvailableFreeSpace;}catch{c.Free=0;}
  string gpuName;long vram;BestNvidia(out gpuName,out vram);c.Gpu=gpuName;c.Vram=vram;
  // SHARP took 7.0 GB of graphics memory in the 2026-10 test; 8 GB cards are the smallest that work
  c.GpuCan=gpuName!=null&&vram>=(long)(7.5*(1L<<30));
  c.Need=(gpu&&c.GpuCan?DiskGpu:DiskCpu)-Math.Min(UsedSoFar(),DiskCpu/2);
  c.DiskOk=c.Free>=c.Need;
  var m=new MemoryStatusEx();if(GlobalMemoryStatusEx(m))c.Ram=(long)m.ullTotalPhys;
  c.RamOk=c.Ram>=(long)(15.5*(1L<<30));   // 16 GB recommended; less still works, slower
  return c;
 }
 static long UsedSoFar(){try{return Directory.Exists(SharpRoot)?new DirectoryInfo(SharpRoot).EnumerateFiles("*",SearchOption.AllDirectories).Sum(f=>f.Length):0;}catch{return 0;}}
 // The NVIDIA adapter with the most memory, from the display drivers' registry entries (read only).
 static void BestNvidia(out string name,out long vram){
  name=null;vram=0;
  try{using(var cls=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}")){
   if(cls==null)return;
   foreach(string sub in cls.GetSubKeyNames()){if(!Regex.IsMatch(sub,@"^\d{4}$"))continue;
    using(var k=cls.OpenSubKey(sub)){if(k==null)continue;string desc=k.GetValue("DriverDesc") as string;if(desc==null||desc.IndexOf("NVIDIA",StringComparison.OrdinalIgnoreCase)<0)continue;
     object q=k.GetValue("HardwareInformation.qwMemorySize");long mem=q is long?(long)q:q is byte[]&&((byte[])q).Length>=8?BitConverter.ToInt64((byte[])q,0):0;
     if(mem==0){object d=k.GetValue("HardwareInformation.MemorySize");mem=d is int?(long)(uint)(int)d:d is byte[]&&((byte[])d).Length>=4?BitConverter.ToUInt32((byte[])d,0):0;}
     if(mem>vram){vram=mem;name=desc.Replace("NVIDIA ","").Replace("GeForce ","");}}}}}
  catch(Exception e){Log("GPU check: "+e.Message);}
 }

 // ---------------------------------------------------------------- the install
 internal enum SharpStage{None,Env,Model,Trial,Done,Failed}
 internal SharpStage sharpStage;internal string sharpError,sharpErrorKind;internal bool sharpPaused,sharpGpu;
 internal long sharpDone,sharpTotal;internal double sharpTrialSeconds;internal bool sharpFoundModel;
 Thread sharpThread;volatile bool sharpCancel;Process sharpProc;DateTime sharpStarted;long sharpDoneAtStart;
 internal bool SharpBusy{get{return sharpThread!=null&&sharpThread.IsAlive;}}
 internal void StartSharpInstall(bool gpu){
  if(SharpBusy)return;
  sharpGpu=gpu;sharpCancel=false;sharpPaused=false;sharpError=null;sharpErrorKind=null;sharpStage=SharpStage.Env;
  sharpTotal=(gpu?PackagesGpu:PackagesCpu)+50L*(1<<20)+WeightsBytes;sharpDone=0;sharpStarted=DateTime.Now;sharpDoneAtStart=-1;
  Directory.CreateDirectory(SharpRoot);File.WriteAllText(Path.Combine(SharpRoot,"installing.txt"),gpu?"cu128":"cpu");
  try{File.Delete(SharpDoneFile);}catch{}   // a switch of edition: not usable until it is done (nothing is generated meanwhile)
  sharpBytes=-1;
  sharpThread=new Thread(SharpWork){IsBackground=true,Name="SHARP install",Priority=ThreadPriority.BelowNormal};sharpThread.Start();
  Log("SHARP install started ("+(gpu?"GPU":"CPU")+")");SharpChanged();
 }
 // A second click within 3 s cancels (the page arms the button first); everything installed so far is removed.
 internal void CancelSharpInstall(){
  sharpCancel=true;sharpPaused=false;
  try{if(sharpProc!=null&&!sharpProc.HasExited){ResumeTree(sharpProc.Id);KillTree(sharpProc.Id);}}catch{}
  var t=sharpThread;ThreadPool.QueueUserWorkItem(_=>{if(t!=null)t.Join(15000);RemoveSharp();Post(()=>{sharpStage=SharpStage.None;SharpChanged();});});
 }
 internal void PauseSharpInstall(bool pause){
  sharpPaused=pause;
  try{if(sharpProc!=null&&!sharpProc.HasExited){if(pause)SuspendTree(sharpProc.Id);else ResumeTree(sharpProc.Id);}}catch{}
  SharpChanged();
 }
 internal static void RemoveSharp(){for(int i=0;i<5&&Directory.Exists(SharpRoot);i++){DeleteTree(SharpRoot);if(Directory.Exists(SharpRoot))Thread.Sleep(600);}}
 // rmdir with a \\?\ path: the packages' folders go deeper than the old 260-character limit, which Directory.Delete
 // (without long-path support switched on in Windows) cannot pass
 static void DeleteTree(string dir){
  if(!Directory.Exists(dir))return;
  try{using(var p=StartDirect(new ProcessStartInfo("cmd.exe","/d /c rmdir /s /q \"\\\\?\\"+Path.GetFullPath(dir)+"\""){UseShellExecute=false,CreateNoWindow=true}))p.WaitForExit(120000);}catch{}
  try{if(Directory.Exists(dir))Directory.Delete(dir,true);}catch{}
 }
 void Post(Action a){try{if(ui!=null)ui.BeginInvoke(a);else if(panel!=null)panel.Dispatcher.BeginInvoke(a);else a();}catch{}}
 void SharpFail(string kind,string detail){sharpErrorKind=kind;sharpError=detail;sharpStage=SharpStage.Failed;Log("SHARP install: "+kind+": "+detail);}

 void SharpWork(){
  try{
   string dl=Path.Combine(SharpRoot,"download");Directory.CreateDirectory(dl);
   // 1. uv, the tool that makes the Python environment
   string uv=Path.Combine(SharpRoot,"uv","uv.exe");
   if(!File.Exists(uv)){
    string zip=Path.Combine(dl,"uv.zip");
    if(!Download("https://github.com/astral-sh/uv/releases/download/"+UvVersion+"/uv-x86_64-pc-windows-msvc.zip",zip,0,null))return;
    if(!HashIs(zip,UvSha256)){File.Delete(zip);SharpFail("verify","uv");return;}
    Directory.CreateDirectory(Path.GetDirectoryName(uv));
    using(var a=System.IO.Compression.ZipFile.OpenRead(zip)){var e=a.Entries.First(x=>x.Name.Equals("uv.exe",StringComparison.OrdinalIgnoreCase));System.IO.Compression.ZipFileExtensions.ExtractToFile(e,uv,true);}
    File.Delete(zip);
   }
   sharpDone=50L<<20;
   // 2. Python and the locked packages, all inside spatial\runtime (no system Python, no user config)
   // (switching edition: the same environment, synced to the other locked list - only the PyTorch build changes)
   string okFile=Path.Combine(SharpRoot,"env","packages.ok"),edition=sharpGpu?"cu128":"cpu";
   if(!File.Exists(okFile)||File.ReadAllText(okFile).Trim()!=edition){
    string cache=Path.Combine(SharpRoot,"cache"),req=Path.Combine(OwnSpatialDir,sharpGpu?"requirements-cu128.txt":"requirements-cpu.txt");
    if(!File.Exists(req)){SharpFail("files","缺少 "+Path.GetFileName(req));return;}
    long expect=sharpGpu?PackagesGpu:PackagesCpu;
    if(!File.Exists(SharpPython)){
     if(!Uv(uv,"python install "+PythonBuild,cache,0))return;
     if(!Uv(uv,"venv \""+Path.Combine(SharpRoot,"env")+"\" --python "+PythonBuild+" --python-preference only-managed",cache,0))return;
    }
    try{File.Delete(okFile);}catch{}
    if(!Uv(uv,"pip sync --python \""+SharpPython+"\" \""+req+"\" --require-hashes --no-build --torch-backend "+edition,cache,expect,sharpGpu?PackagesWorkGpu:PackagesWorkCpu))return;
    File.WriteAllText(okFile,edition);
    DeleteTree(cache);   // the downloaded packages are installed (copied); their cache is not needed
   }
   sharpDone=(50L<<20)+(sharpGpu?PackagesGpu:PackagesCpu);
   // 3. the model, from Apple (or a copy already on this computer from SHARP's own tools)
   sharpStage=SharpStage.Model;Post(SharpChanged);
   if(!File.Exists(SharpModel)){
    Directory.CreateDirectory(Path.GetDirectoryName(SharpModel));
    string part=Path.Combine(dl,WeightsName+".part");
    string local=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".cache","torch","hub","checkpoints",WeightsName);
    if(File.Exists(local)&&new FileInfo(local).Length==WeightsBytes&&HashIs(local,WeightsSha256)){
     sharpFoundModel=true;if(!CreateHardLink(SharpModel,local,IntPtr.Zero))File.Copy(local,SharpModel,true);Log("SHARP model: the copy already on this computer");
    }else{
     long before=sharpDone;
     if(!Download(WeightsUrl,part,before,WeightsBytes))return;
     if(new FileInfo(part).Length!=WeightsBytes||!HashIs(part,WeightsSha256)){File.Delete(part);SharpFail("verify","model");return;}
     File.Move(part,SharpModel);
    }
   }
   sharpDone=sharpTotal;
   // 4. one stock picture made as a trial: the environment, the model and the slicer all work, and how long one takes
   sharpStage=SharpStage.Trial;Post(SharpChanged);
   string sample=new[]{@"Web\Wallpaper\Windows\img0.jpg",@"Web\Wallpaper\Windows\img19.jpg",@"Web\Screen\img100.jpg"}.Select(p=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),p)).FirstOrDefault(File.Exists);
   if(sample==null){SharpFail("trial","找不到用来试运行的系统图片");return;}
   string trialOut=Path.Combine(SharpRoot,"trial.spatial"),output;
   int code=RunHidden(SharpPython,"-E -s -u \""+Path.Combine(OwnSpatialDir,"matte_sharp.py")+"\" --trial \""+sample+"\" --out \""+trialOut+"\" --checkpoint \""+SharpModel+"\" --width 2560 --height 1600",null,out output);
   if(sharpCancel)return;
   var m=Regex.Match(output??"",@"Trial ok: ([\d.]+) s");
   if(code!=0||!m.Success||!File.Exists(trialOut)){File.WriteAllText(Path.Combine(SharpRoot,"trial.log"),output??"",Encoding.UTF8);SharpFail("trial","exit "+code);return;}
   try{File.Delete(trialOut);}catch{}
   sharpTrialSeconds=double.Parse(m.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);
   File.WriteAllText(SharpDoneFile,"{\"edition\":\""+(sharpGpu?"cu128":"cpu")+"\",\"secondsPerPhoto\":"+sharpTrialSeconds.ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+",\"installed\":\""+DateTime.Now.ToString("s")+"\"}");
   try{File.Delete(Path.Combine(SharpRoot,"installing.txt"));}catch{}DeleteTree(dl);
   sharpStage=SharpStage.Done;Log("SHARP installed, trial "+sharpTrialSeconds+" s per photo");
   Post(()=>{if(!PanelOpen)try{tray.ShowBalloonTip(3000,"Matte","SHARP 已就绪，可以生成 Spatial 图片了",System.Windows.Forms.ToolTipIcon.Info);}catch{}});
  }catch(Exception e){if(!sharpCancel)SharpFail("other",e.Message);}
  finally{sharpProc=null;Post(SharpChanged);}
 }
 internal double SharpSecondsPerPhoto{get{
  if(sharpTrialSeconds>0)return sharpTrialSeconds;
  try{var m=Regex.Match(File.ReadAllText(SharpDoneFile),"\"secondsPerPhoto\":([\\d.]+)");if(m.Success)return double.Parse(m.Groups[1].Value,System.Globalization.CultureInfo.InvariantCulture);}catch{}
  return 50;}}
 internal string SharpEditionTag{get{try{return File.ReadAllText(SharpDoneFile).Contains("cu128")?"gpu":"cpu";}catch{return "cpu";}}}
 internal string SharpEdition{get{return SharpEditionTag=="gpu"?"显卡版":"CPU 版";}}

 // Downloads url into file, resuming what is there; progress adds to sharpDone from base. False when cancelled or failed.
 bool Download(string url,string file,long @base,long? size){
  // .NET Framework does not offer TLS 1.2 / 1.3 by itself; GitHub and Apple need them
  try{ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12|(SecurityProtocolType)12288;}catch(NotSupportedException){ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;}
  for(int attempt=0;;attempt++){
   if(sharpCancel)return false;
   long have=File.Exists(file)?new FileInfo(file).Length:0;
   if(size.HasValue&&have==size.Value)return true;
   try{
    var req=(HttpWebRequest)WebRequest.Create(url);req.UserAgent="Matte";req.Timeout=30000;req.ReadWriteTimeout=60000;
    if(have>0)req.AddRange(have);
    using(var resp=(HttpWebResponse)req.GetResponse()){
     bool partial=resp.StatusCode==HttpStatusCode.PartialContent;
     using(var src=resp.GetResponseStream())using(var dst=new FileStream(file,partial?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.Read)){
      if(!partial)have=0;var buf=new byte[1<<16];int n;var last=DateTime.MinValue;
      while((n=src.Read(buf,0,buf.Length))>0){
       if(sharpCancel)return false;
       while(sharpPaused&&!sharpCancel)Thread.Sleep(200);
       dst.Write(buf,0,n);have+=n;sharpDone=@base+have;
       if((DateTime.Now-last).TotalMilliseconds>400){last=DateTime.Now;Post(SharpChanged);}
      }
     }
    }
    if(!size.HasValue||new FileInfo(file).Length>=size.Value)return true;
   }catch(WebException e){
    var r=e.Response as HttpWebResponse;
    if(r!=null&&(int)r.StatusCode==416&&size.HasValue&&have>=size.Value)return true;
    if(r!=null&&((int)r.StatusCode==404||(int)r.StatusCode==403)){SharpFail("source",url+": "+(int)r.StatusCode);return false;}
    if(attempt>=2){SharpFail("network",e.Message);return false;}
    Thread.Sleep(3000);
   }catch(IOException e){
    if(IsDiskFull(e)){SharpFail("disk",e.Message);return false;}
    if(attempt>=2){SharpFail("network",e.Message);return false;}
    Thread.Sleep(3000);
   }
  }
 }
 static bool IsDiskFull(IOException e){int hr=Marshal.GetHRForException(e)&0xFFFF;return hr==112||hr==39;}
 static bool HashIs(string file,string sha){using(var s=File.OpenRead(file))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").Equals(sha,StringComparison.OrdinalIgnoreCase);}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool CreateHardLink(string link,string existing,IntPtr sa);

 // uv with Matte's own folders only: its cache and Python inside spatial\runtime, no user config, files copied
 // (not linked) so the cache can go. expect: bytes it downloads, measured from the cache folder as it fills.
 bool Uv(string uv,string args,string cache,long expect,long work=0){
  var env=new Dictionary<string,string>{{"UV_CACHE_DIR",cache},{"UV_PYTHON_INSTALL_DIR",Path.Combine(SharpRoot,"python")},{"UV_NO_CONFIG","1"},{"UV_LINK_MODE","copy"},{"UV_PYTHON_DOWNLOADS","automatic"},{"UV_NO_PROGRESS","1"}};
  long start=sharpDone;string output;
  string envDir=Path.Combine(SharpRoot,"env");int ticks=0;
  int code=RunHidden(uv,args,env,out output,work>0?(Action)(()=>{if(ticks++%2!=0)return;long have=FolderBytes(cache)+FolderBytes(envDir);sharpDone=start+(long)(expect*Math.Min(.99,have/(double)work));Post(SharpChanged);}):null);
  if(sharpCancel)return false;
  if(code!=0){
   File.WriteAllText(Path.Combine(SharpRoot,"install.log"),"uv "+args+"\r\n"+output,Encoding.UTF8);
   string o=output??"";
   SharpFail(o.IndexOf("not enough space",StringComparison.OrdinalIgnoreCase)>=0||o.Contains("os error 112")?"disk":
    Regex.IsMatch(o,"(dns|connect|timed out|network|Failed to fetch|error sending request)",RegexOptions.IgnoreCase)?"network":"env","uv exit "+code);
   return false;}
  return true;
 }
 static long FolderBytes(string dir){try{return Directory.Exists(dir)?new DirectoryInfo(dir).EnumerateFiles("*",SearchOption.AllDirectories).Sum(f=>f.Length):0;}catch{return 0;}}
 int RunHidden(string exe,string args,Dictionary<string,string> env,out string output,Action tick=null){
  var psi=new ProcessStartInfo(exe,args){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=SharpRoot};
  psi.EnvironmentVariables["PYTHONIOENCODING"]="utf-8";
  if(env!=null)foreach(var kv in env)psi.EnvironmentVariables[kv.Key]=kv.Value;
  var sb=new StringBuilder();
  using(var p=new Process{StartInfo=psi}){
   p.OutputDataReceived+=(s,a)=>{if(a.Data!=null)lock(sb)sb.AppendLine(a.Data);};p.ErrorDataReceived+=(s,a)=>{if(a.Data!=null)lock(sb)sb.AppendLine(a.Data);};
   lock(StartGate)p.Start();sharpProc=p;try{p.PriorityClass=ProcessPriorityClass.BelowNormal;}catch{}
   p.BeginOutputReadLine();p.BeginErrorReadLine();
   while(!p.WaitForExit(1000)){if(sharpCancel){try{KillTree(p.Id);}catch{}break;}if(tick!=null&&!sharpPaused)tick();}
   p.WaitForExit(5000);sharpProc=null;
   lock(sb)output=sb.ToString();
   try{return p.HasExited?p.ExitCode:-1;}catch{return -1;}
  }
 }

 // ---------------------------------------------------------------- process trees: pause, resume, stop
 [DllImport("ntdll.dll")] static extern int NtSuspendProcess(IntPtr h);
 [DllImport("ntdll.dll")] static extern int NtResumeProcess(IntPtr h);
 [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access,bool inherit,int pid);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
 [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool Process32First(IntPtr snap,ref ProcessEntry e);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern bool Process32Next(IntPtr snap,ref ProcessEntry e);
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct ProcessEntry{public uint dwSize,cntUsage,th32ProcessID;public IntPtr th32DefaultHeapID;public uint th32ModuleID,cntThreads,th32ParentProcessID;public int pcPriClassBase;public uint dwFlags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string szExeFile;}
 static List<int> Tree(int root){
  var kids=new Dictionary<int,List<int>>();IntPtr snap=CreateToolhelp32Snapshot(2,0);
  try{var e=new ProcessEntry{dwSize=(uint)Marshal.SizeOf(typeof(ProcessEntry))};
   if(Process32First(snap,ref e))do{int parent=(int)e.th32ParentProcessID;if(!kids.ContainsKey(parent))kids[parent]=new List<int>();kids[parent].Add((int)e.th32ProcessID);}while(Process32Next(snap,ref e));}
  finally{CloseHandle(snap);}
  var all=new List<int>{root};for(int i=0;i<all.Count;i++){List<int> k;if(kids.TryGetValue(all[i],out k))foreach(int c in k)if(!all.Contains(c))all.Add(c);}
  return all;
 }
 static void EachInTree(int root,Func<IntPtr,int> act){foreach(int pid in Tree(root)){IntPtr h=OpenProcess(0x0800,false,pid);if(h!=IntPtr.Zero){act(h);CloseHandle(h);}}}
 static void SuspendTree(int root){EachInTree(root,NtSuspendProcess);}
 static void ResumeTree(int root){EachInTree(root,NtResumeProcess);}
 static void KillTree(int root){foreach(int pid in Enumerable.Reverse(Tree(root)))try{Process.GetProcessById(pid).Kill();}catch{}}
}
