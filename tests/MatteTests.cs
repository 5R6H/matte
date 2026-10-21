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
}

static class MatteTests {
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

 [STAThread] static int Main(){
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
   File.WriteAllText(p+".bak","");Check(SettingsStore.Load(p,t=>js.Deserialize<MatteSettings>(t),log.Add)==null,"neither reads: should be null");
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
 static GenProgress Feed(params string[] lines){var g=new GenProgress();foreach(var l in lines)g.Line(l);return g;}
}
