using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

// A photo's colour palette, as palette apps extract it: the whole picture (small) clustered in Lab (k-means, seeded the
// same way every time, so a photo always gets the same palette); the clusters ranked by how much of the picture they
// cover, with a bonus for colour (a vivid accent counts more than its size); then picked so each one is clearly
// different from the colours already offered.
static class Palette {
 struct Lab{public double L,A,B;public Lab(double l,double a,double b){L=l;A=a;B=b;}}
 // count colours from the palette, each at least far enough (CIE76) from avoid and from each other
 internal static List<Color> Pick(string path,Color avoid,int count){
  var pts=Pixels(path);if(pts.Count==0)return new List<Color>();
  var clusters=KMeans(pts,8);
  int total=pts.Count;
  var ranked=clusters.Where(c=>c.Item2>0).Select(c=>{double share=c.Item2/(double)total,chroma=Math.Sqrt(c.Item1.A*c.Item1.A+c.Item1.B*c.Item1.B);
   return Tuple.Create(c.Item1,Math.Sqrt(share)*(.55+Math.Min(chroma,60)/60));}).OrderByDescending(t=>t.Item2).Select(t=>t.Item1).ToList();
  var taken=new List<Lab>{ToLab(avoid)};var result=new List<Color>();
  foreach(double gap in new[]{24.0,16.0,10.0}){
   foreach(var c in ranked){if(result.Count>=count)break;
    if(taken.Any(t=>Dist(t,c)<gap))continue;taken.Add(c);result.Add(ToRgb(c));}
   if(result.Count>=count)break;}
  return result;
 }
 static List<Lab> Pixels(string path){
  var list=new List<Lab>();
  using(var im=App.LoadUpright(path)){
   double s=Math.Min(1,110.0/Math.Max(im.Width,im.Height));int w=Math.Max(1,(int)(im.Width*s)),h=Math.Max(1,(int)(im.Height*s));
   using(var b=new Bitmap(w,h)){using(var g=Graphics.FromImage(b)){g.InterpolationMode=InterpolationMode.HighQualityBilinear;g.DrawImage(im,0,0,w,h);}
    var d=b.LockBits(new Rectangle(0,0,w,h),System.Drawing.Imaging.ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    var px=new byte[d.Stride*h];System.Runtime.InteropServices.Marshal.Copy(d.Scan0,px,0,px.Length);b.UnlockBits(d);
    for(int y=0;y<h;y++)for(int x=0;x<w;x++){int i=y*d.Stride+x*4;list.Add(ToLab(Color.FromArgb(px[i+2],px[i+1],px[i])));}}
  }
  return list;
 }
 // k-means++ (fixed seed), then Lloyd's steps; returns (centre, members)
 static List<Tuple<Lab,int>> KMeans(List<Lab> pts,int k){
  var rnd=new Random(7);var centres=new List<Lab>{pts[rnd.Next(pts.Count)]};var d2=new double[pts.Count];
  while(centres.Count<k){double sum=0;for(int i=0;i<pts.Count;i++){double m=double.MaxValue;foreach(var c in centres)m=Math.Min(m,Dist2(pts[i],c));d2[i]=m;sum+=m;}
   if(sum<=0)break;double r=rnd.NextDouble()*sum;int pick=0;for(;pick<pts.Count-1;pick++){r-=d2[pick];if(r<=0)break;}centres.Add(pts[pick]);}
  var counts=new int[centres.Count];
  for(int iter=0;iter<14;iter++){
   var sums=new double[centres.Count,3];Array.Clear(counts,0,counts.Length);
   foreach(var p in pts){int best=0;double bd=double.MaxValue;for(int c=0;c<centres.Count;c++){double dd=Dist2(p,centres[c]);if(dd<bd){bd=dd;best=c;}}
    counts[best]++;sums[best,0]+=p.L;sums[best,1]+=p.A;sums[best,2]+=p.B;}
   for(int c=0;c<centres.Count;c++)if(counts[c]>0)centres[c]=new Lab(sums[c,0]/counts[c],sums[c,1]/counts[c],sums[c,2]/counts[c]);
  }
  return centres.Select((c,i)=>Tuple.Create(c,counts[i])).ToList();
 }
 static double Dist2(Lab a,Lab b){double l=a.L-b.L,x=a.A-b.A,y=a.B-b.B;return l*l+x*x+y*y;}
 static double Dist(Lab a,Lab b){return Math.Sqrt(Dist2(a,b));}
 static double Lin(double c){c/=255;return c<=.04045?c/12.92:Math.Pow((c+.055)/1.055,2.4);}
 static double Gam(double c){c=c<=.0031308?12.92*c:1.055*Math.Pow(c,1/2.4)-.055;return Math.Max(0,Math.Min(255,c*255));}
 static double F(double t){return t>.008856?Math.Pow(t,1/3.0):7.787*t+16/116.0;}
 static double Fi(double t){double t3=t*t*t;return t3>.008856?t3:(t-16/116.0)/7.787;}
 static Lab ToLab(Color c){
  double r=Lin(c.R),g=Lin(c.G),b=Lin(c.B);
  double x=(.4124*r+.3576*g+.1805*b)/.95047,y=.2126*r+.7152*g+.0722*b,z=(.0193*r+.1192*g+.9505*b)/1.08883;
  double fx=F(x),fy=F(y),fz=F(z);return new Lab(116*fy-16,500*(fx-fy),200*(fy-fz));
 }
 static Color ToRgb(Lab l){
  double fy=(l.L+16)/116,fx=fy+l.A/500,fz=fy-l.B/200;double x=Fi(fx)*.95047,y=Fi(fy),z=Fi(fz)*1.08883;
  double r=3.2406*x-1.5372*y-.4986*z,g=-.9689*x+1.8758*y+.0415*z,b=.0557*x-.2040*y+1.0570*z;
  return Color.FromArgb((int)Math.Round(Gam(r)),(int)Math.Round(Gam(g)),(int)Math.Round(Gam(b)));
 }
}
