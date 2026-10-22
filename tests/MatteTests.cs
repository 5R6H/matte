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
 internal void TestRefreshStore(){RefreshStore();}
 internal bool TestSweeping{get{return sweeping;}}
 internal SpatialRetention.Result TestRetention{get{return retentionResult;}}
 internal string TestStartGeneration(){return StartGeneration();}
 internal void TestSweepAge(double hours){lastSweep=DateTime.Now.AddHours(-hours);}
 internal void TestSweep(bool after=false){RunSpatialSweep(after);}
 internal void TestStoreView(SpatialCatalog c,SpatialRetention.Result r){if(panel==null)CreatePanel();catalogLoading=true;storeInfo=new StoreInfo{Dir=SpatialCacheDir,Catalog=c,Files=c.Valid+c.Legacy,Bytes=c.Bytes};lastSweepStore=SpatialCacheDir;retentionResult=r;PaintStore();}
 internal string TestText(string name){return P<System.Windows.Controls.TextBlock>(name).Text;}
 internal bool TestVisible(string name){return P<System.Windows.FrameworkElement>(name).Visibility==System.Windows.Visibility.Visible;}
 internal void TestClick(string name){P<System.Windows.Controls.Button>(name).RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));}
 internal bool TestLegacyCleaning{get{return legacyCleaning;}}
 internal bool TestLegacyArmed{get{return legacyArmed;}}
 internal void TestMoveTo(string dest){MoveStoreTo(false,dest);}
 internal bool TestMoving{get{return storeMoving;}}
 internal bool TestLicence(bool agreed){if(panel==null)CreatePanel();sharpAgreed=agreed;sharpView="license";PaintSharp();return P<System.Windows.Controls.Button>("SharpRightBtn").IsEnabled;}
 internal void TestRenderPage(string name,string path,int height){var v=P<System.Windows.FrameworkElement>(name);var parent=System.Windows.Media.VisualTreeHelper.GetParent(v) as System.Windows.Controls.Panel;parent.Children.Remove(v);v.Visibility=System.Windows.Visibility.Visible;v.Opacity=1;v.RenderTransform=new System.Windows.Media.TranslateTransform();
  var frame=new System.Windows.Controls.Border{Width=320,Height=height,Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(35,38,42)),Resources=panel.Resources,Child=v};System.Windows.Documents.TextElement.SetForeground(frame,panel.Foreground);System.Windows.Documents.TextElement.SetFontFamily(frame,panel.FontFamily);System.Windows.Documents.TextElement.SetFontSize(frame,panel.FontSize);frame.Measure(new System.Windows.Size(320,height));frame.Arrange(new System.Windows.Rect(0,0,320,height));frame.UpdateLayout();
  var image=new System.Windows.Media.Imaging.RenderTargetBitmap(640,height*2,192,192,System.Windows.Media.PixelFormats.Pbgra32);image.Render(frame);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));using(var f=File.Create(path))png.Save(f);
  frame.Child=null;parent.Children.Add(v);
 }
 internal void TestOpenStore(){
  ui=new MessageWindow();CreatePanel();panel.ShowActivated=false;panel.ShowInTaskbar=false;panel.Left=-20000;panel.Top=-20000;panel.Show();
  page="spatialstore";RefreshStore();
 }
 internal void TestClose(){if(legacyDisarm!=null)legacyDisarm.Stop();genQueued=false;if(sweepCancel!=null)sweepCancel.Cancel();CancelSlideRequests();ReleaseDisplayPins();PinWallpaper(null);System.Threading.Interlocked.Increment(ref catalogRequest);if(panel!=null)panel.Close();if(ui!=null)ui.Dispose();}
 internal void TestCatalog(SpatialCatalog catalog,params string[] photos){ApplySpatialCatalog(catalog,photos.Select(p=>Tuple.Create(p,new List<string>{p})).ToList(),true);}
 internal string TestSpatialPath(string path){return SpatialPathFor(path);}
 internal int TestSpatialSlides{get{return spatialSlides.Count;}}
 internal int[] TestNeeds(){var n=SpatialNeeds();return new[]{n.Photos,n.Missing,n.Unchecked,n.Errors,n.RecipeUnknown?1:0};}
 internal bool[] TestGenerateButtons(){TestPanelGenText();sharpView="ready";PaintSharp();return new[]{P<System.Windows.Controls.Button>("GenBtn").IsEnabled,P<System.Windows.Controls.Button>("SharpRightBtn").IsEnabled&&(string)P<System.Windows.Controls.Button>("SharpRightBtn").Content=="开始生成"};}
 internal void TestLoadCatalog(){LoadSpatialCatalog();}
 internal bool TestCatalogLoading{get{return catalogLoading;}}
 internal SpatialCatalog TestCatalogResult{get{return spatialCatalog;}}
 internal void TestShow(string path){ShowSlide(path);}
 internal void TestSpatialOn(bool on){spatialEnabled=on;ReevaluateCurrentSpatial();}
 internal void TestPowerFlat(){spatialFlat=true;ReevaluateCurrentSpatial();}
 internal void TestPaused(){spatialPaused=true;ReevaluateCurrentSpatial();}
 internal DateTime TestCountdown{get{return nextSlide;}}
 internal int TestRotation{get{return slideIndex;}}
 internal SlideSnapshot TestSnapshot{get{return currentImage;}}
 internal void TestAhead(string path){slideFiles=new List<string>{path};slideIndex=0;PrepareNextSlide();}
 internal string TestAmbient(SlideSnapshot image){string backdrop;return WallpaperImage(image,"ambient",new Size(160,100),out backdrop);}
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
  if(args.Length>=2&&(args[0]=="--native-validate"||args[0]=="--native-fault"))return NativeChild(args);
  if(args.Contains("--native")){NativeTests();return failures;}
  if(args.Length==3&&args[0]=="--hold-lock"){using(SpatialStoreLock.Acquire(args[1],3000)){Console.WriteLine("LOCKED");Console.Out.Flush();System.Threading.Thread.Sleep(int.Parse(args[2]));}return 0;}
  if(args.Contains("--format")){FormatTests();return failures;}
  if(args.Contains("--locks")){LockTests();return failures;}
  if(args.Contains("--store")){StoreTests();return failures;}
  if(args.Contains("--catalog")){CatalogTests();return failures;}
  if(args.Contains("--attach")){AttachTests();CacheTests();return failures;}
  if(args.Contains("--cache")){CacheTests();return failures;}
  if(args.Contains("--retention")){RetentionTests();return failures;}
  if(args.Contains("--store-page")){StorePageTests();return failures;}
  IdentityTests();if(args.Contains("--identity"))return failures;
  StoreTests();
  LockTests();
  CatalogTests();NativeTests();AttachTests();CacheTests();RetentionTests();StorePageTests();
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
    using(var snap=new SlideSnapshot(p,File.ReadAllBytes(p))){var buffer=new System.Windows.Media.Imaging.FormatConvertedBitmap(snap.Pixels(),System.Windows.Media.PixelFormats.Bgra32,null,0);var pixels=new byte[buffer.PixelWidth*buffer.PixelHeight*4];buffer.CopyPixels(pixels,buffer.PixelWidth*4,0);Check(buffer.PixelWidth==w.PixelWidth&&buffer.PixelHeight==w.PixelHeight&&pixels.SequenceEqual(px),"orientation "+o+": snapshot and path transforms differ");}
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
   string py=File.ReadAllText(Path.Combine(Here,@"..\..\spatial\publish.py"));
   Check(py.Contains("emit(f'Saved: {photo.name}')"),"spatial\\publish.py no longer emits Saved: <name>, which GenProgress counts");
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
 static System.Diagnostics.Process LockChild(string command,string arguments){return App.StartDirect(new System.Diagnostics.ProcessStartInfo(command,arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true});}
 static void LockReady(System.Diagnostics.Process child){var line=System.Threading.Tasks.Task.Run(()=>child.StandardOutput.ReadLine());Check(line.Wait(5000)&&line.Result=="LOCKED","lock child did not acquire");}
 static void EndLockChild(System.Diagnostics.Process child){try{if(!child.HasExited){child.Kill();child.WaitForExit(5000);}}finally{child.Dispose();}}
 static void LockTimeout(string path,int timeout){bool blocked=false;try{using(SpatialStoreLock.Acquire(path,timeout)){}}catch(TimeoutException){blocked=true;}Check(blocked,"second holder acquired the lock");}
 static void LockTests(){
  Run("T2b lock excludes another handle and releases",()=>{
   string path=Path.Combine(Fresh("b-lock"),"store.lock");var first=SpatialStoreLock.Acquire(path,0);
   try{LockTimeout(path,150);}finally{first.Dispose();first.Dispose();}
   using(SpatialStoreLock.Acquire(path,0)){}
  });
  Run("T2b lock excludes another process then waits for release",()=>{
   string path=Path.Combine(Fresh("b-process"),"store.lock");var child=LockChild(Path.Combine(Here,"MatteTests.exe"),"--hold-lock \""+path+"\" 700");
   try{LockReady(child);LockTimeout(path,100);var watch=System.Diagnostics.Stopwatch.StartNew();using(SpatialStoreLock.Acquire(path,3000)){Check(watch.ElapsedMilliseconds>=400,"did not wait for child's lock");}}
   finally{EndLockChild(child);}
  });
  Run("T2b move refuses held stable lock",()=>{
   string root=Fresh("b-move"),original=Path.Combine(root,"old"),dest=Path.Combine(root,"new");MoveFixture(original);bool failed=false;
   try{using(SpatialStoreLock.Acquire(SpatialStoreLock.DefaultPath,0)){
     try{SpatialStoreMove.Move(original,null,dest,null,s=>{});}catch(IOException e){failed=true;Check(e.Message=="存储正在使用，请稍后再移动","busy message not plain Chinese");}}
    Check(failed,"move bypassed the stable lock");Check(!SpatialStoreMove.IsJunction(original)&&!Directory.Exists(dest),"busy move changed store locations");
   }finally{RemoveTestJunction(original);}
  });
  Run("T2b move holds stable lock through verification",()=>{
   string root=Fresh("b-duration"),original=Path.Combine(root,"old"),dest=Path.Combine(root,"new");MoveFixture(original);bool checkedLock=false;
   try{SpatialStoreMove.BeforeVerify=stage=>{LockTimeout(SpatialStoreLock.GeneratorPath,0);LockTimeout(SpatialStoreLock.DefaultPath,0);checkedLock=true;};SpatialStoreMove.Move(original,null,dest,null,s=>{});Check(checkedLock,"verification hook not reached");}
   finally{SpatialStoreMove.BeforeVerify=null;RemoveTestJunction(original);}
  });
  Run("T2b move refuses held generator lock",()=>{
   string root=Fresh("b-generator-move"),original=Path.Combine(root,"old"),dest=Path.Combine(root,"new");MoveFixture(original);bool failed=false;
   var child=LockChild(Path.Combine(Here,"MatteTests.exe"),"--hold-lock \""+SpatialStoreLock.GeneratorPath+"\" 10000");
   try{LockReady(child);var watch=System.Diagnostics.Stopwatch.StartNew();
    try{SpatialStoreMove.Move(original,null,dest,null,s=>{});}catch(IOException e){failed=true;Check(e.Message=="正在生成立体照片，请结束后再移动","generator busy message not plain Chinese");}
    Check(failed,"move bypassed generator.lock");Check(watch.ElapsedMilliseconds<1000,"busy move did not fail fast");
    Check(!SpatialStoreMove.IsJunction(original)&&!Directory.Exists(dest),"busy move changed store locations");
   }finally{EndLockChild(child);RemoveTestJunction(original);}
  });
  Run("T2b move takes generator lock before store lock",()=>{
   string root=Fresh("b-lock-order"),original=Path.Combine(root,"old"),dest=Path.Combine(root,"new");MoveFixture(original);bool failed=false;
   using(SpatialStoreLock.Acquire(SpatialStoreLock.GeneratorPath,0))using(SpatialStoreLock.Acquire(SpatialStoreLock.DefaultPath,0)){
    var watch=System.Diagnostics.Stopwatch.StartNew();
    try{SpatialStoreMove.Move(original,null,dest,null,s=>{});}catch(IOException e){failed=true;Check(e.Message=="正在生成立体照片，请结束后再移动","store.lock acquired before generator.lock");}
    Check(failed&&watch.ElapsedMilliseconds<1000,"move waited on store.lock before generator.lock");}
  });
  string python=Environment.GetEnvironmentVariable("MATTE_TEST_PYTHON"),prefix=Environment.GetEnvironmentVariable("MATTE_TEST_PYTHON_ARGS")??"";
  if(string.IsNullOrEmpty(python)){Console.WriteLine("SKIP C#/Python store lock tests: no Python 3");return;}
  string script=Path.GetFullPath(Path.Combine(Here,@"..\..\spatial\test_publish.py"));
  Run("T2b C# lock blocks Python",()=>{
   string path=Path.Combine(Fresh("b-cs-py"),"store.lock");
   using(SpatialStoreLock.Acquire(path,0))using(var child=LockChild(python,prefix+" \""+script+"\" --try-lock \""+path+"\" 150")){
    Check(child.WaitForExit(5000),"Python lock probe hung");Check(child.ExitCode==2&&child.StandardOutput.ReadToEnd().Trim()=="TIMEOUT","Python acquired C#-held lock: "+child.StandardError.ReadToEnd());}
   using(var child=LockChild(python,prefix+" \""+script+"\" --try-lock \""+path+"\" 150")){
    Check(child.WaitForExit(5000)&&child.ExitCode==0,"Python could not acquire released C# lock");}
  });
  Run("T2b Python lock blocks C#",()=>{
   string path=Path.Combine(Fresh("b-py-cs"),"store.lock");var child=LockChild(python,prefix+" \""+script+"\" --hold-lock \""+path+"\" 700");
   try{LockReady(child);LockTimeout(path,100);Check(child.WaitForExit(5000)&&child.ExitCode==0,"Python holder failed");using(SpatialStoreLock.Acquire(path,0)){}}
   finally{EndLockChild(child);}
  });
 }
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
 static Dictionary<string,object> CatalogRecipe(int width=2560){return new Dictionary<string,object>{{"exporter","spatial-photos/1"},{"generator","matte-sharp/1"},{"height",1600},{"width",width},{"model",new string('0',64)},{"upright","exif-v1"}};}
 static string CatalogHash(byte[] data){using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();}
 static string CatalogPair(string store,string photo,int width=2560){
  byte[] data=File.ReadAllBytes(photo),artifact=SpatialFixture();var fields=CatalogRecipe(width);string source=CatalogHash(data),recipe=SpatialIdentity.RecipeSha256(fields),path=SpatialIdentity.ArtifactPath(store,source,recipe);
  Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllBytes(path,artifact);
  var sidecar=new Dictionary<string,object>{{"version",1},{"source",new Dictionary<string,object>{{"sha256",source},{"size",data.Length},{"name",Path.GetFileName(photo)}}},{"recipe",new Dictionary<string,object>{{"sha256",recipe},{"fields",fields}}},{"artifact",new Dictionary<string,object>{{"file",Path.GetFileName(path)},{"sha256",CatalogHash(artifact)},{"size",artifact.Length}}},{"created","2026-10-23T01:00:00Z"}};
  File.WriteAllText(path+".json",SpatialIdentity.SidecarJson(sidecar));return path;
 }
 // A native sink stand-in: receives only verified leases; releases old scenes on transition completion.
 sealed class SlideSink:IDisposable {
  internal readonly List<string> Events=new List<string>();internal readonly SpatialLeases Leases=new SpatialLeases();internal int Sequence;internal SlideSnapshot Image;
  internal void Show(SlideSnapshot image,SpatialVerification verified){Image=image;Events.Add((verified==null?"flat:":"3d:")+image.Source);if(verified==null)Leases.Dispose();else{Leases.Add(++Sequence,verified.TakeSceneLease());Leases.TransitionDone(Sequence);}}
  public void Dispose(){Leases.Dispose();}
 }
 static void Until(Func<bool> done,string why,int ms=6000){var watch=System.Diagnostics.Stopwatch.StartNew();while(!done()&&watch.ElapsedMilliseconds<ms)Pump(20);Check(done(),why);}
 static App AttachApp(string store,SlideSink sink,params string[] photos){var app=new App(new TestWall()){TestSettings=new MatteSettings{Playlist=photos.ToList(),SpatialFolder=store}};app.presentSlide=sink.Show;app.TestCatalog(SpatialCatalog.Load(store,photos,SpatialIdentity.RecipeSha256(CatalogRecipe())).Complete(()=>false),photos);app.TestSpatialOn(true);return app;}
 static void Bmp(string path,Color color){using(var b=new Bitmap(32,24)){using(var g=Graphics.FromImage(b))g.Clear(color);b.Save(path,ImageFormat.Bmp);}}
 static bool Writable(string path){try{using(var s=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.ReadWrite|FileShare.Delete)){}return true;}catch(IOException){return false;}}
 [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 static void StorePageTests(){
  Run("T2e summary has exactly the nonzero lines",()=>{
   var c=SpatialCatalog.Load(Fresh("e-summary"),new string[0],null);var date=new DateTime(2026,11,25,12,0,0,DateTimeKind.Utc);
   for(int bits=0;bits<64;bits++){c.Valid=(bits&1)!=0?7:0;c.Legacy=(bits&2)!=0?3:0;c.Unsupported=(bits&32)!=0?5:0;
    var r=new SpatialRetention.Result{Missing=(bits&4)!=0?2:0,Unknown=((bits&8)!=0?4:0)+c.Unsupported,Unsupported=c.Unsupported,Quarantined=(bits&16)!=0?6:0,EarliestClearUtc=date,LastCompleteScan=date};var lines=new List<string>();
    if(c.Valid>0)lines.Add("已有 7 张 3D 图片");if(c.Legacy>0)lines.Add("3 张旧格式 · 需要重新生成才会显示立体效果");if(r.Missing>0)lines.Add("2 张的原图暂时找不到 · 至少保留到 "+date.ToLocalTime().ToString("M月d日"));
    if((bits&8)!=0)lines.Add("4 张的原图在未连接的磁盘上 · 保留");if(r.Quarantined>0)lines.Add("6 个文件已隔离（无法识别或已损坏）· 30 天后移到回收站");if(c.Unsupported>0)lines.Add("5 张来自更新版本的 Matte · 不会改动");
    Check(App.StoreSummary(c,r)==string.Join("\n",lines),"summary has missing, zero or duplicate lines for mask "+bits);Check(App.StoreFooter(r)=="上次完整检查："+date.ToLocalTime().ToString("yyyy年M月d日 HH:mm"),"complete-scan footer differs");
   }
   c.Valid=c.Legacy=c.Unsupported=0;Check(App.StoreSummary(c,null)==""&&App.StoreFooter(null)=="还没有完成过完整检查","empty summary or first-scan footer differs");
  });
  Run("T2e legacy explanation follows count and uses supplied text",()=>{
   string store=Fresh("e-visibility");var c=SpatialCatalog.Load(store,new string[0],null);var app=new App(new TestWall()){TestSettings=new MatteSettings{SpatialFolder=store}};
   try{foreach(int count in new[]{0,1,0}){c.Legacy=count;app.TestStoreView(c,null);Check(app.TestVisible("StoreLegacyBox")== (count>0),"legacy explanation visibility differs from count");}
    Check(app.TestText("StoreLegacyInfo")=="新版本按照片内容识别 3D 图片，旧格式的 3D 图片无法确认对应哪张照片，为了不显示错误的立体效果，暂时以普通照片显示。重新生成后会恢复立体效果，旧文件不会被自动删除。","legacy explanation changed");
   }finally{app.TestClose();}
  });
  Run("T2e legacy clean requires second confirmation and excludes v1",()=>{
   string store=Fresh("e-clean"),legacy=Path.Combine(store,"old.spatial"),nested=Path.Combine(store,"v1","a","keep.spatial"),other=Path.Combine(store,"keep.json");Directory.CreateDirectory(Path.GetDirectoryName(nested));File.WriteAllText(legacy,"old");File.WriteAllText(nested,"new");File.WriteAllText(other,"keep");int calls=0;
   var app=new App(new TestWall()){TestSettings=new MatteSettings{SpatialFolder=store}};app.legacyRecycle=files=>{calls++;foreach(string p in files)File.Delete(p);};
   try{app.TestStoreView(SpatialCatalog.Load(store,new string[0],null),null);app.TestClick("StoreLegacyBtn");Pump(100);Check(calls==0&&File.Exists(legacy)&&app.TestLegacyArmed,"legacy cleaned without second confirmation");
    app.TestClick("StoreLegacyBtn");Until(()=>!app.TestLegacyCleaning,"legacy cleanup unfinished");Check(calls==1&&!File.Exists(legacy),"confirmed legacy cleanup did not recycle");Check(File.Exists(nested)&&File.Exists(other),"legacy cleanup entered v1 or touched non-legacy files");Until(()=>!app.TestCatalogLoading,"cleanup catalogue unfinished");Pump(50);
   }finally{app.TestClose();}
  });
  Run("T2e legacy confirmation expires",()=>{
   string store=Fresh("e-expire"),legacy=Path.Combine(store,"old.spatial");File.WriteAllText(legacy,"old");int calls=0;var app=new App(new TestWall()){TestSettings=new MatteSettings{SpatialFolder=store}};app.legacyRecycle=files=>calls++;
   try{app.TestStoreView(SpatialCatalog.Load(store,new string[0],null),null);app.TestClick("StoreLegacyBtn");Pump(3200);app.TestClick("StoreLegacyBtn");Check(calls==0&&File.Exists(legacy)&&app.TestLegacyArmed,"expired confirmation still authorized cleanup");}finally{app.TestClose();}
  });
  Run("T2e overlong current or selected store refuses move",()=>{
   string shortPath=Path.GetPathRoot(Here)+"short-store",longPath=shortPath+new string('x',180);var c=SpatialCatalog.Load(shortPath,new string[0],null);var app=new App(new TestWall()){TestSettings=new MatteSettings{SpatialFolder=shortPath}};
   try{app.TestStoreView(c,null);Check(!app.TestVisible("StorePathWarning"),"short path warning shown");app.TestMoveTo(longPath);Check(!app.TestMoving&&app.TestSettings.SpatialStoreTarget==null&&app.TestText("StoreMoveNote")=="这个位置的路径太长，请选一个更短的文件夹","overlong destination was not refused");
    app.TestSettings.SpatialStoreTarget=longPath;app.TestStoreView(c,null);Check(app.TestVisible("StorePathWarning"),"overlong current target not shown");app.TestMoveTo(shortPath);Check(!app.TestMoving&&app.TestSettings.SpatialStoreTarget==longPath,"overlong current store was not refused");
   }finally{app.TestClose();}
  });
  Run("T2e licence wording keeps the agreement gate and model link",()=>{
   var app=new App(new TestWall()){TestSettings=new MatteSettings()};try{Check(!app.TestLicence(false)&&app.TestLicence(true),"license acceptance gate changed");Check(App.SharpLicenseUrl.EndsWith("/LICENSE_MODEL"),"model license link missing");
    Check(app.TestText("SharpLicenseText")=="SHARP 的模型由 Apple 提供，许可证只允许\"以推进科学研究为唯一目的\"的非商业研究使用，不包括产品开发或任何商业用途。用它来制作个人壁纸是否在许可范围内，需要你自己判断；Matte 不附带也不分发这个模型，只在你同意后从 Apple 下载到你的电脑。","model license explanation changed");
    app.TestRenderPage("SharpPage",Path.Combine(Here,"t2e-license.png"),500);
   }finally{app.TestClose();}
   string repo=Directory.GetCurrentDirectory();foreach(string name in new[]{"README.md","spatial/README.md","THIRD_PARTY_NOTICES.md"}){string text=File.ReadAllText(Path.Combine(repo,name));Check(!text.Contains("non-commercial research use only")&&text.Contains("sole intent")&&text.Contains("product development")&&text.Contains("neither ships nor redistributes")&&text.Contains("not affiliated with or endorsed by Apple"),"incomplete licence wording in "+name);}
  });
  Run("T2e store view renders summary and legacy actions",()=>{
   string store=Fresh("e-render");var c=SpatialCatalog.Load(store,new string[0],null);c.Valid=7;c.Legacy=3;c.Unsupported=5;var r=new SpatialRetention.Result{Missing=2,Unknown=9,Unsupported=5,Quarantined=6,EarliestClearUtc=DateTime.UtcNow.AddDays(30),LastCompleteScan=DateTime.UtcNow};
   var app=new App(new TestWall()){TestSettings=new MatteSettings{SpatialFolder=store}};try{app.TestStoreView(c,r);Check(app.TestText("StoreAutoInfo")==App.StoreSummary(c,r),"store UI did not use summary");app.TestRenderPage("SpatialStorePage",Path.Combine(Here,"t2e-store.png"),750);}finally{app.TestClose();}
  });
 }
 static DateTime RetentionNow{get{return new DateTime(2026,10,23,1,0,0,DateTimeKind.Utc);}}
 static SpatialRetention.Result Retain(string store,IEnumerable<string> roots,DateTime now,SpatialRetention.IO io=null,Action<string> recycle=null){string locks=Path.Combine(Path.GetDirectoryName(store),"locks");return SpatialRetention.Sweep(store,roots,recycle??File.Delete,e=>{},now,io,Path.Combine(locks,"generator.lock"),Path.Combine(locks,"store.lock"));}
 static string RetentionKey(string artifact){return Path.GetFileName(Path.GetDirectoryName(artifact))+"/"+Path.GetFileNameWithoutExtension(artifact);}
 static void RetentionTests(){
  Run("T2d rename copy and history-only photos prove use",()=>{
   string root=Fresh("d-rename"),store=Path.Combine(root,"store"),photos=Path.Combine(root,"photos");Directory.CreateDirectory(photos);string photo=Path.Combine(photos,"a.jpg");File.WriteAllBytes(photo,new byte[]{1,2,3});string artifact=CatalogPair(store,photo);var now=RetentionNow;
   var first=Retain(store,new[]{photos},now);Check(first.InUse==1&&first.Complete&&first.LastCompleteScan==now,"initial complete use missing");string renamed=Path.Combine(photos,"renamed.jpg"),copy=Path.Combine(photos,"copy.jpg");File.Move(photo,renamed);File.Copy(renamed,copy);
   var r=Retain(store,new[]{photos},now.AddDays(40));Check(r.InUse==1&&r.Missing==0&&File.Exists(artifact),"renamed/copied photo was missed");var h=SpatialRetention.Load(store,now.AddDays(40));var rec=h.pairs[RetentionKey(artifact)];Check(rec.locations.ContainsKey(photo)&&rec.locations.ContainsKey(renamed)&&rec.locations.ContainsKey(copy),"source location history was lost");Check(rec.missingSince==null,"in-use pair has missing clock");
   r=Retain(store,new string[0],now.AddDays(80));Check(r.InUse==1&&r.Recycled==0,"history locations were not scanned");
  });
  Run("R15 usable hints avoid all 300 photo hashes",()=>{
   string root=Fresh("d-hints"),store=Path.Combine(root,"store"),photos=Path.Combine(root,"photos");Directory.CreateDirectory(photos);var paths=new List<string>();
   for(int i=0;i<300;i++){string p=Path.Combine(photos,i+".jpg");File.WriteAllBytes(p,new byte[]{(byte)i,(byte)(i>>8),1});paths.Add(p);CatalogPair(store,p);}
   SpatialCatalog.Load(store,paths,null).Complete(()=>false);int hashes=0;var io=new SpatialRetention.IO{Hash=p=>{hashes++;return CatalogHash(File.ReadAllBytes(p));}};
   var r=Retain(store,new[]{photos},RetentionNow,io);Check(r.Complete&&r.InUse==300&&r.Missing==0&&hashes==0,"hinted library was rehashed: "+hashes);
   Check(SpatialRetention.Load(store,RetentionNow).pairs.Values.All(x=>x.locations.Count==1&&x.missingSince==null),"hinted locations not retained");
  });
  Run("R15 stale positive only delays cleanup and never proves absence",()=>{
   string root=Fresh("d-stale"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"a.jpg"),gone=Path.Combine(root,"gone.jpg");File.WriteAllBytes(photo,new byte[]{1,2,3});string old=CatalogPair(store,photo);SpatialCatalog.Load(store,new[]{photo},null).Complete(()=>false);var stamp=SpatialCatalog.Probe(photo);
   File.WriteAllBytes(photo,new byte[]{4,5,6});File.SetLastWriteTimeUtc(photo,new DateTime(stamp.mtimeTicks,DateTimeKind.Utc));Check(SpatialCatalog.Probe(photo).fileId==stamp.fileId,"stale index fixture changed file id");string current=CatalogPair(store,photo);
   File.WriteAllBytes(gone,new byte[]{7,8,9});string absent=CatalogPair(store,gone);File.Delete(gone);
   var r=Retain(store,new[]{photo},RetentionNow);var h=SpatialRetention.Load(store,RetentionNow);
   Check(r.Missing==1&&r.InUse==2&&h.pairs[RetentionKey(current)].missingSince==null,"hint was used to prove absence of the replacement source");
   Check(h.pairs[RetentionKey(old)].missingSince==null&&h.pairs[RetentionKey(absent)].missingSince!=null,"stale positive did not retain old pair or unhinted absence was missed");
   r=Retain(store,new[]{photo},RetentionNow.AddDays(30));Check(File.Exists(old)&&File.Exists(current)&&!File.Exists(absent)&&r.RecycledPairs==1,"hint delayed anything except its own positive pair");
  });
  Run("R15 page opens reuse last result and respect sweep intervals",()=>{
   string root=Fresh("d-throttle"),store=Path.Combine(root,"store");var app=new App(new TestWall()){TestSettings=new MatteSettings{Playlist=new List<string>(),SpatialFolder=store}};
   try{app.TestOpenStore();Until(()=>!app.TestSweeping,"page sweep unfinished");var result=app.TestRetention;app.TestRefreshStore();Pump(100);Check(app.TestSweeps==1&&app.TestRetention==result,"two page opens started two sweeps");
    app.TestSweepAge(2);app.TestSweep();Check(app.TestSweeps==1,"periodic sweep ignored six-hour interval");app.TestRefreshStore();Until(()=>!app.TestSweeping,"hour-old page sweep unfinished");Check(app.TestSweeps==2,"page did not refresh after an hour");
    app.TestSweep(true);Until(()=>!app.TestSweeping,"post-generation sweep unfinished");Check(app.TestSweeps==3,"generation did not bypass throttle");app.TestSweepAge(6.1);app.TestSweep();Until(()=>!app.TestSweeping,"six-hour sweep unfinished");Check(app.TestSweeps==4,"periodic sweep never resumed");
   }finally{app.TestClose();}
  });
  Run("R16 queued generation cancels sweep and starts after lock release",()=>{
   string root=Fresh("d-queue"),store=Path.Combine(root,"store"),photos=Path.Combine(root,"photos"),source=Path.Combine(root,"source.jpg");Directory.CreateDirectory(photos);
   File.WriteAllBytes(source,new byte[]{0,0,0});string artifact=CatalogPair(store,source);Retain(store,new string[0],DateTime.UtcNow.AddDays(-40));
   for(int i=0;i<100;i++)File.WriteAllBytes(Path.Combine(photos,i+".jpg"),new byte[]{(byte)i,1,1});
   string script=Path.Combine(root,"generate.ps1"),started=Path.Combine(root,"started"),finish=Path.Combine(root,"finish"),failed=Path.Combine(root,"failed");
   Func<string,string> quote=p=>"'"+p.Replace("'","''")+"'";
   File.WriteAllText(script,"param($RootsFile,$Cache)\n$ErrorActionPreference='Stop'\ntry { $f=[IO.File]::Open("+quote(SpatialStoreLock.GeneratorPath)+",[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite); $f.Lock(0,1); [IO.File]::WriteAllText("+quote(started)+",'started'); while(!(Test-Path -LiteralPath "+quote(finish)+")){Start-Sleep -Milliseconds 50}; $f.Unlock(0,1); $f.Dispose() } catch { [IO.File]::WriteAllText("+quote(failed)+",'another generation is running'); exit 1 }",new System.Text.UTF8Encoding(true));
   using(var entered=new System.Threading.ManualResetEventSlim())using(var release=new System.Threading.ManualResetEventSlim()){
    int hashes=0;var app=new App(new TestWall()){TestSettings=new MatteSettings{Playlist=new List<string>{photos},SpatialFolder=store,SpatialGenerator=script}};
    app.retentionIO=new SpatialRetention.IO{Hash=p=>{if(System.Threading.Interlocked.Increment(ref hashes)==1){entered.Set();release.Wait(10000);}return CatalogHash(File.ReadAllBytes(p));}};
    try{app.TestOpenStore();Until(()=>entered.IsSet,"scan did not reach first photo");Check(app.TestStartGeneration()==null,"generation request rejected");
     Check(app.TestGenerating&&app.TestPanelGenText().Contains("正在整理 3D 图片，整理完自动开始"),"generation started without waiting for sweep");
     Pump(150);Check(!File.Exists(started)&&!File.Exists(failed),"generator launched while sweep held lock");release.Set();Until(()=>File.Exists(started)||File.Exists(failed),"queued generation did not start",6000);
     var r=app.TestRetention;Check(!File.Exists(failed)&&r!=null&&r.Cancelled&&!r.Complete&&r.Recycled==0&&hashes==1,"cancelled scan did not stop early before generation");
     Check(File.Exists(artifact)&&SpatialRetention.Load(store,DateTime.UtcNow).pairs.Values.All(x=>x.missingSince==null),"cancelled scan deleted pair or retained old clock");
     File.WriteAllText(finish,"done");Until(()=>!app.TestGenerating&&!app.TestSweeping,"generator did not finish",10000);
    }finally{release.Set();File.WriteAllText(finish,"done");if(app.TestGenerating)app.TestStop();Until(()=>!app.TestGenerating&&!app.TestSweeping,"cleanup unfinished",10000);app.TestClose();}
   }
  });
  Run("R16 cancellation resets clocks without touching quarantine",()=>{
   string root=Fresh("d-cancel"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"a.jpg");File.WriteAllBytes(photo,new byte[]{1});string artifact=CatalogPair(store,photo);Retain(store,new string[0],RetentionNow);File.WriteAllBytes(photo,new byte[]{2});
   string orphan=SpatialIdentity.ArtifactPath(store,new string('b',64),new string('c',64));Directory.CreateDirectory(Path.GetDirectoryName(orphan));File.WriteAllText(orphan,"orphan");
   string aged=SpatialIdentity.QuarantinePath(store,new string('d',64),new string('e',64),RetentionNow);Directory.CreateDirectory(Path.GetDirectoryName(aged));File.WriteAllText(aged,"aged");
   bool cancel=false;int recycled=0;var io=new SpatialRetention.IO{Cancelled=()=>cancel,Hash=p=>{cancel=true;return CatalogHash(File.ReadAllBytes(p));}};
   var r=Retain(store,new[]{photo},RetentionNow.AddDays(40),io,p=>{recycled++;File.Delete(p);});
   Check(r.Cancelled&&!r.Complete&&recycled==0&&File.Exists(artifact)&&File.Exists(orphan)&&File.Exists(aged),"cancelled sweep mutated a pair or quarantine");
   Check(SpatialRetention.Load(store,RetentionNow.AddDays(40)).pairs.Values.All(x=>x.missingSince==null),"cancelled sweep preserved missing clocks");
  });
  Run("T2d offline resets the entire missing interval",()=>{
   string root=Fresh("d-offline"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"a.jpg");File.WriteAllBytes(photo,new byte[]{1});string artifact=CatalogPair(store,photo);var now=RetentionNow;Retain(store,new[]{photo},now);File.Delete(photo);
   var r=Retain(store,new string[0],now.AddDays(1));Check(r.Missing==1,"online missing file not recorded");bool online=false;var io=new SpatialRetention.IO{DriveThere=p=>online};
   r=Retain(store,new string[0],now.AddDays(41),io);Check(!r.Complete&&r.Unknown==1&&r.Recycled==0&&File.Exists(artifact),"offline source recycled");Check(r.LastCompleteScan==now.AddDays(1),"incomplete scan changed last complete date");Check(SpatialRetention.Load(store,now.AddDays(41)).pairs[RetentionKey(artifact)].missingSince==null,"incomplete scan kept missing clock");
   online=true;r=Retain(store,new string[0],now.AddDays(42),io);Check(r.Recycled==0&&r.EarliestClearUtc==now.AddDays(72),"clock did not restart after offline");Retain(store,new string[0],now.AddDays(71),io);Check(File.Exists(artifact),"recycled before new full interval");Retain(store,new string[0],now.AddDays(72),io);Check(!File.Exists(artifact),"continuous complete interval did not clear pair");
  });
  foreach(string loss in new[]{"deleted","corrupt","unreadable"})Run("T2d "+loss+" retention starts fresh clocks",()=>{
   string root=Fresh("d-history-"+loss),store=Path.Combine(root,"store"),photo=Path.Combine(root,"a.jpg");File.WriteAllBytes(photo,new byte[]{2});string artifact=CatalogPair(store,photo);var now=RetentionNow;Retain(store,new[]{photo},now);File.Delete(photo);Retain(store,new string[0],now.AddDays(1));string history=Path.Combine(store,"v1","retention.json");
   if(loss=="deleted")File.Delete(history);else if(loss=="corrupt")File.WriteAllText(history,"bad");else{using(var held=new FileStream(history,FileMode.Open,FileAccess.Read,FileShare.None)){var blocked=Retain(store,new string[0],now.AddDays(40));Check(!blocked.Complete&&blocked.Recycled==0&&File.Exists(artifact),"unreadable history authorized recycling");}}
   var r=Retain(store,new string[0],now.AddDays(41));Check(r.Recycled==0&&r.EarliestClearUtc==now.AddDays(71),"lost history reused an old date");Retain(store,new string[0],now.AddDays(70));Check(File.Exists(artifact),"lost history interval shorter than 30 days");Retain(store,new string[0],now.AddDays(71));Check(!File.Exists(artifact),"new complete interval did not recycle");
  });
  Run("T2d hashes each candidate once across twenty pairs",()=>{
   string root=Fresh("d-once"),store=Path.Combine(root,"store"),photos=Path.Combine(root,"photos");Directory.CreateDirectory(photos);var paths=new List<string>();for(int i=0;i<500;i++){string p=Path.Combine(photos,i+".jpg");File.WriteAllBytes(p,new byte[]{(byte)i,(byte)(i>>8),7,8});paths.Add(p);}
   for(int i=0;i<20;i++){string p=Path.Combine(root,"source"+i+".jpg");File.WriteAllBytes(p,new byte[]{(byte)i,0,9,10});CatalogPair(store,p);File.Delete(p);}
   var calls=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);var io=new SpatialRetention.IO{Hash=p=>{int n;calls.TryGetValue(p,out n);calls[p]=n+1;return CatalogHash(File.ReadAllBytes(p));}};
   var r=Retain(store,new[]{photos,photos},RetentionNow,io);Check(r.Complete&&r.Missing==20&&calls.Count==500&&calls.Values.All(n=>n==1),"candidate photo hashed more than once per sweep");
  });
  Run("T2d held artifact preserves its sidecar and clock",()=>{
   string root=Fresh("d-held"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"a.jpg");File.WriteAllBytes(photo,new byte[]{3});string artifact=CatalogPair(store,photo);var now=RetentionNow;Retain(store,new[]{photo},now);File.Delete(photo);Retain(store,new string[0],now.AddDays(1));var attempted=new List<string>();
   using(var held=new FileStream(artifact,FileMode.Open,FileAccess.Read,FileShare.Read)){var r=Retain(store,new string[0],now.AddDays(31),null,p=>{attempted.Add(p);File.Delete(p);});Check(File.Exists(artifact)&&File.Exists(artifact+".json")&&attempted.SequenceEqual(new[]{artifact}),"sidecar recycled before locked artifact");Check(r.Missing==1&&r.RecycledPairs==0&&r.EarliestClearUtc==now.AddDays(31),"locked pair clock was not kept");}
   var cleared=Retain(store,new string[0],now.AddDays(32));Check(cleared.RecycledPairs==1&&!File.Exists(artifact)&&!File.Exists(artifact+".json"),"released pair did not recycle");
  });
  Run("T2d generation lock skips all sweep work",()=>{
   string root=Fresh("d-lock"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"a.jpg");File.WriteAllBytes(photo,new byte[]{4});string artifact=CatalogPair(store,photo),run=Path.Combine(root,"locks","generator.lock");int calls=0;var io=new SpatialRetention.IO{Files=p=>{calls++;return Directory.GetFiles(p);},Hash=p=>{calls++;return CatalogHash(File.ReadAllBytes(p));}};
   using(SpatialStoreLock.Acquire(run,0)){var watch=System.Diagnostics.Stopwatch.StartNew();var r=Retain(store,new[]{photo},RetentionNow,io,p=>{calls++;File.Delete(p);});Check(r.Skipped&&watch.ElapsedMilliseconds<1000&&calls==0&&!File.Exists(Path.Combine(store,"v1","retention.json")),"sweep ran while generation held its lock");}
   Check(File.Exists(artifact),"busy sweep mutated artifact");
  });
  Run("T2d unsupported retained orphan quarantined and aged by entry date",()=>{
   string root=Fresh("d-quarantine"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"a.jpg");File.WriteAllBytes(photo,new byte[]{5});string artifact=CatalogPair(store,photo);File.WriteAllText(artifact+".json","{\"version\":2}");string orphan=SpatialIdentity.ArtifactPath(store,new string('b',64),new string('c',64));Directory.CreateDirectory(Path.GetDirectoryName(orphan));File.WriteAllText(orphan,"orphan");File.SetLastWriteTimeUtc(orphan,RetentionNow.AddYears(-2));
   string stray=Path.Combine(store,"v1","stray");Directory.CreateDirectory(stray);File.WriteAllText(Path.Combine(stray,Path.GetFileName(orphan)),"untouched");var r=Retain(store,new[]{photo},RetentionNow);string q=SpatialIdentity.QuarantinePath(store,new string('b',64),new string('c',64),RetentionNow);
   Check(r.Unsupported==1&&File.Exists(artifact)&&File.Exists(artifact+".json"),"unsupported pair was touched");Check(!File.Exists(orphan)&&File.Exists(q)&&r.Quarantined==1&&r.QuarantinedThisSweep==1,"orphan did not enter quarantine");Check(File.Exists(Path.Combine(stray,Path.GetFileName(orphan))),"entered stray v1 directory");
   Retain(store,new[]{photo},RetentionNow.AddDays(29));Check(File.Exists(q),"quarantine used original old mtime");r=Retain(store,new[]{photo},RetentionNow.AddDays(30));Check(!File.Exists(q)&&r.RecycledQuarantine==1&&File.Exists(artifact),"quarantine not recycled at thirty days or unsupported touched");
  });
  Run("T2d incomplete listing and unreadable candidate reset all clocks",()=>{
   string root=Fresh("d-incomplete"),store=Path.Combine(root,"store"),photos=Path.Combine(root,"photos");Directory.CreateDirectory(photos);string photo=Path.Combine(photos,"a.jpg");File.WriteAllBytes(photo,new byte[]{6});string artifact=CatalogPair(store,photo);Retain(store,new[]{photos},RetentionNow);File.Delete(photo);Retain(store,new[]{photos},RetentionNow.AddDays(1));
   var io=new SpatialRetention.IO{Files=p=>{if(p==photos)throw new UnauthorizedAccessException();return Directory.GetFiles(p);}};var r=Retain(store,new[]{photos},RetentionNow.AddDays(40),io);Check(!r.Complete&&File.Exists(artifact)&&SpatialRetention.Load(store,RetentionNow.AddDays(40)).pairs.Values.All(x=>x.missingSince==null),"listing failure preserved deletion clock");
   Retain(store,new[]{photos},RetentionNow.AddDays(41));File.WriteAllBytes(photo,new byte[]{7});io=new SpatialRetention.IO{Hash=p=>{throw new IOException("unreadable");}};r=Retain(store,new[]{photos},RetentionNow.AddDays(80),io);Check(!r.Complete&&r.Unknown==1&&r.Recycled==0&&SpatialRetention.Load(store,RetentionNow.AddDays(80)).pairs.Values.All(x=>x.missingSince==null),"unreadable candidate preserved deletion clock");
  });
  Run("T2d reclassifies immediately before recycling",()=>{
   string root=Fresh("d-reclassify"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"a.jpg");File.WriteAllBytes(photo,new byte[]{8});string artifact=CatalogPair(store,photo);Retain(store,new string[0],RetentionNow);File.WriteAllBytes(photo,new byte[]{9});
   var io=new SpatialRetention.IO{Hash=p=>{File.WriteAllText(artifact+".json","{\"version\":2}");return CatalogHash(File.ReadAllBytes(p));}};var r=Retain(store,new[]{photo},RetentionNow.AddDays(30),io);Check(r.RecycledPairs==0&&File.Exists(artifact)&&File.Exists(artifact+".json"),"recycled a pair that became unsupported");
  });
  Run("T2d store lock skips and releases the generator lock",()=>{
   string root=Fresh("d-store-lock"),store=Path.Combine(root,"store"),file=Path.Combine(root,"locks","store.lock"),run=Path.Combine(root,"locks","generator.lock");using(SpatialStoreLock.Acquire(file,0)){var r=Retain(store,new string[0],RetentionNow);Check(r.Skipped&&!Directory.Exists(store),"store lock did not prevent sweep work");using(SpatialStoreLock.Acquire(run,0)){} }
  });
  Run("T2d invalid pairs quarantine collision without overwrite",()=>{
   string root=Fresh("d-collision"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"a.jpg");File.WriteAllBytes(photo,new byte[]{8});string artifact=CatalogPair(store,photo);File.WriteAllText(artifact+".json","bad");string source=Path.GetFileName(Path.GetDirectoryName(artifact)),recipe=Path.GetFileNameWithoutExtension(artifact),q=SpatialIdentity.QuarantinePath(store,source,recipe,RetentionNow);Directory.CreateDirectory(Path.GetDirectoryName(q));File.WriteAllText(q,"earlier");
   var r=Retain(store,new[]{photo},RetentionNow);string second=SpatialIdentity.QuarantinePath(store,source,recipe,RetentionNow.AddSeconds(1));Check(File.ReadAllText(q)=="earlier"&&File.Exists(second)&&File.Exists(Path.ChangeExtension(second,".json"))&&!File.Exists(artifact),"quarantine overwrote a recovery or missed invalid pair");Check(r.Quarantined==2,"quarantine pair counted as separate files");
  });
 }
 static void CacheTests(){
  Run("R13 flat JPEG slides make no display copies",()=>{
   string root=Fresh("r13-flat"),store=Path.Combine(root,"store");var photos=new List<string>();for(int i=0;i<8;i++){string p=Path.Combine(root,i+".jpg");Solid(p,Color.FromArgb(143+i,32+i,92),64,48,1);photos.Add(p);}
   var before=new HashSet<string>(Directory.Exists(App.DisplayCacheDir)?Directory.GetFiles(App.DisplayCacheDir):new string[0],StringComparer.OrdinalIgnoreCase);
   using(var sink=new SlideSink()){var app=AttachApp(store,sink,photos.ToArray());try{foreach(string photo in photos){int count=sink.Events.Count;app.TestShow(photo);Until(()=>sink.Events.Count>count,"flat slide not shown");Check(sink.Image.Display==photo,"flat JPEG created a display copy");}
     Check(!Directory.Exists(App.DisplayCacheDir)||Directory.GetFiles(App.DisplayCacheDir).All(before.Contains),"flat JPEG library copied into cache");
    }finally{app.TestClose();}}
  });
  Run("R13 verified underlay and prepared next stay pinned",()=>{
   string root=Fresh("r13-pins"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"a.bmp"),next=Path.Combine(root,"b.bmp");Bmp(photo,Color.Orange);Bmp(next,Color.Purple);CatalogPair(store,photo);string nextArtifact=CatalogPair(store,next);
   using(var entered=new System.Threading.ManualResetEventSlim())using(var release=new System.Threading.ManualResetEventSlim())using(var sink=new SlideSink()){
    var app=AttachApp(store,sink,photo,next);SlideSnapshot ahead=null;app.beforeVerifyDelivery=v=>{if(v.Image.Source==next){ahead=v.Image;entered.Set();release.Wait(10000);}};
    try{app.TestShow(photo);Until(()=>sink.Events.Contains("3d:"+photo),"verified slide not shown");string current=sink.Image.Display;Check(current!=photo&&File.ReadAllBytes(current).SequenceEqual(File.ReadAllBytes(photo)),"verified 3D underlay was not copied from its buffer");Check(sink.Image.ShowPath(false,true)==photo,"flat slide reused a non-WebP display copy");
     app.TestAhead(next);Until(()=>entered.IsSet,"next slide not prepared");Check(ahead.Display!=next,"next verified underlay not copied");DisplayCache.Trim(App.DisplayCacheDir,0);
     Check(File.Exists(current)&&File.Exists(ahead.Display),"evicted current or prepared-next display copy");
     app.TestClose();DisplayCache.Trim(App.DisplayCacheDir,0);Check(File.Exists(current),"evicted the native player's underlay");release.Set();Until(()=>Writable(nextArtifact),"cancelled prefetch kept artifact");
     sink.Dispose();Until(()=>{DisplayCache.Trim(App.DisplayCacheDir,0);return !File.Exists(current)&&!File.Exists(ahead.Display);},"released display copies remained pinned");
    }finally{release.Set();app.TestClose();}}
  });
  Run("R13 ambient 3D needs no extra source copy",()=>{
   string root=Fresh("r13-ambient"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"a.bmp");Bmp(photo,Color.Silver);CatalogPair(store,photo);
   using(var sink=new SlideSink()){var app=AttachApp(store,sink,photo);app.TestSettings.Background="ambient";try{app.TestShow(photo);Until(()=>sink.Events.Contains("3d:"+photo),"ambient slide not shown");Check(sink.Image.Display==photo&&sink.Image.Wallpaper!=photo&&File.Exists(sink.Image.Wallpaper),"ambient 3D made a redundant source copy");}finally{app.TestClose();}}
  });
  Run("R13 cache cap uses last use and skips pinned files",()=>{
   Check(DisplayCache.Pin("")==null,"empty Windows wallpaper path not accepted");Check(DisplayCache.DefaultLimit==1024L*1024*1024,"default display cap is not 1 GB");string root=Fresh("r13-lru");Action<string> write=p=>File.WriteAllBytes(p,new byte[10]);
   foreach(string name in new[]{"a.jpg","b.jpg","c.jpg"})using(DisplayCache.Acquire(root,name,write,30)){}
   DateTime epoch=new DateTime(2000,1,1,0,0,0,DateTimeKind.Utc);File.SetLastWriteTimeUtc(Path.Combine(root,"c.jpg"),epoch);File.SetLastWriteTimeUtc(Path.Combine(root,"a.jpg"),epoch.AddSeconds(1));File.SetLastWriteTimeUtc(Path.Combine(root,"b.jpg"),epoch.AddSeconds(2));
   using(DisplayCache.Pin(Path.Combine(root,"c.jpg"))){} // c was created first/oldest, but used most recently.
   using(DisplayCache.Acquire(root,"d.jpg",write,30)){}Check(!File.Exists(Path.Combine(root,"a.jpg"))&&File.Exists(Path.Combine(root,"b.jpg"))&&File.Exists(Path.Combine(root,"c.jpg")),"cache did not evict least recently used copy");Check(new DirectoryInfo(root).GetFiles().Sum(f=>f.Length)<=30,"cache exceeded cap with evictable files");
   using(var current=DisplayCache.Pin(Path.Combine(root,"c.jpg")))using(var next=DisplayCache.Pin(Path.Combine(root,"d.jpg"))){File.SetLastWriteTimeUtc(current.Path,epoch);File.SetLastWriteTimeUtc(next.Path,epoch);
    using(DisplayCache.Acquire(root,"e.jpg",write,20)){Check(File.Exists(current.Path)&&File.Exists(next.Path),"evicted an in-use display copy");Check(!File.Exists(Path.Combine(root,"b.jpg")),"kept an unused copy over the cap");}
   }DisplayCache.Trim(root,0);Check(Directory.GetFiles(root).Length==0,"released cache files were not evictable");
  });
 }
 // Exporter-layout fixtures only. JPEG pixels are generated here; no owner photos/cache are enumerated.
 static byte[] formatJpeg;
 static byte[] SpatialFixture(int slices=1,int gw=1,int gh=1,int block=16){
  if(formatJpeg==null)using(var bmp=new Bitmap(16,16))using(var stream=new MemoryStream()){using(var g=Graphics.FromImage(bmp))g.Clear(Color.CornflowerBlue);bmp.Save(stream,ImageFormat.Jpeg);formatJpeg=stream.ToArray();}
  long corners=SpatialFormat.CornerSlots(gw,gh),cells=(long)gw*gh;byte[] cm=new byte[(int)((corners+7)/8)],bm=new byte[(int)((cells+7)/8)];
  foreach(int n in new[]{0,1,gw+1,gw+2})cm[n/8]|=(byte)(1<<(n%8));bm[0]=1;
  byte[] geometry;using(var stream=new MemoryStream())using(var w=new BinaryWriter(stream)){for(int s=0;s<slices;s++){w.Write(4u);w.Write(1u);w.Write(cm);for(int n=0;n<4;n++)w.Write(1f+n*.1f);w.Write(bm);w.Write((byte)0);w.Write((byte)0);}geometry=stream.ToArray();}
  using(var stream=new MemoryStream())using(var w=new BinaryWriter(stream)){
   w.Write(4280403u);w.Write((uint)(gw*block));w.Write((uint)(gh*block));w.Write((uint)slices);w.Write((uint)block);w.Write(32f);w.Write(16u);w.Write(16u);w.Write(0u);w.Write(1f);
   w.Write((uint)(4*slices));w.Write((uint)slices);w.Write((uint)geometry.Length);w.Write((uint)formatJpeg.Length);w.Write((uint)formatJpeg.Length);w.Write(16u);w.Write(16u);w.Write(geometry);w.Write(formatJpeg);w.Write(formatJpeg);return stream.ToArray();
  }
 }
 static void FormatUInt(byte[] b,int at,uint n){Array.Copy(BitConverter.GetBytes(n),0,b,at,4);}
 static void FormatFloat(byte[] b,int at,float n){Array.Copy(BitConverter.GetBytes(n),0,b,at,4);}
 static void FormatBad(byte[] b,string reason){string why;SpatialFormat.Summary summary;long allocated=0;using(var stream=new FormatStream(b)){
  Check(!SpatialFormat.Validate(stream,out summary,out why,n=>{Check(n<=SpatialFormat.MaxValidationBytes,"allocation quota exceeded");allocated=n;}),"accepted malformed file: "+reason);
  Check(summary==null&&!string.IsNullOrEmpty(why)&&why.Contains(reason),"expected '"+reason+"', got '"+why+"'");Check(allocated<=SpatialFormat.MaxValidationBytes,"unbounded validation");
 }}
 sealed class FormatStream:Stream {
  readonly MemoryStream inner;readonly long length,readLimit;internal long ReadBytes,MaxRead;internal bool ThrowOnRead;
  internal FormatStream(byte[] data,long fakeLength=-1,long limit=-1){if(nativeCorpus!=null&&fakeLength<0)nativeCorpus.Add((byte[])data.Clone());inner=new MemoryStream(data,false);length=fakeLength<0?data.Length:fakeLength;readLimit=limit<0?data.Length*2L+128:limit;}
  public override bool CanRead{get{return true;}}public override bool CanSeek{get{return true;}}public override bool CanWrite{get{return false;}}
  public override long Length{get{return length;}}public override long Position{get{return inner.Position;}set{inner.Position=value;}}
  public override int Read(byte[] buffer,int offset,int count){Check(!ThrowOnRead,"cap ran after hashing/reading");MaxRead=Math.Max(MaxRead,count);int got=inner.Read(buffer,offset,count);ReadBytes+=got;Check(ReadBytes<=readLimit,"read work budget exceeded");return got;}
  public override int ReadByte(){byte[] b=new byte[1];return Read(b,0,1)==0?-1:b[0];}
  public override long Seek(long offset,SeekOrigin origin){return inner.Seek(offset,origin);}public override void Flush(){}public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] b,int o,int c){throw new NotSupportedException();}
  protected override void Dispose(bool disposing){if(disposing)inner.Dispose();base.Dispose(disposing);}
 }
 [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)] static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle,uint code,IntPtr input,uint inputSize,IntPtr output,uint outputSize,out uint returned,IntPtr overlapped);
 static void FormatSidecar(string artifact,byte[] bytes,long size=-1){var side=SpatialIdentity.ValidateSidecar(SpatialIdentity.ParseStrict(File.ReadAllText(artifact+".json")));var meta=(Dictionary<string,object>)side["artifact"];meta["size"]=size<0?bytes.LongLength:size;meta["sha256"]=CatalogHash(bytes);File.WriteAllText(artifact+".json",SpatialIdentity.SidecarJson(side));}
 static void FormatTests(){
  Run("T3a valid exporter layout, JPEG metadata and summary",()=>{
   foreach(int slices in new[]{1,2,64}){byte[] b=SpatialFixture(slices);string why;SpatialFormat.Summary s;using(var stream=new FormatStream(b)){stream.Position=3;
    Check(SpatialFormat.Validate(stream,out s,out why),why);Check(s.Slices==slices&&s.Blocks==slices&&s.DenseSlots==5*slices&&s.Vertices==4*slices&&s.Indices==6*slices&&s.MeshBytes==104*slices,"wrong geometry summary");
    Check(s.ColorOffset==68+28*slices&&s.AlphaOffset==s.ColorOffset+s.ColorBytes&&s.AlphaOffset+s.AlphaBytes==b.Length,"wrong section summary");Check(s.ColorWidth==16&&s.ColorHeight==16&&s.AlphaWidth==16&&s.AlphaHeight==16&&s.AtlasWidth==16&&s.AtlasHeight==16,"wrong JPEG summary");Check(s.ValidationBytes==74,"unexpected validator allocation");
   }}
   string root=Fresh("format-fixture");File.WriteAllBytes(Path.Combine(root,"tiny.spatial"),SpatialFixture());Console.WriteLine("INFO  no checked-in SHARP fixture; generated exporter-layout fixture with real JPEGs (owner spatial/pictures untouched)");
  });
  Run("T3a compatible boundary values and empty slices",()=>{
   foreach(int block in new[]{1,256}){var b=SpatialFixture(1,1,1,block);FormatFloat(b,36,0);FormatUInt(b,24,(uint)Math.Max(16,block));FormatUInt(b,28,(uint)Math.Max(16,block));FormatJpegSize(b,true,Math.Max(16,block),Math.Max(16,block));FormatJpegSize(b,false,Math.Max(16,block),Math.Max(16,block));string why;using(var stream=new FormatStream(b))Check(SpatialFormat.Validate(stream,out why),why);}
   var max=SpatialFixture(1,64,64,256);FormatUInt(max,24,8192);FormatUInt(max,28,8192);FormatUInt(max,60,16384);FormatUInt(max,64,16384);FormatJpegSize(max,true,8192,8192);FormatJpegSize(max,false,8192,8192);string reason;
   using(var stream=new FormatStream(max))Check(SpatialFormat.Validate(stream,out reason),reason);
   var two=SpatialFixture(2);var empty=two.Take(68).Concat(new byte[10]).Concat(two.Skip(96)).ToArray();FormatUInt(empty,40,4);FormatUInt(empty,44,1);FormatUInt(empty,48,38);
   using(var stream=new FormatStream(empty))Check(SpatialFormat.Validate(stream,out reason),reason);
  });
  Run("T3a header ranges and aggregate budgets",()=>{
   FormatBad(new byte[68],"not a Spatial");
   foreach(int at in new[]{4,8,12,16,24,28,60,64})foreach(uint value in new[]{0u,(uint)(at==12?65:at==16?257:16385),uint.MaxValue}){var b=SpatialFixture();FormatUInt(b,at,value);FormatBad(b,at==12?"slice count":at==16?"block size":at==24||at==28?"atlas dimensions":at>=60?"original dimensions":"image dimensions");}
   var huge=SpatialFixture();FormatUInt(huge,4,16384);FormatUInt(huge,8,16384);FormatUInt(huge,16,1);FormatFloat(huge,36,0);FormatBad(huge,"dense slot budget");
   FormatUInt(huge,12,64);FormatBad(huge,"dense slot budget");
   var aggregate=SpatialFixture();FormatUInt(aggregate,4,4096);FormatUInt(aggregate,8,4096);FormatUInt(aggregate,12,32);FormatBad(aggregate,"dense slot budget"); // Each slice fits; the sum does not.
   var blocks=SpatialFixture();FormatUInt(blocks,4,8192);FormatUInt(blocks,8,8192);FormatUInt(blocks,16,8);FormatUInt(blocks,44,(uint)SpatialFormat.MaxBlocks+1);FormatBad(blocks,"block budget");
   var pixels=SpatialFixture();FormatUInt(pixels,24,16384);FormatUInt(pixels,28,4097);FormatBad(pixels,"atlas pixel budget");
   var grid=SpatialFixture();FormatUInt(grid,16,32);FormatBad(grid,"empty block grid");grid=SpatialFixture();FormatUInt(grid,24,8);FormatBad(grid,"empty atlas grid");
   foreach(int at in new[]{40,44})foreach(uint v in new[]{0u,100u,uint.MaxValue}){var b=SpatialFixture();FormatUInt(b,at,v);FormatBad(b,at==40?"totalCorners":"totalBlocks");}
   Check(SpatialFormat.MaxVertices*20+SpatialFormat.MaxIndices*4==SpatialFormat.MaxMeshBytes&&SpatialFormat.MaxMeshBytes<=uint.MaxValue,"mesh limits do not fit UINT");
   // The outer 16384 bound prevents this overflow through Validate; test the arithmetic primitive for native parity.
   Check(SpatialFormat.CornerSlots(65535,65535)==4294967296L,"corner multiplication narrowed to uint");
   bool overflow=false;try{SpatialFormat.CornerSlots(long.MaxValue,1);}catch(OverflowException){overflow=true;}Check(overflow,"corner addition did not check overflow");
   overflow=false;try{SpatialFormat.CornerSlots(1L<<40,1L<<40);}catch(OverflowException){overflow=true;}Check(overflow,"corner multiplication did not check overflow");
  });
  Run("T3a representative grid headroom and bounded mask allocations",()=>{
   foreach(var dims in new[]{new[]{87,54,286140},new[]{130,73,575520},new[]{260,146,2289810}}){byte[] b=SpatialFixture(30,dims[0],dims[1],32);FormatUInt(b,24,32);FormatUInt(b,28,32);
    // Tiny JPEG headers are changed to 32x32; metadata-only validation deliberately does not decode entropy data.
    FormatJpegSize(b,true,32,32);FormatJpegSize(b,false,32,32);string why;SpatialFormat.Summary s;long allocation=0;
    using(var stream=new FormatStream(b))Check(SpatialFormat.Validate(stream,out s,out why,n=>{Check(n<=SpatialFormat.MaxValidationBytes,"allocation budget exceeded");allocation=n;}),why);
    Check(s.DenseSlots==dims[2]&&s.ValidationBytes==allocation&&allocation<11000,"representative scene exceeded working budget");
   }
   var near=SpatialFixture(1,1447,1447,8);string reason;SpatialFormat.Summary summary;using(var stream=new FormatStream(near))Check(SpatialFormat.Validate(stream,out summary,out reason),reason);Check(summary.DenseSlots==4190513&&summary.ValidationBytes==523887&&summary.ValidationBytes<SpatialFormat.MaxValidationBytes,"large mask budget");
  });
  Run("T3a mask counts padding and all four block corners",()=>{
   var extra=SpatialFixture(1,2,1);extra[76]|=4;FormatBad(extra,"mask popcount"); // Extra unreferenced corner: skipping popcount would accept it.
   foreach(int at in new[]{68,72}){var b=SpatialFixture();FormatUInt(b,at,100);FormatBad(b,"slice counts exceed mask size");}
   foreach(byte mask in new byte[]{7,0}){var b=SpatialFixture();b[76]=mask;FormatBad(b,"mask popcount");}
   var bm=SpatialFixture();bm[93]=0;FormatBad(bm,"mask popcount");
   foreach(int at in new[]{76,93}){var b=SpatialFixture();b[at]|=128;FormatBad(b,"mask padding");}
   // Counts and lengths agree, but the block references the absent fourth corner.
   var missing=SpatialFixture().Where((b,i)=>i<89||i>=93).ToArray();FormatUInt(missing,40,3);FormatUInt(missing,68,3);FormatUInt(missing,48,24);missing[76]=7;FormatBad(missing,"missing depth corners");
   var sums=SpatialFixture(2);FormatUInt(sums,40,7);FormatBad(sums,"slice sums exceed");
   sums=SpatialFixture(1,2,1);FormatUInt(sums,40,5);FormatBad(sums,"slice sums differ");
   sums=SpatialFixture(1,2,1);FormatUInt(sums,44,2);FormatBad(sums,"slice sums differ");
   foreach(int at in new[]{94,95}){var b=SpatialFixture();b[at]=1;FormatBad(b,"atlas coordinate");}
  });
  Run("T3a finite depths focal padding and derived projections",()=>{
   foreach(int at in new[]{20,77,81,85,89})foreach(float value in new[]{0f,-1f,float.NaN,float.PositiveInfinity,float.NegativeInfinity}){var b=SpatialFixture();FormatFloat(b,at,value);FormatBad(b,at==20?"focal must":"depth must");}
   foreach(float value in new[]{-1f,8.01f,float.NaN,float.PositiveInfinity,float.NegativeInfinity,float.MaxValue}){var b=SpatialFixture();FormatFloat(b,36,value);FormatBad(b,"uvPadding");}
   foreach(float value in new[]{0f,8f}){var b=SpatialFixture();FormatFloat(b,36,value);string why;using(var stream=new FormatStream(b))Check(SpatialFormat.Validate(stream,out why),why);}
   var extreme=SpatialFixture();FormatFloat(extreme,20,float.Epsilon);FormatBad(extreme,"mesh projection");
   extreme=SpatialFixture();FormatFloat(extreme,77,float.MaxValue);FormatBad(extreme,"mesh projection");
   extreme=SpatialFixture();for(int at=77;at<=89;at+=4)FormatFloat(extreme,at,1e20f);FormatBad(extreme,"camera projection");
   extreme=SpatialFixture();FormatUInt(extreme,60,1);FormatUInt(extreme,64,1);FormatFloat(extreme,20,float.MaxValue);FormatBad(extreme,"camera projection");
  });
  Run("T3a exact geometry and section boundaries",()=>{
   var b=SpatialFixture();var extra=b.Take(96).Concat(new byte[]{0}).Concat(b.Skip(96)).ToArray();FormatUInt(extra,48,29);FormatBad(extra,"geometry end differs");
   var shortGeometry=b.Where((v,i)=>i!=95).ToArray();FormatUInt(shortGeometry,48,27);FormatBad(shortGeometry,"truncated section");
   foreach(uint n in new[]{0u,uint.MaxValue}){var geom=SpatialFixture();FormatUInt(geom,48,n);FormatBad(geom,n==0?"trailing bytes":"truncated geometry");}
   foreach(int at in new[]{52,56}){var empty=SpatialFixture();FormatUInt(empty,at,0);FormatBad(empty,"empty image section");empty=SpatialFixture();FormatUInt(empty,at,uint.MaxValue);FormatBad(empty,"truncated image sections");}
   FormatBad(b.Concat(new byte[]{0}).ToArray(),"trailing bytes");
   int alpha=96+formatJpeg.Length;foreach(int cut in new[]{0,1,4,20,48,60,67,68,72,76,77,81,85,89,93,94,95,96,97,98,alpha-1,alpha,alpha+1,b.Length-1})FormatBad(b.Take(cut).ToArray(),"truncated");
  });
  Run("T3a JPEG SOI SOFn dimensions and segment limits",()=>{
   foreach(bool color in new[]{true,false}){
    string name=color?"color":"alpha";var b=SpatialFixture();int offset=color?96:96+formatJpeg.Length;b[offset]=0;FormatBad(b,name+" JPEG SOI");
    b=SpatialFixture();FormatJpegSize(b,color,17,16);FormatBad(b,name+" JPEG dimensions");b=SpatialFixture();FormatJpegSize(b,color,16,17);FormatBad(b,name+" JPEG dimensions");
    b=SpatialFixture();FormatJpegSize(b,color,16384,16384);FormatBad(b,name+" JPEG pixel budget");
    b=SpatialFixture();b[offset+4]=255;b[offset+5]=255;FormatBad(b,name+" JPEG segment truncated");
    b=SpatialFixture();b[offset+4]=0;b[offset+5]=1;FormatBad(b,name+" JPEG segment length");
    b=SpatialFixture();b[offset+3]=218;FormatBad(b,name+" JPEG SOF missing");
    b=SpatialFixture();int sof=FormatSof(b,offset);b[sof+1]=196;FormatBad(b,name+" JPEG SOF missing");
    b=SpatialFixture();b[sof+3]=7;FormatBad(b,name+" JPEG SOF truncated");
    b=SpatialFixture();b[sof+9]=0;FormatBad(b,name+" JPEG SOF layout");
    b=SpatialFixture();b[sof+1]=194;string why;using(var stream=new FormatStream(b))Check(SpatialFormat.Validate(stream,out why),"SOF2 header rejected: "+why);
   }
  });
  Run("T3a cap before hash and parsing (counting stream and sparse file)",()=>{
   string why;SpatialFormat.Summary summary;using(var stream=new FormatStream(SpatialFixture(),SpatialFormat.MaxFileBytes+1)){stream.ThrowOnRead=true;
    Check(!SpatialFormat.Validate(stream,out summary,out why)&&why.Contains("1 GiB cap")&&stream.ReadBytes==0,"validator read oversized input");
    Check(!SpatialVerification.CheckArtifact(stream,stream.Length,new string('0',64),out summary,out why)&&why.Contains("1 GiB cap")&&stream.ReadBytes==0,"verification hashed oversized input");
   }
   string root=Fresh("format-cap"),photo=Path.Combine(root,"photo.bmp"),store=Path.Combine(root,"store");Bmp(photo,Color.Red);string artifact=CatalogPair(store,photo);byte[] original=File.ReadAllBytes(artifact);long size=SpatialFormat.MaxFileBytes+1;
   using(var file=new FileStream(artifact,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){uint returned;Check(DeviceIoControl(file.SafeFileHandle,0x900c4,IntPtr.Zero,0,IntPtr.Zero,0,out returned,IntPtr.Zero),"FSCTL_SET_SPARSE failed");file.SetLength(size);}
   FormatSidecar(artifact,original,size);SpatialIdentity.Pair pair;Check(SpatialIdentity.Classify(artifact+".json",false,out pair)==SpatialIdentity.Classification.Valid,"sparse sidecar fixture invalid");
   using(var verified=SpatialVerification.Check(1,new SlideSnapshot(photo,File.ReadAllBytes(photo)),pair))Check(verified.Pair==null&&verified.ArtifactInvalid&&verified.ArtifactReason.Contains("1 GiB cap")&&!verified.HasHandle,"cap did not precede hash in Check");
   using(var stream=new FormatStream(SpatialFixture(),SpatialFormat.MaxFileBytes)){Check(SpatialFormat.CheckLength(stream,out why),"exact cap rejected");}
  });
  Run("T3a held-handle hash then format and failure reason",()=>{
   string root=Fresh("format-check"),photo=Path.Combine(root,"photo.bmp"),store=Path.Combine(root,"store");Bmp(photo,Color.Red);string artifact=CatalogPair(store,photo);SpatialIdentity.Pair pair;SpatialIdentity.Classify(artifact+".json",false,out pair);
   using(var verified=SpatialVerification.Check(1,new SlideSnapshot(photo,File.ReadAllBytes(photo)),pair))Check(verified.Pair!=null&&verified.Format!=null&&verified.HasHandle&&!Writable(artifact),"valid format did not retain lease");
   var b=SpatialFixture();b[76]=7;File.WriteAllBytes(artifact,b);
   using(var verified=SpatialVerification.Check(2,new SlideSnapshot(photo,File.ReadAllBytes(photo)),pair))Check(verified.Pair==null&&verified.ArtifactReason=="artifact hash mismatch"&&!verified.HasHandle,"format checked before hash");
   FormatSidecar(artifact,b);using(var verified=SpatialVerification.Check(3,new SlideSnapshot(photo,File.ReadAllBytes(photo)),pair))Check(verified.Pair==null&&verified.ArtifactInvalid&&verified.ArtifactReason.Contains("mask popcount")&&!verified.HasHandle,"hash-valid bad geometry attached");Check(Writable(artifact),"failed format retained handle");
  });
  Run("T3a 4000 seeded flips/truncations stay within allocation and read quotas",()=>{
   byte[] good=SpatialFixture(2);var random=new Random(730031);long peak=0;int rejected=0;
   for(int i=0;i<4000;i++){var b=(byte[])good.Clone();for(int flip=0,n=random.Next(1,9);flip<n;flip++){int p=random.Next(b.Length);b[p]^=(byte)(1<<random.Next(8));}if(i%3==0)Array.Resize(ref b,random.Next(b.Length+1));
    using(var stream=new FormatStream(b)){string why;SpatialFormat.Summary summary;bool ok=SpatialFormat.Validate(stream,out summary,out why,n=>{Check(n<=SpatialFormat.MaxValidationBytes,"allocation quota exceeded");peak=Math.Max(peak,n);});
     Check(ok?summary!=null&&why==null:summary==null&&!string.IsNullOrEmpty(why),"missing validation result");if(!ok)rejected++;Check(stream.MaxRead<=SpatialFormat.MaxValidationBytes,"unbounded read buffer");}
   }Check(rejected>2000,"fuzz corpus did not exercise failures");Console.WriteLine("INFO  fuzz seed 730031: 4000 cases, "+rejected+" rejected, peak reserved bytes "+peak);
  });
 }
 static int FormatSof(byte[] b,int start){int p=start+2;while(p<b.Length){Check(b[p]==255,"bad test JPEG");int m=b[p+1];if(m>=192&&m<=207&&m!=196&&m!=200&&m!=204)return p;p+=2+(b[p+2]<<8)+b[p+3];}throw new Exception("fixture has no SOF");}
 static void FormatJpegSize(byte[] b,bool color,int width,int height){int start=68+(int)BitConverter.ToUInt32(b,48)+(color?0:(int)BitConverter.ToUInt32(b,52)),p=FormatSof(b,start);b[p+5]=(byte)(height>>8);b[p+6]=(byte)height;b[p+7]=(byte)(width>>8);b[p+8]=(byte)width;}
 static List<byte[]> nativeCorpus;
 [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)] static extern IntPtr LoadLibraryW(string path);
 [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
 static string NativeDll{get{return Environment.GetEnvironmentVariable("MATTE_TEST_NATIVE_DLL")??Path.GetFullPath(Path.Combine(Here,"..","..","build","SpatialNative.dll"));}}
 static void LoadNative(){Check(LoadLibraryW(NativeDll)!=IntPtr.Zero,"cannot load "+NativeDll+": "+System.Runtime.InteropServices.Marshal.GetLastWin32Error());}
 static int NativeChild(string[] args){
  SetErrorMode(1|2|0x8000); // A deliberately crashing mutant must exit, never open Windows Error Reporting UI.
  try{LoadNative();string path=args[1];var error=new System.Text.StringBuilder(512);
   if(args[0]=="--native-fault"){
    int fault=int.Parse(args[2]),expected=int.Parse(args[3]),expectedReason=int.Parse(args[4]),eventKind=0,eventSequence=0;NativeSpatial.FailureReason reason;
    NativeSpatial.EventProc callback=(kind,seq)=>{eventKind=kind;eventSequence=seq;};
    int count=fault==2?33:1;int before=0;
    for(int i=0;i<count;i++){
     int kind=NativeSpatial.SnTestLoader(path,fault,callback,out reason,error,error.Capacity);
     Check(kind==expected&&(int)reason==expectedReason,"fault "+fault+": kind="+kind+", reason="+reason+", "+error);
     Check(eventKind==expected&&eventSequence==1,"classified event was not emitted for current sequence");
     if(fault==2){using(var file=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){}using(var process=System.Diagnostics.Process.GetCurrentProcess()){if(i==0)before=process.HandleCount;else if(i==count-1)Check(process.HandleCount<=before+2,"file/worker handles leaked after exception");}}
    }GC.KeepAlive(callback);Console.WriteLine("FAULT OK "+fault);return 0;
   }
   string why;bool managed;using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))managed=SpatialFormat.Validate(file,out why);
   int structure=NativeSpatial.SnValidateStructure(path,error,error.Capacity);Check(structure==(managed?0:NativeSpatial.LoadError),"structural parity: managed="+managed+" ("+why+"), native="+structure+" ("+error+")");
   int decoded=NativeSpatial.SnValidate(path,error,error.Capacity);
   Check(decoded==0||decoded==NativeSpatial.LoadError,"unexpected native runtime error: "+error);Check(managed||decoded==NativeSpatial.LoadError,"native accepted managed-invalid input");
   Console.WriteLine("RESULT "+structure+" "+decoded+" "+error);return 0;
  }catch(Exception e){Console.WriteLine("CHILD FAIL "+e.Message);return 1;}
 }
 static string NativeProcess(string arguments,int timeout=15000){
  var start=new System.Diagnostics.ProcessStartInfo(Assembly.GetExecutingAssembly().Location,arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};start.EnvironmentVariables["MATTE_TEST_NATIVE_DLL"]=NativeDll;
  using(var child=System.Diagnostics.Process.Start(start)){
   var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();
   if(!child.WaitForExit(timeout)){child.Kill();child.WaitForExit(5000);throw new Exception("native child timed out: "+arguments);}
   string text=output.Result+error.Result;Check(child.ExitCode==0,"native child exit "+child.ExitCode+": "+arguments+"\n"+text);return text;
  }
 }
 static void NativeTests(){
  bool available=File.Exists(NativeDll),required=Environment.GetEnvironmentVariable("MATTE_REQUIRE_NATIVE")=="1";
  nativeCorpus=available?new List<byte[]>():null;try{FormatTests();}finally{if(!available)nativeCorpus=null;}
  if(!available){if(required)Run("T3b required native DLL",()=>Check(false,"required DLL missing: "+NativeDll));else Console.WriteLine("SKIP T3b native validation: "+NativeDll+" missing");return;}
  var corpus=nativeCorpus;nativeCorpus=null;
  Run("T3b all native limits equal managed constants",()=>{
   LoadNative();long[] expected={SpatialFormat.MaxFileBytes,SpatialFormat.MaxDenseSlots,SpatialFormat.MaxBlocks,SpatialFormat.MaxVertices,SpatialFormat.MaxIndices,SpatialFormat.MaxMeshBytes,SpatialFormat.MaxAtlasPixels,SpatialFormat.MaxValidationBytes,SpatialFormat.MaxSlices,SpatialFormat.MaxBlockSize,SpatialFormat.MaxDimension,SpatialFormat.HeaderBytes};
   var actual=new long[expected.Length];Check(NativeSpatial.SnLimits(actual,actual.Length)==expected.Length&&actual.SequenceEqual(expected),"native/managed limits differ");
  });
  string root=Fresh("native-corpus");
  Run("T3b T3a corpus and all 4000 seeded cases in timed child processes",()=>{
   Check(corpus.Count>=4000,"T3a corpus was not captured");var problems=new System.Collections.Concurrent.ConcurrentQueue<string>();int done=0,decodedRejects=0;
   System.Threading.Tasks.Parallel.For(0,corpus.Count,new System.Threading.Tasks.ParallelOptions{MaxDegreeOfParallelism=4},i=>{
    string path=Path.Combine(root,i.ToString("D5")+".spatial");File.WriteAllBytes(path,corpus[i]);
    try{string result=NativeProcess("--native-validate \""+path+"\"");if(result.StartsWith("RESULT 0 3 "))System.Threading.Interlocked.Increment(ref decodedRejects);}catch(Exception e){problems.Enqueue(e.Message);}
    int n=System.Threading.Interlocked.Increment(ref done);if(n%250==0)Console.WriteLine("INFO  native children "+n+"/"+corpus.Count);
   });
   Console.WriteLine("INFO  native corpus "+corpus.Count+", WIC-only content rejections "+decodedRejects);Check(problems.IsEmpty,string.Join("\n",problems.Take(12)));
  });
  Run("T3b actual JPEG decode succeeds and damaged payload is content",()=>{
   string valid=Path.Combine(root,"valid.spatial");File.WriteAllBytes(valid,SpatialFixture());Check(NativeProcess("--native-validate \""+valid+"\"").StartsWith("RESULT 0 0 "),"valid JPEG did not decode");
   var b=SpatialFixture();int sof=FormatSof(b,96),end=sof+2+(b[sof+2]<<8)+b[sof+3];
   // Keep SOI/SOF and all section sizes valid but remove the scan/tables after SOF.
   for(int i=end;i<96+formatJpeg.Length;i++)b[i]=0;
   string damaged=Path.Combine(root,"bad-payload.spatial");File.WriteAllBytes(damaged,b);Check(NativeProcess("--native-validate \""+damaged+"\"").StartsWith("RESULT 0 3 "),"undecodable JPEG was not a content failure");
   string cap=Directory.GetFiles(Path.Combine(Here,"t-format-cap","store"),"*.spatial",SearchOption.AllDirectories).Single();Check(NativeProcess("--native-validate \""+cap+"\"").StartsWith("RESULT 3 3 "),"oversized sparse file not rejected");
  });
  Run("T3b LoaderRun classifies exceptions, releases handles, checks WIC sizes",()=>{
   string valid=Path.Combine(root,"fault-valid.spatial"),hostile=Path.Combine(root,"fault-hostile.spatial");File.WriteAllBytes(valid,SpatialFixture());var b=SpatialFixture();FormatUInt(b,12,uint.MaxValue);File.WriteAllBytes(hostile,b);
   foreach(var test in new[]{new[]{1,5,103},new[]{2,5,103},new[]{3,3,1},new[]{4,5,104},new[]{5,5,107},new[]{6,5,105},new[]{7,5,106},new[]{8,3,4},new[]{9,5,102}}){string path=test[0]==2||test[0]==3?hostile:valid;NativeProcess("--native-fault \""+path+"\" "+test[0]+" "+test[1]+" "+test[2]);}
   NativeProcess("--native-fault \""+Path.Combine(root,"missing.spatial")+"\" 0 5 101");
  });
 }
 static void AttachTests(){
  Run("T2c2 stale source stays flat and heals hint",()=>{
   string root=Fresh("a-stale"),photo=Path.Combine(root,"photo.bmp"),store=Path.Combine(root,"store");Bmp(photo,Color.Red);string artifact=CatalogPair(store,photo);
   using(var sink=new SlideSink()){var app=AttachApp(store,sink,photo);var old=app.TestCatalogResult;var stamp=SpatialCatalog.Probe(photo);Bmp(photo,Color.Blue);File.SetLastWriteTimeUtc(photo,new DateTime(stamp.mtimeTicks,DateTimeKind.Utc));var now=SpatialCatalog.Probe(photo);
    try{Check(now.fileId==stamp.fileId&&now.size==stamp.size&&now.mtimeTicks==stamp.mtimeTicks,"stale fixture changed identity");app.TestShow(photo);Until(()=>sink.Events.Count>0,"no source result");
     Check(sink.Events.All(e=>e.StartsWith("flat:")),"mismatched source attached 3D");Check(old.Photos[photo].Status==SpatialCatalog.State.Unchecked&&old.Photos[photo].Needs,"mismatched source hint was not dropped");Check(File.Exists(artifact),"healing deleted artifact");
     Check(File.ReadAllBytes(sink.Image.Display).SequenceEqual(File.ReadAllBytes(photo)),"flat display differs from captured bytes");
    }finally{app.TestClose();}}
  });
  Run("T2c2 unpaired stale hint also invalidated",()=>{
   var image=new SlideSnapshot("photo.bmp",new byte[]{1,2,3});using(var result=SpatialVerification.Check(1,image,null,new string('0',64)))Check(result.SourceMismatch&&result.Pair==null,"unpaired stale hint was kept");
  });
  Run("T2c2 held artifact blocks writes and deletes through transition",()=>{
   string root=Fresh("a-lease"),photo=Path.Combine(root,"a.bmp"),other=Path.Combine(root,"b.bmp"),store=Path.Combine(root,"store");Bmp(photo,Color.Red);Bmp(other,Color.Blue);string artifact=CatalogPair(store,photo),second=CatalogPair(store,other);
   var c=SpatialCatalog.Load(store,new[]{photo,other},null).Complete(()=>false);using(var leases=new SpatialLeases()){
    using(var first=SpatialVerification.Check(1,new SlideSnapshot(photo,File.ReadAllBytes(photo)),c.Photos[photo].Pair)){Check(first.Pair!=null,"valid pair rejected");leases.Add(1,first.TakeHandle());}
    Check(!Writable(artifact),"shown artifact allowed a writer");bool deleted=false;try{File.Delete(artifact);deleted=true;}catch(IOException){}Check(!deleted,"shown artifact allowed deletion");
    using(var native=CreateFileW(artifact,0x80000000,1|4,IntPtr.Zero,3,0,IntPtr.Zero))Check(!native.IsInvalid,"native read open incompatible with held lease");
    using(var next=SpatialVerification.Check(2,new SlideSnapshot(other,File.ReadAllBytes(other)),c.Photos[other].Pair))leases.Add(2,next.TakeHandle());leases.NativeEvent(NativeSpatial.FirstScene,2);Check(!Writable(artifact),"old scene lease released during transition");
    leases.NativeEvent(NativeSpatial.TransitionDone,2);Check(Writable(artifact),"old scene lease survived transition");File.Delete(artifact);Check(!Writable(second),"current scene lease released too soon");
   }Check(Writable(second),"shutdown kept current lease");File.Delete(second);
  });
  foreach(string change in new[]{"slide","off","power","fullscreen"})Run("T2c2 late result rejected after "+change,()=>{
   string root=Fresh("a-late-"+change),photo=Path.Combine(root,"a.bmp"),other=Path.Combine(root,"b.bmp"),store=Path.Combine(root,"store");Bmp(photo,Color.Red);Bmp(other,Color.Blue);string artifact=CatalogPair(store,photo);
   using(var entered=new System.Threading.ManualResetEventSlim())using(var release=new System.Threading.ManualResetEventSlim())using(var sink=new SlideSink()){
    var app=AttachApp(store,sink,photo,other);app.beforeVerifyDelivery=v=>{if(v.Image.Source==photo&&v.Pair!=null){entered.Set();release.Wait(10000);}};
    try{app.TestShow(photo);Until(()=>entered.IsSet,"verification did not enter gate");Check(!Writable(artifact),"verification did not hold artifact");
     if(change=="slide")app.TestShow(other);else if(change=="off")app.TestSpatialOn(false);else if(change=="power")app.TestPowerFlat();else app.TestPaused();release.Set();
     Until(()=>Writable(artifact)||sink.Events.Contains("3d:"+photo),"late result leaked its artifact handle");Check(!sink.Events.Contains("3d:"+photo),"late result attached obsolete 3D");Until(()=>sink.Events.Count>0,"replacement did not display");Pump(100);
     Check(!sink.Events.Contains("3d:"+photo),"late result attached obsolete 3D");if(change=="slide")Check(sink.Image.Source==other,"late result replaced current photo");
    }finally{release.Set();app.TestClose();}}
  });
  Run("T2c2 two second flat fallback then current 3D",()=>{
   string root=Fresh("a-timeout"),photo=Path.Combine(root,"a.bmp"),store=Path.Combine(root,"store");Bmp(photo,Color.Red);CatalogPair(store,photo);
   using(var entered=new System.Threading.ManualResetEventSlim())using(var release=new System.Threading.ManualResetEventSlim())using(var sink=new SlideSink()){
    var app=AttachApp(store,sink,photo);app.beforeVerifyDelivery=v=>{entered.Set();release.Wait(10000);};try{app.TestShow(photo);Until(()=>entered.IsSet,"timeout gate not entered");Until(()=>sink.Events.Count>0,"no flat fallback",2800);Check(sink.Events[0]=="flat:"+photo,"did not show flat while verification pending");var countdown=app.TestCountdown;release.Set();Until(()=>sink.Events.Contains("3d:"+photo),"current verified result did not attach");Check(app.TestCountdown==countdown,"late attach reset timer");}finally{release.Set();app.TestClose();}}
  });
  Run("T2c2 WebP and ambient use buffer digest despite preserved mtime",()=>{
   // Independent real lossless WebP fixtures, made by Pillow: 16x12 solid red and blue, equal length.
   byte[] red=Convert.FromBase64String("UklGRhwAAABXRUJQVlA4TA8AAAAvD8ACAAcQ/Y/+ByKi/wEA"),blue=Convert.FromBase64String("UklGRhwAAABXRUJQVlA4TA8AAAAvD8ACAAcQ0f/+ByKi/wEA");
   Func<byte[],System.Windows.Media.Imaging.BitmapSource> decoder=null;
   try{new SlideSnapshot("probe.webp",red).Pixels();}catch(NotSupportedException){
    Console.WriteLine("INFO  WebP WPF codec unavailable; cache test uses a buffer decoder stand-in (real WebP fixture bytes)");
    decoder=bytes=>{Check(bytes.SequenceEqual(red)||bytes.SequenceEqual(blue),"decoder received different bytes");byte[] pixels=new byte[16*12*3];for(int i=0;i<pixels.Length;i+=3)pixels[i+(bytes.SequenceEqual(red)?2:0)]=255;var image=System.Windows.Media.Imaging.BitmapSource.Create(16,12,96,96,System.Windows.Media.PixelFormats.Bgr24,null,pixels,48);image.Freeze();return image;};
   }
   string root=Fresh("a-webp"),photo=Path.Combine(root,"photo.webp"),cache=Path.Combine(root,"cache");File.WriteAllBytes(photo,red);var time=File.GetLastWriteTimeUtc(photo);var first=new SlideSnapshot(photo,File.ReadAllBytes(photo),decoder);first.MakeDisplay(cache);
   var app=new App(new TestWall()){TestSettings=new MatteSettings()};try{string bg=app.TestAmbient(first);File.WriteAllBytes(photo,blue);File.SetLastWriteTimeUtc(photo,time);var next=new SlideSnapshot(photo,File.ReadAllBytes(photo),decoder);next.MakeDisplay(cache);string nextBg=app.TestAmbient(next);
    Check(first.Display!=next.Display&&!File.ReadAllBytes(first.Display).SequenceEqual(File.ReadAllBytes(next.Display)),"preserved-mtime WebP reused old display copy");Check(bg!=nextBg&&!File.ReadAllBytes(bg).SequenceEqual(File.ReadAllBytes(nextBg)),"preserved-mtime ambient reused old composite");
    File.WriteAllBytes(photo,red);using(var decoded=next.Decode())Check(decoded.GetPixel(0,0).B>200,"decoded source path instead of captured buffer");Check(File.ReadAllBytes(next.Display).Length>0,"display missing");first.Dispose();next.Dispose();
   }finally{app.TestClose();}
  });
  Run("T2c2 reload preserves 3D and attaches new pair without rotation",()=>{
   string root=Fresh("a-reload"),photo=Path.Combine(root,"a.bmp"),other=Path.Combine(root,"b.bmp"),store=Path.Combine(root,"store");Bmp(photo,Color.Red);Bmp(other,Color.Blue);string artifact=CatalogPair(store,photo);
   using(var sink=new SlideSink()){var app=AttachApp(store,sink,photo,other);try{app.TestShow(photo);Until(()=>sink.Events.Contains("3d:"+photo),"initial 3D missing");var countdown=app.TestCountdown;int rotation=app.TestRotation,count=sink.Events.Count;
     File.Delete(Path.Combine(store,"v1","index.json"));app.TestLoadCatalog();Check(app.TestSpatialPath(photo)==artifact&&app.TestSpatialSlides==1,"catalogue cleared before reload applied");Until(()=>!app.TestCatalogLoading,"reload unfinished");Check(sink.Events.Count==count&&app.TestCountdown==countdown&&app.TestRotation==rotation,"reload flickered or restarted rotation");
     app.TestShow(other);Until(()=>sink.Events.Last()=="flat:"+other,"unpaired photo not flat");countdown=app.TestCountdown;CatalogPair(store,other);app.TestLoadCatalog();Until(()=>sink.Events.Last()=="3d:"+other,"new pair did not attach on reload");Check(app.TestCountdown==countdown&&app.TestRotation==rotation,"new pair restarted rotation");
     File.Delete(SpatialIdentity.SidecarPath(store,app.TestSnapshot.Digest,SpatialIdentity.RecipeSha256(CatalogRecipe())));app.TestLoadCatalog();Until(()=>sink.Events.Last()=="flat:"+other,"lost pair did not drop 3D");
    }finally{app.TestClose();}}
  });
  foreach(bool sidecar in new[]{false,true})Run("T2c2 rejects and suppresses changed "+(sidecar?"sidecar":"artifact"),()=>{
   string root=Fresh("a-bad-"+sidecar),photo=Path.Combine(root,"a.bmp"),store=Path.Combine(root,"store");Bmp(photo,Color.Red);string artifact=CatalogPair(store,photo);
   using(var sink=new SlideSink()){var app=AttachApp(store,sink,photo);try{if(sidecar)File.WriteAllText(artifact+".json","{\"version\":2}");else{byte[] b=File.ReadAllBytes(artifact);b[0]^=1;File.WriteAllBytes(artifact,b);}app.TestShow(photo);Until(()=>sink.Events.Count>0,"bad pair result missing");Check(sink.Events.All(e=>e.StartsWith("flat:")),"changed pair attached");Until(()=>!app.TestCatalogLoading,"bad pair reload unfinished");Check(app.TestSpatialPath(photo)==null&&File.Exists(artifact),"bad pair still offered or deleted");CatalogPair(store,photo);app.TestLoadCatalog();Until(()=>sink.Events.Last()=="3d:"+photo,"regenerated pair remains suppressed");}finally{app.TestClose();}}
  });
  Run("T2c2 off skips 3D verification and next slide is prepared",()=>{
   string root=Fresh("a-ahead"),photo=Path.Combine(root,"a.bmp"),store=Path.Combine(root,"store");Bmp(photo,Color.Red);string artifact=CatalogPair(store,photo);
   using(var entered=new System.Threading.ManualResetEventSlim())using(var sink=new SlideSink()){var app=AttachApp(store,sink,photo);SpatialVerification prepared=null;app.beforeVerifyDelivery=v=>{prepared=v;entered.Set();};try{app.TestSpatialOn(false);app.TestAhead(photo);Until(()=>entered.IsSet,"next slide not prepared");Check(prepared.Candidate==null&&prepared.Pair==null&&!prepared.HasHandle&&Writable(artifact),"Spatial off started 3D verification");app.TestShow(photo);Until(()=>sink.Events.Count>0,"prepared next slide not shown");Check(sink.Events[0]=="flat:"+photo,"off attached 3D");}finally{app.TestClose();}}
  });
 }
 static void CatalogTests(){
  Run("T2c1 same stems and legacy flat",()=>{
   string root=Fresh("c-stems"),store=Path.Combine(root,"store"),a=Path.Combine(root,"a","same.jpg"),b=Path.Combine(root,"b","same.jpg"),legacy=Path.Combine(root,"old.jpg");
   StoreFile(root,@"a\same.jpg",new byte[]{1,2});StoreFile(root,@"b\same.jpg",new byte[]{3,4});File.WriteAllBytes(legacy,new byte[]{5,6});
   string pa=CatalogPair(store,a),pb=CatalogPair(store,b),desired=SpatialIdentity.RecipeSha256(CatalogRecipe());File.WriteAllText(Path.Combine(store,"old.spatial"),"legacy");File.WriteAllText(Path.Combine(root,"old.spatial"),"legacy next to photo");
   var first=SpatialCatalog.Load(store,new[]{a,b,legacy},desired);Check(first.Photos.Values.All(p=>p.Status==SpatialCatalog.State.Unchecked),"hashed on initial hint load");
   var catalog=first.Complete(()=>false);var app=new App(new TestWall()){TestSettings=new MatteSettings{Playlist=new List<string>{a,b,legacy},SpatialFolder=store}};
   try{app.TestCatalog(catalog,a,b,legacy);Check(app.TestSpatialPath(a)==pa&&app.TestSpatialPath(b)==pb,"same stems attached to the wrong pair");
    Check(app.TestSpatialPath(legacy)==null&&app.TestSpatialSlides==2,"legacy stem fallback catalogued an unverified picture");Check(app.TestNeeds()[1]==1,"legacy photo not counted as needing generation");
    Check(catalog.Legacy==1&&catalog.StoreText().Contains("旧格式 · 需要重新生成才会显示立体效果"),"legacy count wording");Check(File.Exists(Path.Combine(store,"old.spatial")),"legacy picture mutated");
   }finally{app.TestClose();}
  });
  Run("T2c1 rename and copies reuse content",()=>{
   string root=Fresh("c-renamed"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"photo.jpg"),renamed=Path.Combine(root,"renamed.png"),copy=Path.Combine(root,"copy.jpg");File.WriteAllBytes(photo,new byte[]{1,2,3});
   string pair=CatalogPair(store,photo),desired=SpatialIdentity.RecipeSha256(CatalogRecipe());SpatialCatalog.Load(store,new[]{photo},desired).Complete(()=>false);
   File.Move(photo,renamed);File.Copy(renamed,copy);var first=SpatialCatalog.Load(store,new[]{renamed,copy},desired);Check(first.Photos.Values.All(p=>p.Status==SpatialCatalog.State.Unchecked),"new paths skipped checking");
   var c=first.Complete(()=>false);Check(c.Photos.Values.All(p=>p.Pair.Artifact==pair&&!p.Needs),"rename/copy requires generation");Check(c.Valid==1&&File.Exists(pair),"renamed the artifact");
  });
  Run("T2c1 file id and volume invalidate hints",()=>{
   string root=Fresh("c-stamp"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"photo.jpg");File.WriteAllBytes(photo,new byte[]{1,2,3});CatalogPair(store,photo);string desired=SpatialIdentity.RecipeSha256(CatalogRecipe());
   SpatialCatalog.Load(store,new[]{photo},desired).Complete(()=>false);
   foreach(string part in new[]{"fileId","volume","unavailable"}){
    var c=SpatialCatalog.Load(store,new[]{photo},desired,path=>{var s=SpatialCatalog.Probe(path);if(part=="fileId")s.fileId=s.fileId==new string('0',16)?new string('1',16):new string('0',16);else if(part=="volume")s.volumeSerial=s.volumeSerial=="00000000"?"11111111":"00000000";else return null;return s;});
    Check(c.Photos[photo].Status==SpatialCatalog.State.Unchecked&&c.Photos[photo].Pair==null&&c.Photos[photo].Needs,"used hint without matching "+part);
   }
   var old=SpatialCatalog.Probe(photo);string replacement=Path.Combine(root,"replacement");File.WriteAllBytes(replacement,new byte[]{7,8,9});File.SetLastWriteTimeUtc(replacement,new DateTime(old.mtimeTicks,DateTimeKind.Utc));File.Delete(photo);File.Move(replacement,photo);
   Check(SpatialCatalog.Probe(photo).fileId!=old.fileId,"fixture did not change file identity");var changed=SpatialCatalog.Load(store,new[]{photo},desired);Check(changed.Photos[photo].Pair==null,"replacement used old pair");
   string pair=CatalogPair(store,photo);changed=changed.Complete(()=>false);Check(changed.Photos[photo].Pair.Artifact==pair&&!changed.Photos[photo].Needs,"replacement not catalogued after hash");
  });
  Run("T2c1 stale hints remain probable only",()=>{
   string root=Fresh("c-probable"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"photo.jpg");File.WriteAllBytes(photo,new byte[]{1,2,3});string pair=CatalogPair(store,photo),desired=SpatialIdentity.RecipeSha256(CatalogRecipe());
   SpatialCatalog.Load(store,new[]{photo},desired).Complete(()=>false);var stamp=SpatialCatalog.Probe(photo);File.WriteAllBytes(photo,new byte[]{4,5,6});File.SetLastWriteTimeUtc(photo,new DateTime(stamp.mtimeTicks,DateTimeKind.Utc));
   var now=SpatialCatalog.Probe(photo);Check(now.fileId==stamp.fileId&&now.size==stamp.size&&now.mtimeTicks==stamp.mtimeTicks,"stale hint fixture");
   byte[] bytes=File.ReadAllBytes(pair);bytes[0]^=1;File.WriteAllBytes(pair,bytes); // Full artifact verification would reject it; T2c1 must not claim that verification.
   var c=SpatialCatalog.Load(store,new[]{photo},desired);Check(c.Photos[photo].Status==SpatialCatalog.State.Hinted&&c.Photos[photo].Pair.Artifact==pair,"probable hint unexpectedly treated as verified or discarded");
   Check(c.Complete(()=>false).Photos[photo].Status==SpatialCatalog.State.Hinted,"probable pair promoted to verified");
  });
  Run("T2c1 index missing corrupt and atomic rebuild",()=>{
   string root=Fresh("c-index"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"photo.jpg");File.WriteAllBytes(photo,new byte[]{1});CatalogPair(store,photo);string desired=SpatialIdentity.RecipeSha256(CatalogRecipe()),index=Path.Combine(store,"v1","index.json");
   foreach(bool corrupt in new[]{false,true}){if(corrupt)File.WriteAllText(index,"broken");else if(File.Exists(index))File.Delete(index);
    var c=SpatialCatalog.Load(store,new[]{photo},desired);Check(c.Photos[photo].Status==SpatialCatalog.State.Unchecked,"bad index accepted");c=c.Complete(()=>false);
    Check(!c.Photos[photo].Needs&&c.Photos[photo].Status==SpatialCatalog.State.Hinted,"index not rebuilt");var data=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(index));Check(data!=null,"invalid index JSON");
    Check(!Directory.GetFiles(Path.Combine(store,"v1"),"index.json.tmp-*").Any(),"index temporary leaked");}
   File.WriteAllText(index,"broken");using(var held=new FileStream(index,FileMode.Open,FileAccess.Read,FileShare.Read)){
    var c=SpatialCatalog.Load(store,new[]{photo},desired).Complete(()=>false);Check(!c.Photos[photo].Needs&&c.Photos[photo].Pair!=null,"failed index write discarded in-memory hash");Check(File.ReadAllText(index)=="broken","replaced a locked index");}
  });
  Run("T2c1 inventory ignores quarantine and stray directories",()=>{
   string root=Fresh("c-inventory"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"photo.jpg");File.WriteAllBytes(photo,new byte[]{1});string pair=CatalogPair(store,photo),desired=SpatialIdentity.RecipeSha256(CatalogRecipe());
   foreach(string dir in new[]{"quarantine","stray",new string('A',64)}){string path=Path.Combine(store,"v1",dir);Directory.CreateDirectory(path);File.Copy(pair+".json",Path.Combine(path,Path.GetFileName(pair)+".json"));}
   string ignored=Path.Combine(Path.GetDirectoryName(pair),"stray.spatial.json");File.WriteAllText(ignored,"bad");
   var c=SpatialCatalog.Load(store,new[]{photo},desired).Complete(()=>false);Check(c.Valid==1&&c.Invalid==0&&c.Unreadable==0,"enumerated quarantine or stray directory");
   File.WriteAllText(pair+".json","{\"version\":2}");c=SpatialCatalog.Load(store,new[]{photo},desired);Check(c.Unsupported==1&&c.Photos[photo].Pair==null&&c.Photos[photo].Needs,"unsupported pair catalogued");
   File.WriteAllText(pair+".json","bad");c=SpatialCatalog.Load(store,new[]{photo},desired);Check(c.Invalid==1,"invalid pair not counted");
   using(var locked=new FileStream(pair+".json",FileMode.Open,FileAccess.Read,FileShare.None)){c=SpatialCatalog.Load(store,new[]{photo},desired);Check(c.Unreadable==1,"unreadable pair not counted");}
  });
  Run("T2c1 unreadable sources are errors",()=>{
   string root=Fresh("c-errors"),photo=Path.Combine(root,"photo.jpg"),missing=Path.Combine(root,"missing.jpg");File.WriteAllBytes(photo,new byte[]{1});
   using(var held=new FileStream(photo,FileMode.Open,FileAccess.Read,FileShare.None)){var c=SpatialCatalog.Load(Path.Combine(root,"store"),new[]{photo,missing},null);Check(c.Photos.Values.All(p=>p.Status==SpatialCatalog.State.Error&&p.Needs&&p.Pair==null),"unreadable source counted as done");}
  });
  Run("T2c1 desired recipe and estimates do not gate generation",()=>{
   string root=Fresh("c-buttons"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"photo.jpg"),generator=Path.Combine(root,"custom.ps1");File.WriteAllBytes(photo,new byte[]{1});File.WriteAllText(generator,"");string pair=CatalogPair(store,photo),desired=SpatialIdentity.RecipeSha256(CatalogRecipe());
   var ready=SpatialCatalog.Load(store,new[]{photo},desired).Complete(()=>false);var app=new App(new TestWall()){TestSettings=new MatteSettings{Playlist=new List<string>{photo},SpatialFolder=store,SpatialGenerator=generator}};
   try{foreach(string want in new[]{desired,null,SpatialIdentity.RecipeSha256(CatalogRecipe(1280))}){
     var c=SpatialCatalog.Load(store,new[]{photo},want);app.TestCatalog(c,photo);Check(app.TestGenerateButtons().All(x=>x),"zero or unknown estimate disabled generation");
     Check(app.TestSpatialPath(photo)==pair,"compatible recipe not selected");Check(app.TestNeeds()[1]==(want==desired?0:1),"needs ignored desired recipe");
     if(want==null)Check(app.TestPanelGenText().Contains("配方未知"),"unknown estimate not shown");
    }
    app.TestSettings.SpatialGenerator=null;app.TestCatalog(SpatialCatalog.Load(store,new[]{photo},null),photo);Check(app.TestGenerateButtons().All(x=>x),"missing SHARP disabled unknown estimate action");
   }finally{app.TestClose();}
  });
  Run("T2c1 recipe resolution caches checkpoint and screen hints",()=>{
   string root=Fresh("c-recipe"),checkpoint=Path.Combine(root,"model");File.WriteAllBytes(checkpoint,new byte[]{1});var fields=CatalogRecipe();string sha=SpatialIdentity.RecipeSha256(fields),text="Recipe: "+SpatialIdentity.Canonical(fields)+"\nRecipeSha: "+sha+"\n";int calls=0;var cache=new SpatialCatalog.RecipeCache();Func<string> describe=()=>{calls++;return text;};
   Check(cache.Resolve(checkpoint,1920,1080,false,describe)==null&&calls==0,"described missing SHARP");Check(cache.Resolve(checkpoint,1920,1080,true,describe)==sha,"recipe not resolved");cache.Resolve(checkpoint,1920,1080,true,describe);Check(calls==1,"unchanged recipe not cached");
   cache.Resolve(checkpoint,2560,1600,true,describe);Check(calls==2,"screen not in cache key");File.SetLastWriteTimeUtc(checkpoint,DateTime.UtcNow.AddMinutes(1));cache.Resolve(checkpoint,2560,1600,true,describe);Check(calls==3,"mtime not in cache key");File.WriteAllBytes(checkpoint,new byte[]{1,2});cache.Resolve(checkpoint,2560,1600,true,describe);Check(calls==4,"size not in cache key");
   Check(SpatialCatalog.ParseRecipe(text.Replace(sha,new string('0',64)))==null&&SpatialCatalog.ParseRecipe(text+text)==null,"invalid desired recipe accepted");
   Check(new SpatialCatalog.RecipeCache().Resolve(checkpoint,1,1,true,()=>{throw new IOException();})==null,"failed describe not unknown");
   string script=Path.Combine(root,"describe.ps1");File.WriteAllText(script,"param([switch]$Describe)\r\nif(!$Describe){exit 4}\r\nWrite-Output 'Recipe: "+SpatialIdentity.Canonical(fields)+"'\r\nWrite-Output 'RecipeSha: "+sha+"'\r\n");
   var task=System.Threading.Tasks.Task.Run(()=>SpatialCatalog.Describe(script));while(!task.IsCompleted)Pump(25);Check(SpatialCatalog.ParseRecipe(task.Result)==sha,"hidden Describe process output not parsed");
  });
  Run("T2c1 load hashes in background and applies catalogue",()=>{
   string root=Fresh("c-background"),store=Path.Combine(root,"store"),photo=Path.Combine(root,"photo.jpg");File.WriteAllBytes(photo,new byte[1024*1024]);string pair=CatalogPair(store,photo);var app=new App(new TestWall()){TestSettings=new MatteSettings{Playlist=new List<string>{photo},SpatialFolder=store}};
   try{app.TestLoadCatalog();Check(app.TestCatalogLoading&&app.TestSpatialPath(photo)==null,"load synchronously hashed the photo");var timer=System.Diagnostics.Stopwatch.StartNew();while(app.TestCatalogLoading&&timer.ElapsedMilliseconds<5000)Pump(20);
    Check(!app.TestCatalogLoading&&app.TestSpatialPath(photo)==pair,"background hash did not populate catalogue");Check(app.TestCatalogResult.Photos[photo].Status==SpatialCatalog.State.Hinted,"background result claimed verification");
   }finally{app.TestClose();}
  });
  Run("T2c1 late catalogue cannot replace a newer request",()=>{
   string root=Fresh("c-late"),store=Path.Combine(root,"store"),a=Path.Combine(root,"a.jpg"),b=Path.Combine(root,"b.jpg");File.WriteAllBytes(a,new byte[]{1});File.WriteAllBytes(b,new byte[]{2});CatalogPair(store,a);string pair=CatalogPair(store,b);
   var app=new App(new TestWall()){TestSettings=new MatteSettings{Playlist=new List<string>{a},SpatialFolder=store}};
   try{app.TestLoadCatalog();app.TestSettings.Playlist=new List<string>{b};app.TestLoadCatalog();var timer=System.Diagnostics.Stopwatch.StartNew();while(app.TestCatalogLoading&&timer.ElapsedMilliseconds<5000)Pump(20);
    Check(app.TestSpatialPath(a)==null&&app.TestSpatialPath(b)==pair&&app.TestSpatialSlides==1,"late catalogue replaced new playlist");
   }finally{app.TestClose();}
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
