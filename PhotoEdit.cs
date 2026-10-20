using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// 调整图片: rotate / flip the photo file itself:
// the EXIF orientation is applied first, JPEG is re-encoded at the highest quality and PNG / BMP / TIFF losslessly, and
// the result is written through the existing file, so every hard link to it changes too. Nothing is written if the file
// changed since it was read; on a failed write the original bytes go back.
//
// Edits are collected first (Pending) and written once, on 保存. A pending edit is one element of the square's symmetry
// group: an optional mirror (left-right) followed by 0-3 quarter turns clockwise. Steps that cancel out (four turns,
// left then right, two flips) come back to the identity, and then nothing is written at all.
static class PhotoEdit {
 internal const int RotateRight=0,RotateLeft=1,FlipHorizontal=2,FlipVertical=3;
 internal struct Pending {
  public int Turns;public bool Mirror;   // the picture is mirrored first (if Mirror), then turned Turns x 90° clockwise
  public bool IsIdentity{get{return Turns==0&&!Mirror;}}
  // One more step, applied after the ones so far. (A left-right mirror after k turns = -k turns after the mirror;
  // an upside-down flip = a left-right mirror and half a turn.)
  public Pending Then(int op){
   var p=this;
   if(op==RotateRight)p.Turns=(p.Turns+1)%4;
   else if(op==RotateLeft)p.Turns=(p.Turns+3)%4;
   else if(op==FlipHorizontal){p.Turns=(4-p.Turns)%4;p.Mirror=!p.Mirror;}
   else if(op==FlipVertical){p.Turns=(6-p.Turns)%4;p.Mirror=!p.Mirror;}
   return p;
  }
 }
 static readonly string[] Supported={".jpg",".jpeg",".png",".bmp",".tif",".tiff"};
 internal static bool CanEdit(string path){return Supported.Contains(Path.GetExtension(path).ToLowerInvariant());}
 internal static void Apply(string path,int op){Apply(path,new Pending().Then(op));}
 internal static void Apply(string path,Pending edit){
  if(edit.IsIdentity)return;
  string ext=Path.GetExtension(path).ToLowerInvariant();BitmapEncoder encoder;
  if(ext==".jpg"||ext==".jpeg")encoder=new JpegBitmapEncoder{QualityLevel=100};
  else if(ext==".png")encoder=new PngBitmapEncoder();
  else if(ext==".bmp")encoder=new BmpBitmapEncoder();
  else if(ext==".tif"||ext==".tiff")encoder=new TiffBitmapEncoder();
  else throw new IOException("这种格式不能直接修改原文件；支持 JPG、PNG、BMP 和单页 TIFF。");
  byte[] original=File.ReadAllBytes(path),output;
  using(var input=new MemoryStream(original)){
   var decoder=BitmapDecoder.Create(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
   if(decoder.Frames.Count!=1)throw new IOException("多帧图片不修改，以免丢失其他帧。");
   BitmapSource source=decoder.Frames[0];var metadata=((BitmapFrame)source).Metadata as BitmapMetadata;int orientation=1;
   if(metadata!=null)foreach(string query in new[]{"/app1/ifd/{ushort=274}","/ifd/{ushort=274}"})try{if(metadata.ContainsQuery(query)){orientation=Convert.ToInt32(metadata.GetQuery(query));break;}}catch{}
   var t=new TransformGroup();
   switch(orientation){   // upright first (the new file carries no orientation tag)
    case 2:t.Children.Add(new ScaleTransform(-1,1));break;case 3:t.Children.Add(new RotateTransform(180));break;case 4:t.Children.Add(new ScaleTransform(1,-1));break;
    case 5:t.Children.Add(new ScaleTransform(-1,1));t.Children.Add(new RotateTransform(270));break;case 6:t.Children.Add(new RotateTransform(90));break;
    case 7:t.Children.Add(new ScaleTransform(-1,1));t.Children.Add(new RotateTransform(90));break;case 8:t.Children.Add(new RotateTransform(270));break;
   }
   if(edit.Mirror)t.Children.Add(new ScaleTransform(-1,1));
   if(edit.Turns!=0)t.Children.Add(new RotateTransform(90*edit.Turns));
   encoder.Frames.Add(BitmapFrame.Create(new TransformedBitmap(source,t)));
   using(var result=new MemoryStream()){encoder.Save(result);output=result.ToArray();}
  }
  using(var stream=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
   if(stream.Length!=original.Length)throw new IOException("图片刚刚被改动过，没有写入。");
   var check=new byte[original.Length];int read=0;while(read<check.Length){int n=stream.Read(check,read,check.Length-read);if(n==0)throw new IOException("图片读取不完整。");read+=n;}
   if(!check.SequenceEqual(original))throw new IOException("图片刚刚被改动过，没有写入。");
   try{stream.Position=0;stream.Write(output,0,output.Length);stream.SetLength(output.Length);stream.Flush(true);}
   catch{stream.Position=0;stream.Write(original,0,original.Length);stream.SetLength(original.Length);stream.Flush(true);throw;}
  }
 }
}
