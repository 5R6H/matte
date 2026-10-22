// Matte's regression tests: compiled together with Matte's own sources (run-tests.ps1), so they reach its internals.
// Nothing here touches the real desktop: the wallpaper service is a stand-in (TestWall), the panel stays off screen,
// and every file is made in a folder of its own under tests\bin.
// Each test is a case that went wrong once:
//  ② the 存储与清理 page swept the library again and again while it stayed open
//  ③ a first start took over Windows' own picture position ("fill" crops the photo and stops the cross-fade)
//  ④ the colour analysis read phone photos on their side (EXIF orientation), and so did WPF's decodes
//  ⑧ the Spatial generation counted a photo as made when it started, failed ones included
//  ⑨ a damaged settings file stopped Matte from starting; settings were overwritten in place
using System;using System.Collections.Generic;using System.Drawing;using System.Drawing.Imaging;using System.IO;using System.Linq;
using System.Reflection;using System.Runtime.Serialization;using System.Web.Script.Serialization;using System.Windows.Threading;

class TestWall:IWallpaper {
 public string Wallpaper;public uint Color=0x202020;public int Position=4;   // Windows set to "fill" before Matte
 public void SetWallpaper(string monitor,string path){Wallpaper=path;}
 public string GetWallpaper(string monitor){return Wallpaper;}
 public string GetMonitorDevicePathAt(uint index){return @"\\?\TEST#DISPLAY1";}
 public uint GetMonitorDevicePathCount(){return 1;}
 public void GetMonitorRECT(string monitor,out Rect rect){rect=new Rect();}   // no rectangle: SynchronizedFade stays off
 public void SetBackgroundColor(uint color){Color=color;}
 public uint GetBackgroundColor(){return Color;}
 public void SetPosition(int position){Position=position;}
 public int GetPosition(){return Position;}
 public void SetSlideshow(IShellItemArray items){}
 public IShellItemArray GetSlideshow(){return null;}
 public void SetSlideshowOptions(uint options,uint milliseconds){}
 public void GetSlideshowOptions(out uint options,out uint milliseconds){options=0;milliseconds=1800000;}
 public void AdvanceSlideshow(string monitor,uint direction){}
 public uint GetStatus(){return 0;}
 public void Enable(bool enabled){}
}

partial class App {
 // A Matte for tests: the stand-in wallpaper service, the slideshow not started (Init starts it the way App() does).
 internal App(IWallpaper testWall){wall=testWall;fade=new System.Windows.Forms.Timer{Interval=32};}
 internal void TestInit(){InitSlideshow();}
 internal MatteSettings TestSettings{get{return settings;}set{settings=value;}}
 internal int TestSweeps{get{return sweepsStarted;}}
 internal void TestOpenStore(){
  ui=new MessageWindow();CreatePanel();panel.ShowActivated=false;panel.ShowInTaskbar=false;panel.Left=-20000;panel.Top=-20000;panel.Show();
  page="spatialstore";RefreshStore();
 }
 internal void TestClose(){if(panel!=null)panel.Close();if(ui!=null)ui.Dispose();}
 internal static uint TestAnalyze(string p){return Analyze(p);}
 internal void TestLoadColourCache(){LoadColourCache();}
 internal uint TestColorFor(string p){return ColorFor(p);}
 internal static string TestKey(string p){return Key(p);}
 internal bool TestGenerating{get{return Generating;}}
 internal int TestGenDone{get{return genDone;}}
 internal int TestGenFailed{get{return genFailed;}}
 internal string TestRunGenerator(string script){var d=Dispatcher.CurrentDispatcher;genPost=a=>d.BeginInvoke(a);return RunGenerator("cmd.exe","/d /c \""+script+"\"");}
 // the end of a run as GenFinished handles it: these lines seen, a generator that exited with 0, stopped or not
 internal void TestFinish(bool stopped,params string[] lines){
  string why;var g=GenProcess.Start("cmd.exe","/d /c exit 0",l=>{},out why);g.Wait();
  gen=g;genActive=true;genStopped=stopped;genProgress=new GenProgress();foreach(var l in lines)GenLine(l);GenFinished();g.Close();}
 internal bool TestStuck{get{return genStuck;}}
 internal string TestPhase{get{return genPhase;}}
 internal int TestActiveAtRelease{get{return genActiveAtRelease;}}
 internal void TestStop(){StopGeneration();}
 // what the panel shows about generation (its 生成 page and Spatial page summary)
 internal string TestPanelGenText(){
  if(panel==null){CreatePanel();panel.ShowActivated=false;panel.ShowInTaskbar=false;panel.Left=-20000;panel.Top=-20000;panel.Show();}
  GenChanged();PaintGenSummary();return P<System.Windows.Controls.TextBlock>("GenCounts").Text+" | "+P<System.Windows.Controls.TextBlock>("GenSummaryNote").Text+" | "+P<System.Windows.Controls.Button>("GenBtn").Content;}
}

static class MatteTests {
 [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)] static extern bool DuplicateHandle(IntPtr sourceProcess,IntPtr source,IntPtr targetProcess,out IntPtr target,uint access,bool inherit,uint options);
 [System.Runtime.InteropServices.DllImport("kernelbase.dll")] static extern bool CompareObjectHandles(IntPtr first,IntPtr second);
 [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 static int failures;
 static string Here{get{return AppDomain.CurrentDomain.BaseDirectory;}}
 static string Fresh(string name){string d=Path.Combine(Here,"t-"+name);if(Directory.Exists(d))Directory.Delete(d,true);Directory.CreateDirectory(d);return d;}
 static void Check(bool ok,string what){if(!ok)throw new Exception(what);}
 static void Run(string name,Action test){
  try{test();Console.WriteLine("PASS  "+name);}
  catch(Exception e){failures++;Console.WriteLine("FAIL  "+name+": "+(e is TargetInvocationException&&e.InnerException!=null?e.InnerException.Message:e.Message));}
 }
 static void Pump(int ms){var f=new DispatcherFrame();var t=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};t.Tick+=delegate{t.Stop();f.Continue=false;};t.Start();Dispatcher.PushFrame(f);}
 // Matte reads settings.json from the folder its exe is in: here, tests\bin
 static string SettingsFile{get{return Path.Combine(Here,"settings.json");}}
 static void ClearSettings(){foreach(var x in new[]{"",".bak",".bad",".tmp"})if(File.Exists(SettingsFile+x))File.Delete(SettingsFile+x);}

 [STAThread] static int Main(string[] args){
  if(args.Contains("--store")){StoreTests();return failures;}
  IdentityTests();if(args.Contains("--identity"))return failures;
  StoreTests();
  // ② the store page's sweep runs once, not again and again
  Run("2 store page sweeps once",()=>{
   string photos=Fresh("store-photos");for(int i=0;i<3;i++)Solid(Path.Combine(photos,"p"+i+".jpg"),Color.FromArgb(40*i,90,140),64,48,1);
   var app=new App(new TestWall());var s=new MatteSettings();s.Playlist=new List<string>{photos};s.SpatialFolder=Fresh("store-cache");app.TestSettings=s;
   app.TestOpenStore();Pump(4000);int n=app.TestSweeps;app.TestClose();
   Check(n==1,"the open page started "+n+" sweeps in 4 s (want 1)");
  });

  // ③ first start: the photo whole (fit), whatever Windows was set to; an inherited "fill" goes back to fit
  Run("3 first start shows the whole photo",()=>{
   ClearSettings();var wall=new TestWall{Position=4};var app=new App(wall);app.TestInit();
   Check(app.TestSettings.Position==3,"position "+app.TestSettings.Position+" (want 3, fit)");
   Check(File.Exists(SettingsFile),"settings.json not written");
  });
  Run("3 an inherited fill goes back to fit",()=>{
   ClearSettings();var old=new MatteSettings();old.Position=4;old.Playlist=new List<string>();File.WriteAllText(SettingsFile,new JavaScriptSerializer().Serialize(old));
   var app=new App(new TestWall());app.TestInit();
   Check(app.TestSettings.Position==3,"position "+app.TestSettings.Position+" (want 3)");
  });

  // ④ phone photos: stored on their side with an EXIF orientation, read as Windows shows them
  Run("4 colour analysis reads the photo upright",()=>{
   string d=Fresh("exif");
   string tagged=Path.Combine(d,"tagged.jpg"),upright=Path.Combine(d,"upright.jpg");
   using(var b=Pattern(200,100))Save(b,tagged,6);   // stored 200x100, shown 100x200 (turned clockwise)
   using(var b=Pattern(200,100)){b.RotateFlip(RotateFlipType.Rotate90FlipNone);Save(b,upright,1);}
   double wide=16/9.0;
   var a=ColorModel.Load(tagged,wide);var r=ColorModel.Load(upright,wide);
   Check(r.LeftRight,"the upright portrait photo should have its bars left and right on a 16:9 screen");
   Check(a.LeftRight==r.LeftRight,"the tagged photo was read on its side (LeftRight "+a.LeftRight+")");
   uint ca=App.TestAnalyze(tagged),cr=App.TestAnalyze(upright);
   Check(Near(ca,cr,12),"classic colour #"+ca.ToString("X6")+" vs #"+cr.ToString("X6")+" upright");
  });
  Run("4 WPF and GDI+ turn all eight orientations alike",()=>{
   string d=Fresh("exif8");
   for(int o=1;o<=8;o++){
    string p=Path.Combine(d,"o"+o+".jpg");using(var b=Pattern(120,60))Save(b,p,o);
    Bitmap g;using(var im=App.LoadUpright(p))g=new Bitmap(im);
    var bi=new System.Windows.Media.Imaging.BitmapImage();bi.BeginInit();bi.CacheOption=System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;bi.UriSource=new Uri(p);bi.EndInit();bi.Freeze();
    var w=new System.Windows.Media.Imaging.FormatConvertedBitmap(App.UprightWpf(bi,p),System.Windows.Media.PixelFormats.Bgra32,null,0);
    Check(w.PixelWidth==g.Width&&w.PixelHeight==g.Height,"orientation "+o+": WPF "+w.PixelWidth+"x"+w.PixelHeight+", GDI+ "+g.Width+"x"+g.Height);
    var px=new byte[w.PixelWidth*w.PixelHeight*4];w.CopyPixels(px,w.PixelWidth*4,0);
    // the pattern's four quarter centres
    foreach(var f in new[]{new[]{.25,.25},new[]{.75,.25},new[]{.25,.75},new[]{.75,.75}}){
     int x=(int)(g.Width*f[0]),y=(int)(g.Height*f[1]);var c=g.GetPixel(x,y);int i=(y*w.PixelWidth+x)*4;
     Check(Math.Abs(c.R-px[i+2])<40&&Math.Abs(c.G-px[i+1])<40&&Math.Abs(c.B-px[i])<40,"orientation "+o+": WPF and GDI+ differ at "+x+","+y);
    }
    g.Dispose();
   }
  });

  // ⑧ a photo counts as made only once written
  Run("8 a started photo that fails is not counted",()=>{
   var g=Feed("- Processing 2 valid image files -","Predicting gaussians for C:\\a.jpg...","Failed: a.jpg: out of memory");
   Check(g.Done==0&&g.Failed==1,"done "+g.Done+", failed "+g.Failed+" (want 0, 1)");Check(g.LastFailure=="a.jpg: out of memory","reason: "+g.LastFailure);
   Check(g.Current==2,"current "+g.Current+" (want 2 of 2)");
  });
  Run("8 Matte's generator: saved and failed",()=>{
   var g=Feed("- Processing 3 valid image files -","Predicting gaussians for a...","Saved: a.jpg","Predicting gaussians for b...","Failed: b.jpg: bad file","Predicting gaussians for c...","Saved: c.jpg");g.Exit(1);
   Check(g.Done==2&&g.Failed==1&&g.Total==3,"done "+g.Done+", failed "+g.Failed+", total "+g.Total+" (want 2, 1, 3)");
  });
  Run("8 a generator of one's own without Saved lines",()=>{
   var ok=Feed("- Processing 2 valid image files -","Predicting gaussians for a...","Predicting gaussians for b...");ok.Exit(0);
   Check(ok.Done==2&&ok.Failed==0,"clean exit: done "+ok.Done+", failed "+ok.Failed);
   var bad=Feed("- Processing 2 valid image files -","Predicting gaussians for a...","Predicting gaussians for b...");bad.Exit(1);
   Check(bad.Done==1&&bad.Failed==1,"error exit: done "+bad.Done+", failed "+bad.Failed+" (want 1, 1)");
  });
  Run("8 the generator says when a photo is saved",()=>{
   string py=File.ReadAllText(Path.Combine(Here,@"..\..\spatial\matte_sharp.py"));
   Check(py.Contains("print(f'Saved: {photo.name}'"),"spatial\\matte_sharp.py no longer prints Saved: <name>, which GenProgress counts");
  });

  // ⑨ settings written safely and read back safely
  Run("9 save keeps the version before",()=>{
   string d=Fresh("store"),p=Path.Combine(d,"s.json");
   SettingsStore.Save(p,"{\"Interval\":60000}");SettingsStore.Save(p,"{\"Interval\":120000}");
   Check(File.ReadAllText(p).Contains("120000")&&File.ReadAllText(p+".bak").Contains("60000"),"file / .bak not as written");
   Check(!File.Exists(p+".tmp"),"temporary file left behind");
  });
  Run("9 a damaged file falls back to the copy before it",()=>{
   string d=Fresh("damaged"),p=Path.Combine(d,"s.json");var log=new List<string>();var js=new JavaScriptSerializer();
   SettingsStore.Save(p,js.Serialize(new MatteSettings{Interval=60000}));SettingsStore.Save(p,js.Serialize(new MatteSettings{Interval=120000}));
   File.WriteAllText(p,"{\"Interval\":12");   // cut short
   var s=SettingsStore.Load(p,t=>js.Deserialize<MatteSettings>(t),log.Add);
   Check(s!=null&&s.Interval==60000,"read "+(s==null?"nothing":s.Interval.ToString())+" (want the .bak's 60000)");
   Check(File.Exists(p+".bad"),"the damaged file was not kept aside");
   Check(File.ReadAllText(p).Contains("60000"),"the good copy was not put back as the file");
   File.WriteAllText(p,"{");File.WriteAllText(p+".bak","");Check(SettingsStore.Load(p,t=>js.Deserialize<MatteSettings>(t),log.Add)==null,"neither reads: should be null");
  });
  Run("9 Matte starts with a damaged settings file",()=>{
   ClearSettings();File.WriteAllText(SettingsFile,"{\"Playlist\":[\"C:\\");
   var app=new App(new TestWall());app.TestInit();
   Check(app.TestSettings!=null,"no settings");Check(File.Exists(SettingsFile+".bad"),"the damaged file was not kept aside");
   Check(new JavaScriptSerializer().Deserialize<MatteSettings>(File.ReadAllText(SettingsFile))!=null,"settings.json not rewritten");
  });
  Run("9 values Matte does not offer go back to the defaults",()=>{
   var s=new MatteSettings{Interval=5,Position=4,Background="bogus",PanelStyle="x",Order="y",Motion="z"};s.Normalize();
   Check(s.Interval==1800000&&s.Position==3&&s.Background=="color"&&s.PanelStyle=="ambient"&&s.Order=="shuffle"&&s.Motion=="follow","not normalized");
  });

  // Codex's review of 489cc9e
  // R1: colours worked out before photos were read upright are not reused
  Run("R1 an old colour cache is not reused",()=>{
   string d=Fresh("cache"),p=Path.Combine(d,"photo.jpg");Solid(p,Color.FromArgb(200,40,40),80,60,1);
   string v2=Path.Combine(Here,"palette-v2.tsv"),v3=Path.Combine(Here,"palette-v3.tsv");
   try{
    File.WriteAllText(v2,App.TestKey(p)+"\t"+0x010203u+Environment.NewLine);if(File.Exists(v3))File.Delete(v3);
    var app=new App(new TestWall());app.TestLoadColourCache();uint c=app.TestColorFor(p);
    Check(c!=0x010203u,"the colour from palette-v2.tsv was reused");
    File.WriteAllText(v3,App.TestKey(p)+"\t"+0x0A0B0Cu+Environment.NewLine);
    var again=new App(new TestWall());again.TestLoadColourCache();Check(again.TestColorFor(p)==0x0A0B0Cu,"palette-v3.tsv is not read");
   }finally{if(File.Exists(v2))File.Delete(v2);if(File.Exists(v3))File.Delete(v3);}
  });
  // R2: a recovery keeps a good backup for the next one
  Run("R2 recovered twice from the same backup",()=>{
   string d=Fresh("recover"),p=Path.Combine(d,"s.json");var js=new JavaScriptSerializer();var log=new List<string>();
   Func<MatteSettings> load=()=>SettingsStore.Load(p,t=>js.Deserialize<MatteSettings>(t),log.Add);
   SettingsStore.Save(p,js.Serialize(new MatteSettings{Interval=60000}));SettingsStore.Save(p,js.Serialize(new MatteSettings{Interval=120000}));
   File.WriteAllText(p,"{\"Interval\":");var first=load();Check(first!=null&&first.Interval==60000,"first recovery: "+(first==null?"nothing":first.Interval.ToString()));
   SettingsStore.Save(p,js.Serialize(first));   // what startup does after a recovery
   File.WriteAllText(p,"{\"Interval\":");var second=load();Check(second!=null&&second.Interval==60000,"second recovery: "+(second==null?"nothing":second.Interval.ToString())+" (want 60000)");
  });
  // R3: stopping does not erase a real failure
  Run("R3 stopped between photos keeps the failure",()=>{
   var g=Feed("Predicting gaussians for a...","Failed: a.jpg: bad file");g.Cancel();
   Check(g.Done==0&&g.Failed==1,"done "+g.Done+", failed "+g.Failed+" (want 0, 1)");
  });
  Run("R3 stopped during a photo counts it neither way",()=>{
   var g=Feed("Predicting gaussians for a...","Saved: a.jpg","Predicting gaussians for b...");g.Cancel();
   Check(g.Done==1&&g.Failed==0,"done "+g.Done+", failed "+g.Failed+" (want 1, 0)");
  });

  // round 2 of Codex's review
  Run("R2 a recovery whose copy-back fails still keeps the good backup",()=>{
   string d=Fresh("recover-locked"),p=Path.Combine(d,"s.json");var js=new JavaScriptSerializer();var log=new List<string>();
   Func<MatteSettings> load=()=>SettingsStore.Load(p,t=>js.Deserialize<MatteSettings>(t),log.Add);
   SettingsStore.Save(p,js.Serialize(new MatteSettings{Interval=60000}));SettingsStore.Save(p,js.Serialize(new MatteSettings{Interval=120000}));
   File.WriteAllText(p,"{\"Interval\":");
   MatteSettings first;
   using(new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.Read))first=load();   // the damaged file held open: the copy back fails
   Check(first!=null&&first.Interval==60000,"first recovery: "+(first==null?"nothing":first.Interval.ToString()));
   Check(log.Any(l=>l.Contains("restoring the copy")),"the copy back did not fail (the test did not exercise it)");
   SettingsStore.Save(p,js.Serialize(first));   // what startup does after a recovery
   File.WriteAllText(p,"{\"Interval\":");var second=load();
   Check(second!=null&&second.Interval==60000,"second recovery: "+(second==null?"nothing":second.Interval.ToString())+" (want 60000)");
  });

  // round 3 of Codex's review
  Run("R2 a save that fails after a recovery is retried without losing the backup",()=>{
   string d=Fresh("recover-retry"),p=Path.Combine(d,"s.json");var js=new JavaScriptSerializer();var log=new List<string>();
   Func<MatteSettings> load=()=>SettingsStore.Load(p,t=>js.Deserialize<MatteSettings>(t),log.Add);
   SettingsStore.Save(p,js.Serialize(new MatteSettings{Interval=60000}));SettingsStore.Save(p,js.Serialize(new MatteSettings{Interval=120000}));
   File.WriteAllText(p,"{\"Interval\":");
   MatteSettings first;bool saveFailed=false;
   using(new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.Read)){
    first=load();Check(first!=null&&first.Interval==60000,"first recovery");
    try{SettingsStore.Save(p,js.Serialize(first));}catch(IOException){saveFailed=true;}   // still held: the save fails
   }
   Check(saveFailed,"the save while the file was held did not fail (the test did not exercise it)");
   SettingsStore.Save(p,js.Serialize(first));   // the retry
   File.WriteAllText(p,"{\"Interval\":");var second=load();
   Check(second!=null&&second.Interval==60000,"recovery after the retry: "+(second==null?"nothing":second.Interval.ToString())+" (want 60000)");
  });


  // ---- a generation run (GenProcess: created suspended, put in its job, then run; its output read here)
  Run("R4 the end comes after the last line",()=>{
   for(int round=0;round<5;round++){
    var w=Watch(Script("echo Predicting gaussians for a...","echo Saved: a.jpg","echo Predicting gaussians for b...","echo Failed: b.jpg: boom"),true);
    Check(w.Ended,"the end never came");Check(w.AtEnd==4,"the end came after "+w.AtEnd+" of 4 lines");
    var g=Feed(w.Lines.ToArray());g.Exit(0);Check(g.Done==1&&g.Failed==1,"done "+g.Done+", failed "+g.Failed+" (want 1, 1)");
   }
  });
  Run("R4 lines after the generator exited still come before the end",()=>{
   // the generator is a PowerShell script running Python: its last lines can come after the process it started exited.
   // A child started with start /b inherits the output pipe; it writes the last line a second after the parent exits.
   var w=Watch(Script("echo Predicting gaussians for a...","start /b cmd /d /c \"ping -n 2 127.0.0.1 >nul & echo Saved: a.jpg\""),true);
   Check(w.Ended,"the end never came");Check(w.AtEnd==2,"the end came after "+w.AtEnd+" of 2 lines");
  });
  Run("R4 an earlier run's output is dropped",()=>{
   var w=Watch(Script("echo Predicting gaussians for old...","echo Saved: old.jpg"),false,1500);
   Check(w.Lines.Count==0&&!w.Ended,"an earlier run's "+w.Lines.Count+" lines / end got through");
  });
  Run("R4 busy until the run's end is handled, not only while its process runs",()=>{
   var app=GenApp("busy");
   string why=app.TestRunGenerator(Script("echo - Processing 1 valid image files -","echo Predicting gaussians for a...","start /b cmd /d /c \"ping -n 2 127.0.0.1 >nul & echo Saved: a.jpg\""));
   Check(why==null,"did not start: "+why);
   for(int i=0;i<400&&app.TestGenerating;i++)Pump(25);
   Check(!app.TestGenerating,"still busy after 10 s");
   Check(app.TestGenDone==1,"done "+app.TestGenDone+" when no longer busy (want 1: the last line came after the generator exited)");
  });
  Run("R3 GenFinished keeps a failure when stopped between photos",()=>{
   var app=GenApp("finish");
   app.TestFinish(true,"- Processing 2 valid image files -","Predicting gaussians for a...","Failed: a.jpg: bad file");
   Check(app.TestGenDone==0&&app.TestGenFailed==1,"done "+app.TestGenDone+", failed "+app.TestGenFailed+" (want 0, 1)");
   var app2=GenApp("finish2");
   app2.TestFinish(true,"- Processing 2 valid image files -","Predicting gaussians for a...","Saved: a.jpg","Predicting gaussians for b...");
   Check(app2.TestGenDone==1&&app2.TestGenFailed==0,"stopped during b: done "+app2.TestGenDone+", failed "+app2.TestGenFailed+" (want 1, 0)");
  });
  Run("R4 a failed wait: no success inferred, the run closed, late lines not counted",()=>{
   var app=GenApp("drain");var wait=App.WaitForEnd;
   try{
    App.WaitForEnd=proc=>{System.Threading.Thread.Sleep(400);throw new InvalidOperationException("test: the wait fails");};
    string why=app.TestRunGenerator(Script("echo - Processing 2 valid image files -","echo Predicting gaussians for a...","start /b cmd /d /c \"ping -n 2 127.0.0.1 >nul & echo Predicting gaussians for b... & echo Saved: b.jpg\""));
    Check(why==null,"did not start: "+why);
    for(int i=0;i<200&&app.TestGenerating;i++)Pump(25);
    Check(!app.TestGenerating,"still busy after the failed wait");
    Check(app.TestGenDone==0,"done "+app.TestGenDone+" right after the failed wait (want 0: the open photo is not taken as made)");
    Pump(2500);
    Check(app.TestGenDone==0,"done "+app.TestGenDone+" after the late lines (want 0: the run is closed)");
   }finally{App.WaitForEnd=wait;}
  });
  Run("R4 a child left behind is ended before the run lets go",()=>{
   // A child started without the generator's handles (Start-Process: the wait returns when the generator exits, as for a
   // child that closed its output) is still in the run's job. It says it started and would write to the store 5 s later.
   var c=LeftBehind(0);
   try{
    var app=GenApp("left");string why=app.TestRunGenerator(c.Script);Check(why==null,"did not start: "+why);
    for(int i=0;i<400&&app.TestGenerating;i++)Pump(25);
    Check(!app.TestGenerating,"still busy after 10 s");Check(app.TestGenDone==1,"done "+app.TestGenDone);
    Check(File.Exists(c.Started),"the child never started (the test did not exercise the job)");
    Check(app.TestActiveAtRelease==0,app.TestActiveAtRelease+" process(es) still in the job as the run let go (want 0)");
    Pump(6500);
    Check(!File.Exists(c.Marker),"the child kept running after the run let go: it wrote to the store");
   }finally{c.Clean();}
  });
  Run("R4 the generator is in its job before it runs",()=>{
   // the gap between creating the process and putting it in its job, made 4 s long: the generator, created suspended,
   // runs nothing in it, so the child it starts first thing is in the job and is ended with the run
   var c=LeftBehind(0);var hook=GenProcess.BeforeAssign;
   try{
    GenProcess.BeforeAssign=()=>System.Threading.Thread.Sleep(4000);
    var app=GenApp("suspended");string why=app.TestRunGenerator(c.Script);Check(why==null,"did not start: "+why);
    for(int i=0;i<600&&app.TestGenerating;i++)Pump(25);
    Check(!app.TestGenerating,"still busy after 15 s");
    Check(File.Exists(c.Started),"the child never started");
    Pump(6500);
    Check(!File.Exists(c.Marker),"the child got out of the job: it ran while the generator was not yet in it, and wrote to the store");
   }finally{GenProcess.BeforeAssign=hook;c.Clean();}
  });
  Run("R4 no job, no run: nothing of the generator runs",()=>{
   foreach(var which in new[]{"job","assign"}){
    string d=Plain("nojob"),marker=Path.Combine(d,"ran.txt");
    try{
     if(which=="job")GenProcess.FailJob=true;else GenProcess.FailAssign=true;
     var app=GenApp("nojob-"+which);string why=app.TestRunGenerator(Script("echo x> \""+marker+"\"","echo Predicting gaussians for a..."));
     Check(why!=null,which+": the run started without its job");Check(!app.TestGenerating,which+": busy after a refused start");
     Pump(1500);Check(!File.Exists(marker),which+": the generator ran (it wrote "+marker+")");
    }finally{GenProcess.FailJob=false;GenProcess.FailAssign=false;try{Directory.Delete(d,true);}catch{}}
   }
  });
  Run("R4 an end that cannot be confirmed keeps Matte busy and says so",()=>{
   var ensure=App.EnsureEnded;var app=GenApp("unconfirmed");
   try{
    App.EnsureEnded=proc=>false;
    string why=app.TestRunGenerator(Script("echo - Processing 1 valid image files -","echo Predicting gaussians for a...","echo Saved: a.jpg"));Check(why==null,"did not start: "+why);
    Pump(3000);
    Check(app.TestGenerating,"Matte let go of the store although the run's end could not be confirmed");
    Check(app.TestStuck&&app.TestPhase.Contains("重新启动"),"no word of it: "+app.TestPhase);
    Check(!app.TestPhase.Contains("完成"),"announced as done: "+app.TestPhase);
    string shown=app.TestPanelGenText();
    Check(shown.Contains("重新启动")&&!shown.Contains("正在生成"),"the panel shows: "+shown);
   }finally{App.EnsureEnded=ensure;}
   app.TestStop();   // 再试一次结束: the job is ended and empty now, so Matte lets go
   Check(!app.TestGenerating&&!app.TestStuck,"再试一次结束 did not let go");
  });

  // round 6 of Codex's review: a failed start leaves nothing behind; a failed read is a failed drain, not a crash
  Run("R7 a start that fails part-way leaves nothing running and no handles behind",()=>{
   var faults=new Dictionary<string,Action<bool>>{
    {"resume",on=>GenProcess.FailResume=on},
    {"assign",on=>GenProcess.FailAssign=on},{"assign, first terminate refused",on=>{GenProcess.FailAssign=on;GenProcess.FailTerminate=on;}}};
   foreach(var f in faults){
    string d=Plain("rollback"),marker=Path.Combine(d,"ran.txt"),script=Script("echo x> \""+marker+"\"","echo Predicting gaussians for a...");
    try{
     f.Value(true);
     // warm up once (first-use allocations), then count handles across repeated failed starts
     string why;var g=GenProcess.Start("cmd.exe","/d /c \""+script+"\"",l=>{},out why);Check(g==null,f.Key+": started");
     GC.Collect();GC.WaitForPendingFinalizers();Pump(300);int before=System.Diagnostics.Process.GetCurrentProcess().HandleCount;
     for(int k=0;k<10;k++){g=GenProcess.Start("cmd.exe","/d /c \""+script+"\"",l=>{},out why);Check(g==null,f.Key+": started");Check(why!=null&&!why.Contains("没能确认"),f.Key+": "+why);}
     GC.Collect();GC.WaitForPendingFinalizers();Pump(500);int after=System.Diagnostics.Process.GetCurrentProcess().HandleCount;
     Check(after-before<=8,f.Key+": "+(after-before)+" handles more after 10 failed starts");
     Pump(800);Check(!File.Exists(marker),f.Key+": the generator ran");
    }finally{f.Value(false);try{Directory.Delete(d,true);}catch{}}
   }
  });
  foreach(int stage in new[]{1,2,3})Run("R7 reader handoff stage "+stage+" closes its handles without collection",()=>{
   string d=Plain("handoff"),marker=Path.Combine(d,"ran.txt"),script=Script("echo x> \""+marker+"\"");
   var hook=GenProcess.PipesCreated;
   try{
    GenProcess.FailReaderStage=stage;string why;
    for(int k=0;k<3;k++){
     IntPtr[] originals=null;var copies=new IntPtr[4];var names=new[]{"stdout read","stdout write","stderr read","stderr write"};
     try{
      GenProcess.PipesCreated=handles=>{
       originals=handles;
       for(int i=0;i<4;i++)Check(DuplicateHandle(new IntPtr(-1),handles[i],new IntPtr(-1),out copies[i],0,false,2),"duplicate "+names[i]+" failed");
      };
      var g=GenProcess.Start("cmd.exe","/d /c \""+script+"\"",l=>{},out why);
      Check(g==null,"started");Check(!string.IsNullOrEmpty(why)&&!why.Contains("没能确认"),"why: "+why);
      Check(originals!=null&&copies.All(h=>h!=IntPtr.Zero),"pipe hook did not duplicate all four ends: "+why);
      // Our write copies keep the first reader alive: check and close them before waiting for that reader's EOF.
      foreach(int i in new[]{1,3}){
       Check(!CompareObjectHandles(copies[i],originals[i]),names[i]+" still names the pipe after failed start "+(k+1));
       CloseHandle(copies[i]);copies[i]=IntPtr.Zero;
      }
      Check(!CompareObjectHandles(copies[2],originals[2]),names[2]+" still names the pipe after failed start "+(k+1));
      for(int n=0;n<100&&CompareObjectHandles(copies[0],originals[0]);n++)System.Threading.Thread.Sleep(10);
      Check(!CompareObjectHandles(copies[0],originals[0]),names[0]+" still names the pipe after failed start "+(k+1));
      Check(!File.Exists(marker),"the generator ran");
     }finally{
      GenProcess.PipesCreated=hook;
      foreach(var h in copies)if(h!=IntPtr.Zero)CloseHandle(h);
     }
    }
   }finally{GenProcess.PipesCreated=hook;GenProcess.FailReaderStage=0;try{Directory.Delete(d,true);}catch{}}
  });
  Run("R7 an end thread that cannot start ends the run as a failed drain",()=>{
   var app=GenApp("nowatch");
   try{
    App.FailWatcher=true;
    string why=app.TestRunGenerator(Script("echo - Processing 1 valid image files -","echo Predicting gaussians for a...","ping -n 4 127.0.0.1 >nul","echo Saved: a.jpg"));
    Check(why==null,"did not start: "+why);
    for(int i=0;i<300&&app.TestGenerating;i++)Pump(25);
    Check(!app.TestGenerating,"still busy");Check(app.TestGenDone==0,"done "+app.TestGenDone+" (want 0: not taken as made)");
    Check(app.TestActiveAtRelease==0,app.TestActiveAtRelease+" process(es) in the job at release");
   }finally{App.FailWatcher=false;}
  });
  Run("R8 a line handler that throws is a failed drain, not a crash",()=>{
   var disp=Dispatcher.CurrentDispatcher;string why;bool ended=false,drained=true;
   var g=GenProcess.Start("cmd.exe","/d /c \""+Script("echo one","echo two","echo three")+"\"",l=>{throw new InvalidOperationException("test: the handler throws");},out why);
   Check(g!=null,"did not start: "+why);
   try{
    App.Watch(g,a=>disp.BeginInvoke(a),()=>true,(d,e,left)=>{drained=d;ended=true;});
    for(int i=0;i<200&&!ended;i++)Pump(25);
    Check(ended,"the end never came");Check(!drained,"reported as drained although its handler failed");
   }finally{g.Close();}
  });
  Run("R8 after a line handler throws, the generator can still write its output",()=>{
   // the writer calls WriteFile itself: .NET console streams ignore a broken pipe, Python does not
   string d=Plain("drain"),ps=Path.Combine(d,"write.ps1"),why;int calls=0;
   File.WriteAllText(ps,@"$k=Add-Type -Name K -Namespace W -PassThru -MemberDefinition @'
[DllImport(""kernel32.dll"")] public static extern System.IntPtr GetStdHandle(int n);
[DllImport(""kernel32.dll"")] public static extern bool WriteFile(System.IntPtr h,byte[] b,int n,out int w,System.IntPtr o);
'@
$h=$k::GetStdHandle(-11);$w=0
$b=[Text.Encoding]::ASCII.GetBytes(""a`r`n"");if(-not $k::WriteFile($h,$b,$b.Length,[ref]$w,[IntPtr]::Zero)){exit 6}
Start-Sleep -Milliseconds 700
$b=[Text.Encoding]::ASCII.GetBytes(""b`r`n"");if(-not $k::WriteFile($h,$b,$b.Length,[ref]$w,[IntPtr]::Zero)){exit 7}
exit 0
");
   var g=GenProcess.Start("powershell.exe","-NoProfile -ExecutionPolicy Bypass -File \""+ps+"\"",l=>{calls++;throw new InvalidOperationException("test: the handler throws");},out why);
   Check(g!=null,"did not start: "+why);
   try{
    for(int i=0;i<600&&!g.HasExited;i++)Pump(25);
    Check(g.HasExited,"the generator never ended");
    Check(g.ExitCode==0,"the generator's later write failed (exit "+g.ExitCode+"): its output was no longer read");
    Check(calls==1,"handler called "+calls+" times (want 1: lines after a failure are dropped)");
   }finally{try{g.Wait();}catch{}g.Close();try{Directory.Delete(d,true);}catch{}}
  });
  Run("R8 a failed read is a failed drain, not a crash",()=>{
   var disp=Dispatcher.CurrentDispatcher;string why;bool ended=false,drained=true;var lines=new List<string>();
   try{
    GenProcess.FailRead=true;
    var g=GenProcess.Start("cmd.exe","/d /c \""+Script("echo one","echo two","echo three")+"\"",l=>disp.BeginInvoke(new Action(()=>lines.Add(l))),out why);
    Check(g!=null,"did not start: "+why);
    try{
     App.Watch(g,a=>disp.BeginInvoke(a),()=>true,(d,e,left)=>{drained=d;ended=true;});
     for(int i=0;i<200&&!ended;i++)Pump(25);
     Check(ended,"the end never came");Check(!drained,"reported as drained although a read failed");
    }finally{g.Close();}
   }finally{GenProcess.FailRead=false;}
  });
  Run("R10 the generator inherits only its input and output, nothing else Matte holds open",()=>{
   // an inheritable pipe of Matte's own: once Matte closes its copy of the writing end, it reads to its end at once
   // unless the generator took a copy along
   using(var other=new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In,HandleInheritability.Inheritable)){
    string why;var g=GenProcess.Start("cmd.exe","/d /c \""+Script("echo started","ping -n 4 127.0.0.1 >nul")+"\"",l=>{},out why);
    Check(g!=null,"did not start: "+why);
    try{
     other.DisposeLocalCopyOfClientHandle();
     var t=System.Threading.Tasks.Task.Run(()=>other.ReadByte());
     bool eof=t.Wait(1500);
     Check(!g.HasExited,"the generator ended too soon for the check to mean anything");
     Check(eof&&t.Result==-1,"the generator holds a handle it was never given");
    }finally{try{g.Wait();}catch{}g.Close();}
   }
  });
  Run("R10 another direct launch cannot inherit the generator's pipe ends",()=>{
   var hook=GenProcess.InWindow;System.Diagnostics.Process other=null;Exception startError=null;
   System.Threading.Thread launch=null;System.Threading.Tasks.Task wait=null;GenProcess g=null;
   bool blocked=false;string why;
   using(var attempted=new System.Threading.ManualResetEvent(false))using(var started=new System.Threading.ManualResetEvent(false)){
    try{
     GenProcess.InWindow=()=>{
      launch=new System.Threading.Thread(()=>{
       attempted.Set();
       try{other=App.StartDirect(new System.Diagnostics.ProcessStartInfo("ping.exe","-n 8 127.0.0.1"){UseShellExecute=false,CreateNoWindow=true});}
       catch(Exception e){startError=e;}finally{started.Set();}
      }){IsBackground=true};launch.Start();
      Check(attempted.WaitOne(1000),"the other launch never attempted to start");
      blocked=!started.WaitOne(300);
     };
     g=GenProcess.Start("cmd.exe","/d /c \""+Script("echo short run")+"\"",l=>{},out why);
     Check(g!=null,"did not start: "+why);Check(started.WaitOne(3000),"the other launch stayed blocked");
     Check(startError==null&&other!=null,"other launch: "+startError);
     wait=System.Threading.Tasks.Task.Run(()=>g.Wait());
     bool drained=wait.Wait(5000),alive=!other.HasExited;
     Check(drained&&alive,"drained within 5 s: "+drained+", unrelated child still alive: "+alive+", blocked in window: "+blocked);
     Check(blocked,"the other launch entered the inheritance window");
    }finally{
     GenProcess.InWindow=hook;
     if(launch!=null)launch.Join(5000);
     if(other!=null){try{if(!other.HasExited)other.Kill();other.WaitForExit(5000);}finally{other.Dispose();}}
     if(g!=null){GenJob.Terminate(g.Job);try{if(wait!=null)wait.Wait(5000);}finally{g.Close();}}
    }
   }
  });
  Run("R10 a handle list that cannot be built is a failed start",()=>{
   string why;
   try{GenProcess.FailHandleList=true;var g=GenProcess.Start("cmd.exe","/d /c echo x",l=>{},out why);Check(g==null,"started");Check(why!=null&&why.Contains("句柄"),"why: "+why);}
   finally{GenProcess.FailHandleList=false;}
  });
  Run("native structures have their x64 layouts",()=>{
   var a=GenProcess.Layout();var b=GenJob.Layout();
   Check(string.Join(",",a)=="24,104,24,112,80,88,96","SECURITY_ATTRIBUTES, STARTUPINFO, PROCESS_INFORMATION, STARTUPINFOEX, STARTUPINFO.hStd*: "+string.Join(",",a));
   Check(string.Join(",",b)=="64,48,144,48,112","JOBOBJECT limits, IO_COUNTERS, extended limits, accounting, ProcessMemoryLimit: "+string.Join(",",b));
  });

  ClearSettings();
  Console.WriteLine(failures==0?"all passed":failures+" failed");
  return failures;
 }

 // ---- pictures for the tests
 // four quarters of clearly different colours, so a turn or a mirror shows
 static Bitmap Pattern(int w,int h){var b=new Bitmap(w,h);using(var g=Graphics.FromImage(b)){
  g.FillRectangle(Brushes.Red,0,0,w/2,h/2);g.FillRectangle(Brushes.Lime,w/2,0,w-w/2,h/2);g.FillRectangle(Brushes.Blue,0,h/2,w/2,h-h/2);g.FillRectangle(Brushes.Yellow,w/2,h/2,w-w/2,h-h/2);}return b;}
 static void Solid(string path,Color c,int w,int h,int orientation){using(var b=new Bitmap(w,h)){using(var g=Graphics.FromImage(b))g.Clear(c);Save(b,path,orientation);}}
 // a JPEG with an EXIF orientation (1 = as stored)
 static void Save(Bitmap b,string path,int orientation){
  if(orientation!=1){var pi=(PropertyItem)FormatterServices.GetUninitializedObject(typeof(PropertyItem));pi.Id=0x0112;pi.Type=3;pi.Len=2;pi.Value=BitConverter.GetBytes((ushort)orientation);b.SetPropertyItem(pi);}
  var jpeg=ImageCodecInfo.GetImageEncoders().First(e=>e.FormatID==ImageFormat.Jpeg.Guid);var q=new EncoderParameters(1);q.Param[0]=new EncoderParameter(Encoder.Quality,95L);
  b.Save(path,jpeg,q);
 }
 static bool Near(uint a,uint b,int tol){for(int k=0;k<3;k++){int x=(int)((a>>(8*k))&255),y=(int)((b>>(8*k))&255);if(Math.Abs(x-y)>tol)return false;}return true;}
 static void StoreFile(string root,string relative,byte[] bytes){string path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllBytes(path,bytes);}
 static void LegacyIndex(string store,string stem,string path,string since){
  // Old-install fixture: includes the retired fingerprint/photo cache and an unknown field. SHA-1 sampled
  // fingerprint of little-endian Int64(10) + UTF-8("photo-data"), computed independently with Python hashlib.
  var record=new{path=path,fp="3659A5663A5A096918E8",missingSince=since,unknown="ignored"};
  var index=new{version=1,pictures=new Dictionary<string,object>{{stem,record}},photos=new Dictionary<string,object>{{path,new{size=10,ticks=0,fp=record.fp}}},unknown=42};
  File.WriteAllText(Path.Combine(store,"spatial-index.json"),new JavaScriptSerializer().Serialize(index));
 }
 static Dictionary<string,byte[]> MoveFixture(string store){
  var expected=new Dictionary<string,byte[]>{{"old.spatial",new byte[]{1,2,3}},{"spatial-index.json",new byte[]{4,5}},
   {@"v1\a\b.spatial",new byte[]{6,7,8,9}},{@"v1\a\b.spatial.json",new byte[]{10,11,12}},
   {@"v1\quarantine\x",new byte[]{13,14,15,16,17}},{@"work\nested\proxy.bin",new byte[]{18,19,20,21,22,23}}};
  foreach(var f in expected)StoreFile(store,f.Key,f.Value);
  StoreFile(store,"old.spatial.tmp-123",new byte[27]);StoreFile(store,@"v1\a\b.spatial.json.tmp-456",new byte[29]);StoreFile(store,@"v1\.lock",new byte[31]);
  return expected;
 }
 static void CheckMoved(string store,Dictionary<string,byte[]> expected){
  foreach(var f in expected){string path=Path.Combine(store,f.Key);Check(File.Exists(path),"missing relative path "+f.Key);Check(File.ReadAllBytes(path).SequenceEqual(f.Value),"changed bytes "+f.Key);}
  Check(Directory.GetFiles(store,"*",SearchOption.AllDirectories).Length==expected.Count,"temporary/lock file copied or extra file present");
 }
 // A failed mutant can leave the default path as a junction. Remove that link alone before Fresh can recurse.
 static void RemoveTestJunction(string path){if(SpatialStoreMove.IsJunction(path))Directory.Delete(path);}
 static void StoreTests(){
  Run("T2p sweep retains renamed legacy picture",()=>{
   string root=Fresh("p-rename"),store=Path.Combine(root,"store"),photos=Path.Combine(root,"photos");Directory.CreateDirectory(store);Directory.CreateDirectory(photos);
   string old=Path.Combine(photos,"old.jpg"),renamed=Path.Combine(photos,"new.jpg"),picture=Path.Combine(store,"old.spatial");
   File.WriteAllText(old,"photo-data");File.WriteAllBytes(picture,new byte[]{1,2,3});LegacyIndex(store,"old",old,null);File.Move(old,renamed);
   var r=SpatialIndex.Sweep(store,new[]{renamed},s=>{},new DateTime(2026,10,22));
   Check(File.Exists(picture)&&!File.Exists(Path.Combine(store,"new.spatial")),"legacy picture was renamed");
   Check(File.ReadAllBytes(picture).SequenceEqual(new byte[]{1,2,3}),"legacy bytes changed");Check(r.Missing==1&&r.MissingBytes==3&&r.InUse==0,"renamed-photo inventory");
  });
  Run("T2p sweep retains legacy after 30 days",()=>{
   string store=Fresh("p-missing"),picture=Path.Combine(store,"gone.spatial");File.WriteAllBytes(picture,new byte[]{1,2,3,4});
   var now=new DateTime(2026,10,22,0,0,0,DateTimeKind.Utc);LegacyIndex(store,"gone",Path.Combine(store,"gone.jpg"),now.AddDays(-31).ToString("o"));
   foreach(var when in new[]{now,now.AddYears(3)}){var r=SpatialIndex.Sweep(store,new string[0],s=>{},when);
    Check(File.Exists(picture),"legacy picture was recycled after 30 days");Check(r.Missing==1&&r.MissingBytes==4&&r.InUse==0&&r.Offline==0,"missing legacy inventory");}
  });
  Run("T2p sweep never counts or touches v1",()=>{
   string store=Fresh("p-v1");StoreFile(store,@"v1\a\b.spatial",new byte[]{1,2,3});StoreFile(store,@"v1\a\b.spatial.json",new byte[]{4,5});StoreFile(store,@"v1\retention.json",new byte[]{6});
   var before=Directory.GetFiles(Path.Combine(store,"v1"),"*",SearchOption.AllDirectories).ToDictionary(p=>p,p=>File.ReadAllBytes(p));
   var times=before.Keys.ToDictionary(p=>p,p=>File.GetLastWriteTimeUtc(p));
   var r=SpatialIndex.Sweep(store,new string[0],s=>{},new DateTime(2030,1,1));
   Check(r.InUse==0&&r.Missing==0&&r.Offline==0&&r.MissingBytes==0,"v1 counted by legacy sweep");
   Check(Directory.GetFiles(Path.Combine(store,"v1"),"*",SearchOption.AllDirectories).Length==before.Count,"v1 tree changed");
   foreach(var f in before)Check(File.Exists(f.Key)&&File.ReadAllBytes(f.Key).SequenceEqual(f.Value)&&File.GetLastWriteTimeUtc(f.Key)==times[f.Key],"v1 touched");
   var idx=new JavaScriptSerializer().Deserialize<SpatialIndex.Index>(File.ReadAllText(Path.Combine(store,"spatial-index.json")));Check(idx.version==1&&idx.pictures.Count==0,"v1 entered legacy index");
  });
  Run("T2p sweep inventories last path and offline legacy",()=>{
   string store=Fresh("p-status"),photo=Path.Combine(store,"renamed.jpg");File.WriteAllText(photo,"photo-data");File.WriteAllBytes(Path.Combine(store,"old.spatial"),new byte[]{1});
   LegacyIndex(store,"old",photo,"2020-01-01T00:00:00Z");
   var r=SpatialIndex.Sweep(store,new string[0],s=>{},DateTime.UtcNow);Check(r.InUse==1&&r.Missing==0,"existing last path not counted");
   string offline=Enumerable.Range('A',26).Select(c=>((char)c)+@":\").First(p=>!Directory.Exists(p));
   File.SetAttributes(Path.Combine(store,"spatial-index.json"),FileAttributes.Normal);LegacyIndex(store,"old",offline+@"photos\old.jpg","2020-01-01T00:00:00Z");
   r=SpatialIndex.Sweep(store,new string[0],s=>{},DateTime.UtcNow);Check(r.Offline==1&&r.Missing==0&&File.Exists(Path.Combine(store,"old.spatial")),"offline picture not retained");
  });
  Run("T2p move carries tree and excludes publication files",()=>{
   string root=Fresh("p-move"),original=Path.Combine(root,"old"),dest=Path.Combine(root,"new"),third=Path.Combine(root,"third");var expected=MoveFixture(original);var progress=new List<double>();
   try{
    var result=SpatialStoreMove.Move(original,null,dest,p=>progress.Add(p),s=>{});
    CheckMoved(dest,expected);CheckMoved(original,expected);Check(SpatialStoreMove.IsJunction(original),"default path not switched");
    Check(result.Target==dest&&Directory.Exists(result.Backup),"move result or backup missing");
    Check(File.Exists(Path.Combine(result.Backup,@"v1\a\b.spatial.json.tmp-456")),"old temporary removed");
    long total=expected.Values.Sum(b=>(long)b.Length);long[] sizes=expected.Values.Select(b=>(long)b.Length).OrderBy(n=>n).ToArray();
    double previous=0;var reported=new List<long>();foreach(double p in progress){Check(p>=previous&&p<=1,"non-monotonic progress");reported.Add((long)Math.Round((p-previous)*total));previous=p;}
    Check(progress.Count==expected.Count&&previous==1&&reported.OrderBy(n=>n).SequenceEqual(sizes),"progress did not count bytes across the whole tree");
    var second=SpatialStoreMove.Move(original,dest,third,null,s=>{});Check(second.Target==third&&second.Backup==dest,"junction-to-junction move result");CheckMoved(original,expected);
    var back=SpatialStoreMove.Move(original,third,original,null,s=>{});Check(back.Target==null&&back.Backup==third&&!SpatialStoreMove.IsJunction(original),"move back result");CheckMoved(original,expected);
   }finally{RemoveTestJunction(original);}
  });
  foreach(string ext in new[]{".spatial",".spatial.json"}){string suffix=ext;Run("T2p move rejects same-size corruption "+suffix,()=>{
   string root=Fresh(suffix==".spatial"?"p-hash":"p-json"),original=Path.Combine(root,"old"),dest=Path.Combine(root,"new");var expected=MoveFixture(original);bool failed=false,hook=false;
   try{
    SpatialStoreMove.BeforeVerify=stage=>{hook=true;string p=Path.Combine(stage,@"v1\a\b"+suffix);byte[] b=File.ReadAllBytes(p);b[0]^=1;File.WriteAllBytes(p,b);};
    try{SpatialStoreMove.Move(original,null,dest,null,s=>{});}catch(IOException){failed=true;}
    Check(hook,"corruption hook not reached");Check(failed,"same-size v1 corruption accepted");
    Check(!SpatialStoreMove.IsJunction(original)&&!Directory.Exists(dest),"failed move switched location or left staged files");
    Check(File.ReadAllBytes(Path.Combine(original,@"v1\a\b"+suffix)).SequenceEqual(expected[@"v1\a\b"+suffix]),"source changed");
    Check(Directory.GetDirectories(root,"old.old-*").Length==0,"old location renamed before verification");
   }finally{SpatialStoreMove.BeforeVerify=null;RemoveTestJunction(original);}
  });}
  Run("T2p copy failure removes stage and preserves current junction",()=>{
   string root=Fresh("p-copy"),original=Path.Combine(root,"old"),current=Path.Combine(root,"current"),dest=Path.Combine(root,"new");var expected=MoveFixture(original);bool failed=false;
   try{
    SpatialStoreMove.Move(original,null,current,null,s=>{});
    using(var held=new FileStream(Path.Combine(current,@"v1\a\b.spatial"),FileMode.Open,FileAccess.Read,FileShare.None))
     try{SpatialStoreMove.Move(original,current,dest,null,s=>{});}catch(IOException){failed=true;}
    Check(failed&&!Directory.Exists(dest),"copy failure left staging or succeeded");Check(SpatialStoreMove.IsJunction(original),"old junction removed on copy failure");CheckMoved(original,expected);CheckMoved(current,expected);
   }finally{RemoveTestJunction(original);}
  });
 }
 static Dictionary<string,object> IdObj(object value){return (Dictionary<string,object>)value;}
 static IEnumerable<Dictionary<string,object>> IdCases(Dictionary<string,object> vectors,string key){return ((List<object>)vectors[key]).Select(IdObj);}
 static void IdReject(Action action,string message){try{action();}catch(FormatException){return;}throw new Exception(message);}
 static void IdentityTests(){
  var v=IdObj(SpatialIdentity.ParseStrict(File.ReadAllText(Path.Combine(Here,"..","identity-vectors.json"),System.Text.Encoding.UTF8)));
  foreach(var item in IdCases(v,"canonical")){var c=item;Run("T2a canonical "+c["name"],()=>{
   string actual=SpatialIdentity.Canonical(SpatialIdentity.ParseStrict((string)c["json"]));
   Check(actual==(string)c["expected"],"canonical differs: "+actual);
   Check(SpatialIdentity.Sha256(System.Text.Encoding.UTF8.GetBytes(actual))==(string)c["sha256"],"canonical SHA-256 differs");
  });}
  foreach(var item in IdCases(v,"parseRejects")){var c=item;Run("T2a parser "+c["name"],()=>IdReject(()=>SpatialIdentity.ParseStrict((string)c["json"]),"parser accepted "+c["name"]));}
  foreach(var item in IdCases(v,"canonicalRejects")){var c=item;Run("T2a canonical rejects "+c["name"],()=>IdReject(()=>SpatialIdentity.Canonical(SpatialIdentity.ParseStrict((string)c["json"])),"canonical accepted "+c["json"]));}
  Run("T2a native value types and raw surrogates",()=>{
   foreach(object bad in new object[]{1.5,1.0m,true,null,"\ud800","\udc00",new Dictionary<string,object>{{"a",1.0}}})IdReject(()=>SpatialIdentity.Canonical(bad),"accepted unsupported native value");
   IdReject(()=>SpatialIdentity.ParseStrict("\"\ud800\""),"accepted raw surrogate");
   Check(SpatialIdentity.Canonical(new object[]{long.MinValue,ulong.MaxValue})=="[-9223372036854775808,18446744073709551615]","native integer bounds");
  });
  foreach(var item in IdCases(v,"recipes")){var c=item;Run("T2a recipe "+c["name"],()=>{
   if((string)c["outcome"]=="Invalid")IdReject(()=>SpatialIdentity.ValidateRecipe(SpatialIdentity.ParseStrict((string)c["json"])),"accepted invalid recipe "+c["name"]);
   else{object fields=SpatialIdentity.ParseStrict((string)c["json"]);
    Check(SpatialIdentity.Canonical(fields)==(string)c["expected"],"recipe canonical differs");
    Check(SpatialIdentity.RecipeSha256(fields)==(string)c["sha256"],"recipe SHA-256 differs");
    Check(SpatialIdentity.Supported(fields)==(c["supported"].ToString()=="1"),"supported recipe differs");}
  });}
  var valid=new List<SpatialIdentity.Pair>();
  foreach(var item in IdCases(v,"sidecars")){var c=item;Run("T2a sidecar "+c["name"],()=>{
   string store=Fresh("id"),source=(string)v["sourceSha"],recipe=(string)v["recipeSha"],location=(string)c["location"],mode=(string)c["mode"];
   if(location=="wrong-source")source=new string('0',64);if(location=="wrong-recipe")recipe=new string('0',64);
   string folder=Path.Combine(store,location=="wrong-version-dir"?"v2":"v1",location=="uppercase-source"?source.ToUpperInvariant():source);Directory.CreateDirectory(folder);
   string artifact=Path.Combine(folder,(location=="uppercase-recipe"?recipe.ToUpperInvariant():recipe)+".spatial"),sidecar=artifact+".json";
   if(mode!="missing-artifact")File.WriteAllBytes(artifact,System.Text.Encoding.UTF8.GetBytes((string)v["artifactText"]));
   if(mode!="missing-sidecar")File.WriteAllBytes(sidecar,mode=="bad-utf8"?new byte[]{255}:System.Text.Encoding.UTF8.GetBytes((string)c["json"]));
   FileStream held=null;
   try{
    if(mode=="locked-sidecar"||mode=="locked-artifact")held=new FileStream(mode=="locked-sidecar"?sidecar:artifact,FileMode.Open,FileAccess.Read,FileShare.None);
    SpatialIdentity.Pair pair;var result=SpatialIdentity.Classify(sidecar,c["verify"].ToString()=="1",out pair);
    Check(result.ToString()==(string)c["expected"],"expected "+c["expected"]+", got "+result);
    if(result==SpatialIdentity.Classification.Valid){Check(pair!=null,"missing pair");valid.Add(pair);object data=SpatialIdentity.ParseStrict((string)c["json"]);Check(SpatialIdentity.SidecarJson(data)==SpatialIdentity.Canonical(data),"sidecar write not canonical");}
    else Check(pair==null,"invalid classification returned a pair");
   }finally{if(held!=null)held.Dispose();}
  });}
  Run("T2a naming and length boundaries",()=>{
   string store=Path.Combine(Here,"store"),source=(string)v["sourceSha"],recipe=(string)v["recipeSha"],artifact=SpatialIdentity.ArtifactPath(store,source,recipe);
   Check(artifact==Path.Combine(store,"v1",source,recipe+".spatial"),"artifact path");Check(SpatialIdentity.SidecarPath(store,source,recipe)==artifact+".json","sidecar path");
   var names=new HashSet<string>();for(int i=0;i<32;i++){string tmp=SpatialIdentity.TempName(artifact);Check(System.Text.RegularExpressions.Regex.IsMatch(tmp,"\\A"+System.Text.RegularExpressions.Regex.Escape(artifact)+@"\.tmp-[0-9a-f]{16}\z"),"temporary name");Check(names.Add(tmp),"repeated random temporary name");}
   var now=new DateTime(2026,10,22,1,2,3,DateTimeKind.Utc);
   Check(SpatialIdentity.QuarantinePath(store,source,recipe,now)==Path.Combine(store,"v1","quarantine","20261022T010203Z-"+source+"-"+recipe+".spatial"),"quarantine path");
   string root=Path.GetPathRoot(Here);
   foreach(int length in new[]{258,259,260})foreach(string prefix in new[]{"","\ud83d\ude00"}){
    string path=root+prefix+new string('s',length-169-root.Length-prefix.Length);
    Check(SpatialIdentity.StoreTooLong(path)==(length>259),"length boundary "+length+" prefix "+prefix.Length);}
   Check(SpatialIdentity.StoreTooLong(root+new string('s',400)),"overlong store path threw or passed");
   IdReject(()=>SpatialIdentity.ArtifactPath(store,source.ToUpperInvariant(),recipe),"accepted uppercase digest");
   IdReject(()=>SpatialIdentity.QuarantinePath(store,source,recipe,DateTime.Now),"accepted non-UTC date");
  });
  Run("T2a selection desired newest tie and source",()=>{
   var original=valid.First();var fields=original.Fields;string source=original.SourceSha;DateTime now=original.Created;
   var older=new SpatialIdentity.Pair(source,new string('a',64),fields,now.AddDays(-1),"older");
   var newer=new SpatialIdentity.Pair(source,new string('b',64),fields,now,"newer");
   var tie=new SpatialIdentity.Pair(source,new string('c',64),fields,now,"tie");
   var other=new SpatialIdentity.Pair(new string('0',64),new string('d',64),fields,now.AddDays(1),"other");
   var choices=new[]{tie,other,newer,older};
   Check(SpatialIdentity.Select(source,older.RecipeSha,choices)==older,"desired recipe did not win");
   Check(SpatialIdentity.Select(source,new string('f',64),choices)==newer,"newest/tie selection failed");
   Check(SpatialIdentity.Select(source,null,choices.Reverse())==newer,"selection depends on enumeration order");
   Check(SpatialIdentity.Select(source,null,new[]{other})==null,"different source selected");
   Check(SpatialIdentity.Select(source,null,new SpatialIdentity.Pair[0])==null,"empty candidates selected");
  });
  Run("T2a selection excludes unsupported",()=>{
   var original=valid.First();var fields=new Dictionary<string,object>(original.Fields);fields["generator"]="matte-sharp/2";
   var future=new SpatialIdentity.Pair(original.SourceSha,SpatialIdentity.RecipeSha256(fields),fields,original.Created.AddDays(1),"future");
   Check(SpatialIdentity.Select(original.SourceSha,future.RecipeSha,new[]{future,original})==original,"unsupported desired recipe selected");
   Check(SpatialIdentity.Select(original.SourceSha,null,new[]{original,future})==original,"unsupported newest recipe selected");
   Check(SpatialIdentity.Select(original.SourceSha,null,new[]{future})==null,"unsupported-only candidates selected");
  });
 }
 static GenProgress Feed(params string[] lines){var g=new GenProgress();foreach(var l in lines)g.Line(l);return g;}
 // the generation tests' files go in the temp folder: cmd reads batch files in the console code page, so paths stay plain
 static string Plain(string name){string d=Path.Combine(Path.GetTempPath(),"matte-"+name+"-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(d);return d;}
 static string Script(params string[] lines){string f=Path.Combine(Plain("gen"),"gen.cmd");File.WriteAllLines(f,new[]{"@echo off"}.Concat(lines).ToArray());return f;}
 static App GenApp(string name){var app=new App(new TestWall());var st=new MatteSettings();st.SpatialFolder=Fresh(name+"-cache");app.TestSettings=st;return app;}
 class Watched{public List<string> Lines=new List<string>();public int AtEnd=-1;public bool Ended;}
 // a script run as a generator, its lines and end watched the way RunGenerator does (current: whether the run is current)
 static Watched Watch(string script,bool current,int extraMs=0){
  var w=new Watched();var disp=Dispatcher.CurrentDispatcher;string why;
  var p=GenProcess.Start("cmd.exe","/d /c \""+script+"\"",l=>disp.BeginInvoke(new Action(()=>{if(current)w.Lines.Add(l);})),out why);
  Check(p!=null,"did not start: "+why);
  App.Watch(p,a=>disp.BeginInvoke(a),()=>current,(drained,ended,left)=>{w.AtEnd=w.Lines.Count;w.Ended=true;});
  try{for(int i=0;i<300&&!w.Ended&&(current||i<extraMs/25);i++)Pump(25);}
  finally{try{p.Wait();}catch{}p.Close();}   // GenProcess has no finalizer: what a test starts, it closes
  return w;
 }
 class Child{public string Script,Started,Marker,Dir;public void Clean(){try{Directory.Delete(Dir,true);}catch{}}}
 // a generator whose first act starts a child outside its handles (Start-Process): the child says it started, and writes
 // to the store five seconds later unless it is ended
 static Child LeftBehind(int unused){
  var c=new Child{Dir=Plain("left")};c.Started=Path.Combine(c.Dir,"started.txt");c.Marker=Path.Combine(c.Dir,"written.txt");
  string child=Path.Combine(c.Dir,"child.cmd"),launcher=Path.Combine(c.Dir,"launch.ps1");c.Script=Path.Combine(c.Dir,"gen.cmd");
  File.WriteAllLines(child,new[]{"@echo off","echo x> \""+c.Started+"\"","ping -n 6 127.0.0.1 >nul","echo x> \""+c.Marker+"\""});
  File.WriteAllText(launcher,"Start-Process -WindowStyle Hidden -FilePath cmd.exe -ArgumentList ('/d /c \"'+'"+child+"'+'\"')"+Environment.NewLine);
  File.WriteAllLines(c.Script,new[]{"@echo off","echo - Processing 1 valid image files -","powershell -NoProfile -ExecutionPolicy Bypass -File \""+launcher+"\"","echo Predicting gaussians for a...","ping -n 2 127.0.0.1 >nul","echo Saved: a.jpg"});
  return c;
 }
 // a process that prints these lines and exits at once (the generator's output, without the generator)
 static System.Diagnostics.Process Echo(params string[] lines){
  string script=Path.Combine(Fresh("echo-"+Guid.NewGuid().ToString("N").Substring(0,6)),"out.cmd");
  File.WriteAllLines(script,new[]{"@echo off"}.Concat(lines.Select(l=>"echo "+l)).ToArray());
  return new System.Diagnostics.Process{StartInfo=new System.Diagnostics.ProcessStartInfo("cmd.exe","/d /c \""+script+"\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true}};
 }
}
