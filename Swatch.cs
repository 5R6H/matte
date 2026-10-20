using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

// The app's mark (Matte, icon\Matte.png): a flat tile of one colour, edge to edge, with app-icon corners (radius 22.4% of
// the size, as on Claude's and most current app icons). The default is the icon's grey; at runtime the tray icon and
// desktop shortcut show the current wallpaper colour.
static class Swatch {
 public static readonly Color Neutral=Color.FromArgb(0xCC,0xCC,0xCC);
 public const float Corner=.224f;

 public static Bitmap Draw(Color color,int size){
  var bmp=new Bitmap(size,size,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
  using(var g=Graphics.FromImage(bmp)){
   g.SmoothingMode=SmoothingMode.AntiAlias;g.PixelOffsetMode=PixelOffsetMode.HighQuality;
   using(var path=Tile(size))using(var fill=new SolidBrush(color))g.FillPath(fill,path);
  }
  return bmp;
 }
 // The rounded tile filling a size x size icon.
 public static GraphicsPath Tile(int size){
  var p=new GraphicsPath();float d=2*Corner*size,e=size;
  p.AddArc(0,0,d,d,180,90);p.AddArc(e-d,0,d,d,270,90);p.AddArc(e-d,e-d,d,d,0,90);p.AddArc(0,e-d,d,d,90,90);p.CloseFigure();return p;
 }

 // A multi-size .ico (PNG frames) of the swatch: used for the app icon and the live desktop-shortcut icon.
 public static void WriteIco(Color color,string path,params int[] sizes){
  if(sizes.Length==0)sizes=new[]{16,20,24,32,40,48,64,96,128,256};
  var frames=new System.Collections.Generic.List<byte[]>();
  foreach(int s in sizes)using(var b=Draw(color,s))using(var ms=new System.IO.MemoryStream()){b.Save(ms,System.Drawing.Imaging.ImageFormat.Png);frames.Add(ms.ToArray());}
  using(var w=new System.IO.BinaryWriter(System.IO.File.Create(path))){
   w.Write((short)0);w.Write((short)1);w.Write((short)sizes.Length);int offset=6+16*sizes.Length;
   for(int i=0;i<sizes.Length;i++){byte d=(byte)(sizes[i]>=256?0:sizes[i]);w.Write(d);w.Write(d);w.Write((byte)0);w.Write((byte)0);w.Write((short)1);w.Write((short)32);w.Write(frames[i].Length);w.Write(offset);offset+=frames[i].Length;}
   foreach(var f in frames)w.Write(f);
  }
 }

 [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
 // A tray-sized Icon that owns its handle (the GetHicon handle is cloned and released, so colour changes do not leak).
 public static Icon TrayIcon(Color color,int size){
  using(var bmp=Draw(color,size)){
   IntPtr h=bmp.GetHicon();var icon=(Icon)Icon.FromHandle(h).Clone();DestroyIcon(h);
   return icon;
  }
 }
}
