using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// 延展: the wallpaper's third background. Where the photo leaves the screen open (the sides of a portrait photo, top
// and bottom of a very wide one), its own edge flows outward and dissolves into the photo's colour (ColorFor):
// a harmonic (membrane) fill - the smoothest surface that equals the photo's edge on one side and the colour at the
// screen's edge on the other (Laplace's equation, as in seamless cloning). Fine detail at the edge fades within a few
// pixels by itself, broad colour carries far, and nothing is stretched into streaks.
// Solved at a quarter of the screen's size (it is smooth), coarse to fine; then enlarged, with the edge's own
// full-size detail added back and faded over the first few pixels, so photo and fill meet without a seam.
static class ExtendWallpaper {
 const int Work=4;   // the solve runs at 1/Work of the screen
 // inset: the photo fitted inside this margin (the 编辑壁纸 preview, whose photo sits 14 px in); the fill covers all
 internal static Bitmap Make(Image photo,Size screen,Color bg){return Make(photo,screen,bg,0);}
 internal static Bitmap Make(Image photo,Size screen,Color bg,int inset){
  var fit=Fit(photo.Size,screen,inset);
  bool sides=fit.Width<screen.Width-1,tops=fit.Height<screen.Height-1;
  if(!sides&&!tops)return Plain(photo,screen,bg,fit);
  if(sides)return MakeSides(photo,screen,bg,inset);
  // bars above and below: the same, turned a quarter
  using(var turned=(Image)photo.Clone()){turned.RotateFlip(RotateFlipType.Rotate90FlipNone);
   var r=MakeSides(turned,new Size(screen.Height,screen.Width),bg,inset);r.RotateFlip(RotateFlipType.Rotate270FlipNone);return r;}
 }
 static Bitmap Plain(Image photo,Size screen,Color bg,Rectangle fit){
  var big=new Bitmap(screen.Width,screen.Height,PixelFormat.Format24bppRgb);
  using(var g=Graphics.FromImage(big)){g.Clear(bg);Draw(g,photo,fit);}return big;
 }
 static void Draw(Graphics g,Image photo,Rectangle r){
  g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.CompositingQuality=CompositingQuality.HighQuality;
  using(var a=new ImageAttributes()){a.SetWrapMode(WrapMode.TileFlipXY);g.DrawImage(photo,r,0,0,photo.Width,photo.Height,GraphicsUnit.Pixel,a);}
 }
 static Rectangle Fit(Size picture,Size screen,int inset=0){
  int aw=Math.Max(1,screen.Width-2*inset),ah=Math.Max(1,screen.Height-2*inset);
  double s=Math.Min((double)aw/picture.Width,(double)ah/picture.Height);int w=(int)Math.Round(picture.Width*s),h=(int)Math.Round(picture.Height*s);
  return new Rectangle((screen.Width-w)/2,(screen.Height-h)/2,w,h);
 }
 // the photo spans the screen's height, open to the left and right
 static Bitmap MakeSides(Image photo,Size screen,Color bg,int inset){
  int sw=(screen.Width+Work-1)/Work,sh=(screen.Height+Work-1)/Work;var rs=Fit(photo.Size,new Size(sw,sh),(inset+Work-1)/Work);
  float[] bgc={bg.R,bg.G,bg.B};
  // the small screen: the photo, and the two fills solved against its edge columns
  var small=new float[3][];for(int k=0;k<3;k++){small[k]=new float[sw*sh];for(int i=0;i<sw*sh;i++)small[k][i]=bgc[k];}
  using(var sp=new Bitmap(sw,sh,PixelFormat.Format32bppArgb)){
   using(var g=Graphics.FromImage(sp)){g.Clear(bg);Draw(g,photo,rs);}
   var px=Pixels(sp);
   for(int y=0;y<sh;y++)for(int x=rs.Left;x<rs.Right;x++)for(int k=0;k<3;k++)small[k][y*sw+x]=px[(y*sw+x)*4+2-k];
  }
  for(int side=0;side<2;side++){
   int gap=side==0?rs.Left:sw-rs.Right;if(gap<=0)continue;int edgeX=side==0?rs.Left:rs.Right-1;
   for(int k=0;k<3;k++){
    var edge=new float[sh];for(int y=0;y<sh;y++)edge[y]=small[k][y*sw+edgeX];
    var u=Solve(edge,gap,bgc[k]);
    for(int y=0;y<sh;y++)for(int d=1;d<=gap;d++){int x=side==0?edgeX-d:edgeX+d;small[k][y*sw+x]=u[y*(gap+2)+d];}
   }
  }
  // enlarged to the screen, the photo at full size over it
  var big=new Bitmap(screen.Width,screen.Height,PixelFormat.Format24bppRgb);
  using(var sb=new Bitmap(sw,sh,PixelFormat.Format32bppArgb)){
   SetPixels(sb,small);
   using(var g=Graphics.FromImage(big)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
    using(var a=new ImageAttributes()){a.SetWrapMode(WrapMode.TileFlipXY);g.DrawImage(sb,new Rectangle(0,0,screen.Width,screen.Height),0,0,sw,sh,GraphicsUnit.Pixel,a);}
    Draw(g,photo,Fit(photo.Size,screen,inset));}
  }
  EdgeDetail(big,Fit(photo.Size,screen,inset));
  return big;
 }
 // Laplace on a strip h × (gap+2): column 0 is the edge, column gap+1 the colour; top and bottom rows mirror their
 // neighbours (free). Successive over-relaxation, coarse to fine.
 static float[] Solve(float[] edge,int gap,float colour){
  int h=edge.Length;float[] u=null;int uh=0,uw=0;
  foreach(int f in new[]{16,8,4,2,1}){
   int hh=Math.Max(2,h/f),w=Math.Max(1,gap/f),W2=w+2;var g=new float[hh*W2];
   for(int y=0;y<hh;y++){double s=0;int n=0;for(int yy=y*h/hh;yy<Math.Min(h,(y+1)*h/hh);yy++){s+=edge[yy];n++;}float e=(float)(s/Math.Max(1,n));
    g[y*W2]=e;g[y*W2+w+1]=colour;
    for(int x=1;x<=w;x++)g[y*W2+x]=u==null?(float)(e+(colour-e)*x/(double)(w+1)):u[Math.Min(uh-1,y*uh/hh)*(uw+2)+Math.Max(1,Math.Min(uw,(int)Math.Round(x*uw/(double)w)))];}
   int iters=f>=16?600:f==8?300:f==4?150:f==2?80:40;
   for(int it=0;it<iters;it++)for(int y=0;y<hh;y++){int up=(y>0?y-1:y)*W2,dn=(y<hh-1?y+1:y)*W2,row=y*W2;
    for(int x=1;x<=w;x++){int i=row+x;float avg=(g[i-1]+g[i+1]+g[up+x]+g[dn+x])*.25f;g[i]+=1.85f*(avg-g[i]);}}
   u=g;uh=hh;uw=w;
  }
  if(uw==gap&&uh==h)return u;
  var r=new float[h*(gap+2)];for(int y=0;y<h;y++)for(int x=0;x<gap+2;x++)r[y*(gap+2)+x]=u[Math.Min(uh-1,y*uh/h)*(uw+2)+Math.Min(uw+1,x*(uw+1)/Math.Max(1,gap+1))];return r;
 }
 // At full size, the first pixels outside the photo take back the edge's own detail (lost at quarter size), fading
 // out over a few pixels: the photo's edge flows on without a step.
 static void EdgeDetail(Bitmap big,Rectangle r){
  var d=big.LockBits(new Rectangle(0,0,big.Width,big.Height),ImageLockMode.ReadWrite,PixelFormat.Format24bppRgb);
  try{var row=new byte[d.Stride];const int Reach=10;
   for(int y=Math.Max(0,r.Top);y<Math.Min(big.Height,r.Bottom);y++){var p=d.Scan0+y*d.Stride;System.Runtime.InteropServices.Marshal.Copy(p,row,0,d.Stride);
    foreach(int side in new[]{-1,1}){int ex=side<0?r.Left:r.Right-1;int first=ex+side;if(first<0||first>=big.Width)continue;
     for(int k=0;k<3;k++){double res=row[ex*3+k]-row[first*3+k];
      for(int dd=0;dd<Reach;dd++){int x=first+side*dd;if(x<0||x>=big.Width)break;row[x*3+k]=(byte)Math.Max(0,Math.Min(255,row[x*3+k]+res*Math.Exp(-dd/2.2)));}}}
    System.Runtime.InteropServices.Marshal.Copy(row,0,p,d.Stride);}}
  finally{big.UnlockBits(d);}
 }
 static byte[] Pixels(Bitmap b){var d=b.LockBits(new Rectangle(0,0,b.Width,b.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);var px=new byte[b.Width*b.Height*4];
  for(int y=0;y<b.Height;y++)System.Runtime.InteropServices.Marshal.Copy(d.Scan0+y*d.Stride,px,y*b.Width*4,b.Width*4);b.UnlockBits(d);return px;}
 static void SetPixels(Bitmap b,float[][] c){var d=b.LockBits(new Rectangle(0,0,b.Width,b.Height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);var row=new byte[b.Width*4];
  for(int y=0;y<b.Height;y++){for(int x=0;x<b.Width;x++){int i=y*b.Width+x;row[x*4]=Clamp(c[2][i]);row[x*4+1]=Clamp(c[1][i]);row[x*4+2]=Clamp(c[0][i]);row[x*4+3]=255;}
   System.Runtime.InteropServices.Marshal.Copy(row,0,d.Scan0+y*d.Stride,b.Width*4);}b.UnlockBits(d);}
 static byte Clamp(float v){return (byte)Math.Max(0,Math.Min(255,v));}
}
