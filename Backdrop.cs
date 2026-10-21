using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

// The panel's backdrop is the photo on the desktop, heavily blurred and dimmed (an "ambient" background)
// rather than a see-through blur of whatever is behind the panel. Nothing on screen is captured or
// tracked: when the photo changes the new ambience cross-fades in, in step with the wallpaper's own 1.6 s fade.
partial class App {
 Image ambBack,ambFront;string ambPath;int ambGeneration;const double AmbBleed=60;   // DIPs beyond the edges, so the blur has no edge
 UIElement ambLayer,ambDim,ambSheen;   // hidden in the simple style (PanelStyle)

 void CreateBackdrop(){
  var root=(Grid)panel.FindName("Root");var tone=ToneBrush;   // registers the animatable tone brush before anything is drawn
  // The images arrive already blurred (Ambience): a live BlurEffect would be recomputed on every frame of each
  // cross-fade, and on the same GPU that takes frames away from the Spatial wallpaper while the pointer moves.
  Func<Image> make=()=>{var im=new Image{Stretch=Stretch.UniformToFill,IsHitTestVisible=false};RenderOptions.SetBitmapScalingMode(im,BitmapScalingMode.HighQuality);return im;};
  ambBack=make();ambFront=make();ambFront.Opacity=0;
  // On a Canvas, so the images never size the panel (it sizes to its pages); they are fitted to it on every resize.
  var layer=new Canvas{IsHitTestVisible=false,ClipToBounds=false};layer.Children.Add(ambBack);layer.Children.Add(ambFront);
  root.SizeChanged+=delegate{FitBackdrop();};
  // A light warm dim on top (the darkening itself is done in Ambience, where it keeps the colour), and a faint light
  // edge at the top like glass.
  var dim=new System.Windows.Shapes.Rectangle{IsHitTestVisible=false,Fill=new SolidColorBrush(Color.FromArgb(AmbDim,0x1E,0x1A,0x18))};
  var sheen=new System.Windows.Shapes.Rectangle{IsHitTestVisible=false,Fill=new LinearGradientBrush(Color.FromArgb(0x1C,255,255,255),Color.FromArgb(0,255,255,255),90)};
  root.Background=new SolidColorBrush(Color.FromRgb(0x2A,0x25,0x22));   // until the first photo has loaded
  root.Children.Insert(0,sheen);root.Children.Insert(0,dim);root.Children.Insert(0,layer);
  ambLayer=layer;ambDim=dim;ambSheen=sheen;
 }
 void FitBackdrop(){var root=(Grid)panel.FindName("Root");foreach(var im in new[]{ambBack,ambFront}){Canvas.SetLeft(im,-AmbBleed);Canvas.SetTop(im,-AmbBleed);im.Width=root.ActualWidth+2*AmbBleed;im.Height=root.ActualHeight+2*AmbBleed;}}

 // Shows this photo's ambience; ms = cross-fade length (0 = at once, e.g. when the panel opens).
 void SetAmbient(string path,int ms){
  if(panel==null||SimpleStyle||string.IsNullOrEmpty(path)||string.Equals(path,ambPath,StringComparison.OrdinalIgnoreCase))return;
  ambPath=path;int gen=++ambGeneration;
  Task.Run(()=>Ambience(path))
   .ContinueWith(t=>panel.Dispatcher.BeginInvoke(new Action(()=>{
    if(gen!=ambGeneration)return;
    if(t.Exception!=null){Log("Ambient: "+t.Exception.InnerException.Message);return;}
    SetTone(t.Result.Item2,ms);
    if(ms<=0||ambBack.Source==null){ambFront.BeginAnimation(UIElement.OpacityProperty,null);ambFront.Opacity=0;ambBack.Source=t.Result.Item1;return;}
    var src=t.Result.Item1;ambFront.Source=src;
    var a=new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(ms)){EasingFunction=new SineEase{EasingMode=EasingMode.EaseInOut}};
    a.Completed+=delegate{if(ambFront.Source==src){ambBack.Source=src;ambFront.BeginAnimation(UIElement.OpacityProperty,null);ambFront.Opacity=0;}};
    ambFront.BeginAnimation(UIElement.OpacityProperty,a);
   })));
 }

 // The photo, small, with a luminosity layer that does all the darkening: every pixel's brightness is set to a level
 // white text reads on (the photo's average, mapped into a dark band) plus a little of its own difference from the
 // average, while its colour (the channels' distance from that brightness) is kept and lifted a little. A black layer
 // on top would darken the colour along with the brightness (that was the muddy look); this keeps the hue, so the
 // panel takes on the photo's colours. Then blurred here, once (three box passes = a Gaussian about as soft as the
 // 48 px BlurEffect it replaces). Also returns the photo's tone: its average colour, made deep and rich.
 const byte AmbDim=0x06;   // the light dim layer on top (2.4%)
 // keep: how much of each pixel's own difference from the average survives, more for the darks than the lights (depth
 // without bright patches behind the text); chroma: the colour lift, relative to how far the pixel was darkened
 const double AmbKeepDark=.42,AmbKeepLight=.27,AmbChroma=1.38,AmbChromaKnee=48,AmbChromaMax=90;   // channel distance from grey, 0-255
 static double AmbTarget(double mean){return Math.Max(70,Math.Min(150,mean))*.66+28;}   // 74..127 (on 0-255), before the dim
 static Tuple<BitmapSource,Color> Ambience(string path){
  var b=new BitmapImage();b.BeginInit();b.CacheOption=BitmapCacheOption.OnLoad;b.DecodePixelWidth=120;b.UriSource=new Uri(path);b.EndInit();b.Freeze();
  var src=new FormatConvertedBitmap(b,PixelFormats.Bgra32,null,0);int w=src.PixelWidth,h=src.PixelHeight,stride=w*4;var px=new byte[stride*h];src.CopyPixels(px,stride,0);
  double sum=0,r=0,g=0,bl=0;int n=w*h;
  for(int i=0;i<px.Length;i+=4){sum+=.0722*px[i]+.7152*px[i+1]+.2126*px[i+2];bl+=px[i];g+=px[i+1];r+=px[i+2];}
  double mean=sum/n,target=AmbTarget(mean);
  // colour lifted, but with a soft ceiling: a muted photo gains, a vivid one is held back (its colour bends toward
  // AmbChromaMax instead of growing on), so no photo turns the panel garish
  var dv=new double[3];
  // The colour scales with the brightness: a pixel darkened to half keeps half its distance from grey (times the lift),
  // so it keeps its saturation, as the wallpaper's own ambience shows it. Without this a bright, vivid photo darkened
  // behind the text kept its full colour on a dark base and looked over-saturated (a light purple turned to a hot one);
  // a photo that is dark already is hardly darkened, so it keeps its colour as before.
  for(int i=0;i<px.Length;i+=4){double l=.0722*px[i]+.7152*px[i+1]+.2126*px[i+2],l2=target+(l-mean)*(l<mean?AmbKeepDark:AmbKeepLight),m=0;
   double rel=Math.Max(.45,Math.Min(1.2,(l2+8)/(l+8)));
   for(int k=0;k<3;k++){dv[k]=(px[i+k]-l)*AmbChroma*rel;m=Math.Max(m,Math.Abs(dv[k]));}
   double f=m<=AmbChromaKnee?1:(AmbChromaKnee+(AmbChromaMax-AmbChromaKnee)*Math.Tanh((m-AmbChromaKnee)/(AmbChromaMax-AmbChromaKnee)))/m;
   for(int k=0;k<3;k++)px[i+k]=(byte)Math.Max(0,Math.Min(255,l2+dv[k]*f));px[i+3]=255;}
  for(int pass=0;pass<3;pass++){BoxBlur(px,w,h,9,true);BoxBlur(px,w,h,9,false);}   // wide: only colour and light are left, no shapes
  var even=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,px,stride);even.Freeze();
  return Tuple.Create((BitmapSource)even,Deep(Color.FromRgb((byte)(r/n),(byte)(g/n),(byte)(bl/n))));
 }
 // One box-blur pass over BGRA pixels, along rows or columns; edges repeat the border pixel (no dark rim).
 static void BoxBlur(byte[] px,int w,int h,int r,bool rows){
  int lines=rows?h:w,len=rows?w:h,step=rows?4:w*4,win=2*r+1;var line=new int[len*3];
  for(int a=0;a<lines;a++){
   int start=rows?a*w*4:a*4;
   for(int i=0;i<len;i++){int o=start+i*step;line[i*3]=px[o];line[i*3+1]=px[o+1];line[i*3+2]=px[o+2];}
   for(int c=0;c<3;c++){
    int sum=0;for(int i=-r;i<=r;i++)sum+=line[Math.Max(0,Math.Min(len-1,i))*3+c];
    for(int i=0;i<len;i++){px[start+i*step+c]=(byte)(sum/win);
     sum+=line[Math.Min(len-1,i+r+1)*3+c]-line[Math.Max(0,i-r)*3+c];}
   }
  }
 }
 // A dark, rich version of a colour: its hue, saturation 35-55%, lightness 20%. Near-greys stay a warm near-black.
 static Color Deep(Color c){
  double r=c.R/255.0,g=c.G/255.0,b=c.B/255.0,max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),d=max-min,h=0,s=max+min==0||max==min?0:d/(1-Math.Abs(max+min-1));
  if(d>0){h=max==r?((g-b)/d)%6:max==g?(b-r)/d+2:(r-g)/d+4;h*=60;if(h<0)h+=360;}
  if(s<.08){h=25;s=.12;}else s=Math.Max(.35,Math.Min(.55,s*1.2));
  double L=.20,cc=(1-Math.Abs(2*L-1))*s,x=cc*(1-Math.Abs((h/60)%2-1)),m=L-cc/2,R=0,G=0,B=0;
  if(h<60){R=cc;G=x;}else if(h<120){R=x;G=cc;}else if(h<180){G=cc;B=x;}else if(h<240){G=x;B=cc;}else if(h<300){R=x;B=cc;}else{R=cc;B=x;}
  return Color.FromRgb((byte)Math.Round((R+m)*255),(byte)Math.Round((G+m)*255),(byte)Math.Round((B+m)*255));
 }
}
