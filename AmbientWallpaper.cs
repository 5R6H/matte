using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

// The wallpaper's background: the solid colour (ColorFor) or the ambience - the photo itself, enlarged to fill the
// screen and heavily blurred, a little darker, with a soft shadow where the photo sits. It is made once per photo and
// screen size into ambient-cache (Matte's own folder, like display-cache): <key>.jpg is the finished wallpaper (the
// photo fitted on its ambience; Windows shows it at Fit, so it fills the screen exactly), <key>-bg.jpg the ambience
// alone (behind a Spatial scene, SpatialNative's backdrop).
// The choice is global (settings.Background, the menu's 壁纸背景): every photo the same way, no exceptions per photo.
partial class App {
 const string AmbientVersion="a2";   // a new look: the cached ambiences are made again
 // color | extend (ExtendWallpaper.cs) | ambient
 string BackgroundMode{get{return settings.Background=="ambient"||settings.Background=="extend"?settings.Background:"color";}}
 bool AmbientDefault{get{return settings.Background=="ambient";}}
 // Whether this photo shows on its ambience (the same for every photo).
 bool AmbientFor(string display){return AmbientDefault;}
 // The global choice; the photo on screen shows it at once.
 void SetBackgroundMode(string mode){
  if(BackgroundMode==mode)return;settings.Background=mode;SaveSettings();
  ReshowCurrent();
 }
 // The photo on screen again with its background as now chosen; its countdown carries on.
 void ReshowCurrent(){
  if(currentSourcePath==null||!File.Exists(currentSourcePath))return;
  var keep=nextSlide;try{ShowSlide(currentSourcePath);}catch(Exception e){Log("Background change: "+e.Message);}nextSlide=keep;
 }

 // What Windows shows for this photo: the composite on ambience, or the photo itself (on the colour).
 // backdrop: the ambience alone, or null.
 string WallpaperImage(string display,out string backdrop){
  backdrop=null;
  if(BackgroundMode=="extend"){try{string done=ExtendFile(display,ColorFor(display));backdrop=done;return done;}catch(Exception e){Log("Extend wallpaper: "+e.Message);return display;}}
  if(!AmbientFor(display))return display;
  try{string bg;string done=AmbientFiles(display,out bg);backdrop=bg;return done;}
  catch(Exception e){Log("Ambient wallpaper: "+e.Message);return display;}
 }
 static readonly object ambientLock=new object();
 static Size AmbientScreen{get{return Screen.PrimaryScreen.Bounds.Size;}}
 string AmbientKey(string display){
  var s=AmbientScreen;string key=display+"|"+File.GetLastWriteTimeUtc(display).Ticks+"|"+s.Width+"x"+s.Height+"|"+AmbientVersion;
  using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key))).Replace("-","").Substring(0,40);
 }
 static string AmbientDir{get{return Path.Combine(Dir,"ambient-cache");}}
 // Made if missing (also from a background thread, ahead of the next slide); returns the composite.
 string AmbientFiles(string display,out string backdrop){
  string key=AmbientKey(display),done=Path.Combine(AmbientDir,key+".jpg");backdrop=Path.Combine(AmbientDir,key+"-bg.jpg");
  lock(ambientLock){
   if(File.Exists(done)&&File.Exists(backdrop))return done;
   Directory.CreateDirectory(AmbientDir);
   var size=AmbientScreen;
   using(var photo=Image.FromFile(display)){
    Upright(photo);
    using(var bg=AmbienceImage(photo,size)){
     SaveJpeg(bg,backdrop,90);
     using(var g=Graphics.FromImage(bg)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.CompositingQuality=CompositingQuality.HighQuality;
      var r=FitRect(photo.Size,size);using(var a=new ImageAttributes()){a.SetWrapMode(WrapMode.TileFlipXY);g.DrawImage(photo,r,0,0,photo.Width,photo.Height,GraphicsUnit.Pixel,a);}}
     SaveJpeg(bg,done,95);
    }
   }
   TrimAmbientCache();
  }
  return done;
 }
 // As Windows shows it: turned by its EXIF orientation (GDI+ loads the stored pixels).
 static void Upright(Image im){
  if(Array.IndexOf(im.PropertyIdList,0x0112)<0)return;int o=BitConverter.ToUInt16(im.GetPropertyItem(0x0112).Value,0);
  var f=o==2?RotateFlipType.RotateNoneFlipX:o==3?RotateFlipType.Rotate180FlipNone:o==4?RotateFlipType.Rotate180FlipX:o==5?RotateFlipType.Rotate90FlipX:o==6?RotateFlipType.Rotate90FlipNone:o==7?RotateFlipType.Rotate270FlipX:o==8?RotateFlipType.Rotate270FlipNone:RotateFlipType.RotateNoneFlipNone;
  if(f!=RotateFlipType.RotateNoneFlipNone)im.RotateFlip(f);
 }
 static Rectangle FitRect(Size picture,Size screen){
  double scale=Math.Min((double)screen.Width/picture.Width,(double)screen.Height/picture.Height);int w=(int)Math.Round(picture.Width*scale),h=(int)Math.Round(picture.Height*scale);
  return new Rectangle((screen.Width-w)/2,(screen.Height-h)/2,w,h);
 }
 // The photo filling a small canvas of the screen's shape (centre crop), blurred hard, then made like acrylic: its
 // contrast eased toward its own average, a little lighter and a little less saturated (a light tint, as frosted glass
 // has), with no shadow around the photo, so photo and ambience flow into each other; then enlarged to the screen
 // (smooth: it is all blur) with a faint grain, the texture that makes it read as a material rather than a smear.
 internal static Bitmap AmbienceImage(Image photo,Size screen){
  const int SW=192;int sh=Math.Max(8,(int)Math.Round(SW*(double)screen.Height/screen.Width));
  using(var small=new Bitmap(SW,sh,PixelFormat.Format32bppArgb)){
   using(var g=Graphics.FromImage(small)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
    double cover=Math.Max((double)SW/photo.Width,(double)sh/photo.Height);int w=(int)Math.Ceiling(photo.Width*cover),h=(int)Math.Ceiling(photo.Height*cover);
    using(var a=new ImageAttributes()){a.SetWrapMode(WrapMode.TileFlipXY);g.DrawImage(photo,new Rectangle((SW-w)/2,(sh-h)/2,w,h),0,0,photo.Width,photo.Height,GraphicsUnit.Pixel,a);}
   }
   var d=small.LockBits(new Rectangle(0,0,SW,sh),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);var px=new byte[d.Stride*sh];
   System.Runtime.InteropServices.Marshal.Copy(d.Scan0,px,0,px.Length);
   for(int pass=0;pass<3;pass++){BoxBlur(px,SW,sh,7,true);BoxBlur(px,SW,sh,7,false);}
   double sum=0;int n=0;for(int i=0;i<px.Length;i+=4){sum+=.0722*px[i]+.7152*px[i+1]+.2126*px[i+2];n++;}double mean=sum/Math.Max(1,n);
   for(int i=0;i<px.Length;i+=4){double l=.0722*px[i]+.7152*px[i+1]+.2126*px[i+2],l2=mean+(l-mean)*AmbWallContrast;
    for(int k=0;k<3;k++){double c=l2+(px[i+k]-l)*AmbWallChroma;c=c+(255-c)*AmbWallTint;px[i+k]=(byte)Math.Max(0,Math.Min(255,c));}px[i+3]=255;}
   System.Runtime.InteropServices.Marshal.Copy(px,0,d.Scan0,px.Length);small.UnlockBits(d);
   var big=new Bitmap(screen.Width,screen.Height,PixelFormat.Format24bppRgb);
   using(var g=Graphics.FromImage(big)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
    using(var a=new ImageAttributes()){a.SetWrapMode(WrapMode.TileFlipXY);g.DrawImage(small,new Rectangle(0,0,screen.Width,screen.Height),0,0,SW,sh,GraphicsUnit.Pixel,a);}}
   Grain(big,AmbWallGrain);
   return big;
  }
 }
 const double AmbWallContrast=.68,AmbWallChroma=.92,AmbWallTint=.07,AmbWallGrain=2.2;
 // A fine monochrome grain (the same for every photo, so it never flickers between slides).
 static void Grain(Bitmap b,double amount){
  var d=b.LockBits(new Rectangle(0,0,b.Width,b.Height),ImageLockMode.ReadWrite,PixelFormat.Format24bppRgb);
  try{var row=new byte[d.Stride];uint seed=2463534242;
   for(int y=0;y<b.Height;y++){var p=d.Scan0+y*d.Stride;System.Runtime.InteropServices.Marshal.Copy(p,row,0,d.Stride);
    for(int x=0;x<b.Width;x++){seed^=seed<<13;seed^=seed>>17;seed^=seed<<5;double v=((seed&1023)/1023.0-.5)*2*amount;int o=x*3;
     for(int k=0;k<3;k++)row[o+k]=(byte)Math.Max(0,Math.Min(255,row[o+k]+v));}
    System.Runtime.InteropServices.Marshal.Copy(row,0,p,d.Stride);}}
  finally{b.UnlockBits(d);}
 } static void SaveJpeg(Bitmap b,string path,long quality){
  var codec=ImageCodecInfo.GetImageEncoders().First(c=>c.FormatID==ImageFormat.Jpeg.Guid);
  using(var p=new EncoderParameters(1)){p.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,quality);
   string tmp=path+".tmp";b.Save(tmp,codec,p);if(File.Exists(path))File.Delete(path);File.Move(tmp,path);}
 }
 // 延展: the photo with its edges flowing into its colour, screen-sized, made once per photo, screen and colour (a new
 // colour on 编辑壁纸 makes it again). Also the backdrop behind a Spatial scene (the photo part is under the scene).
 string ExtendFile(string display,uint color){
  var size=AmbientScreen;string key=AmbientKey(display)+"-"+color.ToString("X6");string done=Path.Combine(AmbientDir,key+"-x.jpg");
  lock(ambientLock){
   if(File.Exists(done))return done;Directory.CreateDirectory(AmbientDir);
   using(var photo=Image.FromFile(display)){Upright(photo);using(var b=ExtendWallpaper.Make(photo,size,Unpack(color)))SaveJpeg(b,done,95);}
   TrimAmbientCache();
  }
  return done;
 }
 // A deleted photo's ambience goes with it (the wallpaper is moved off it first by the next slide).
 void DeleteAmbient(string display){
  try{string key=AmbientKey(display);lock(ambientLock)foreach(string p in Directory.Exists(AmbientDir)?Directory.GetFiles(AmbientDir,key+"*.jpg"):new string[0]){if(File.Exists(p))try{File.Delete(p);}catch(Exception e){Log("Ambient delete: "+e.Message);}}}
  catch{}
 }
 // At most ~300 photos' worth: the oldest go first (made again if they come back).
 static void TrimAmbientCache(){
  try{var files=new DirectoryInfo(AmbientDir).GetFiles("*.jpg").OrderByDescending(f=>f.LastWriteTimeUtc).ToList();
   foreach(var f in files.Skip(600))try{f.Delete();}catch{}}catch{}
 }
 // The next photo's ambience made ahead (the slide change itself then only reads it).
 void PrepareNextAmbient(){
  if(slideFiles.Count==0)return;
  string next=slideAhead.Count>0?slideAhead[slideAhead.Count-1]:slideFiles[slideIndex%slideFiles.Count];
  string mode=BackgroundMode;if(mode=="color")return;
  uint col=0;string disp=null;
  if(mode=="extend"){try{if(!File.Exists(next))return;disp=DisplayPath(next);col=ColorFor(disp);}catch(Exception e){Log("Extend ahead: "+e.Message);return;}}   // the colour here (its cache is the UI thread's)
  Task.Run(()=>{try{if(!File.Exists(next))return;string display=disp??DisplayPath(next);if(mode=="extend")ExtendFile(display,col);else{string bg;AmbientFiles(display,out bg);}}catch(Exception e){Log("Background ahead: "+e.Message);}});
 }
}
