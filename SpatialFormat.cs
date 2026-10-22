using System;
using System.IO;

// v1 wire layout: spatial/exporter.py. Keep these named limits available to the native parity work (T3b).
// matte_sharp.py actually uses 30 slices, block 32, 8% outfill, atlasBlockLimit 256.
// A 2560x1600 source becomes 2784x1728: 30*((87+1)*(54+1)+87*54) = 286140 dense slots.
// 3840x2160 becomes 4160x2336: 575520 slots; 7680x4320 becomes 8320x4672: 2289810 slots.
// 4M slots gives ~14.7x/7.3x/1.8x headroom, at most 16 MiB of native dense depth/coordinate arrays.
// The exporter's 256x256 atlas block capacity is 65536 blocks; allow 4x that (262144), giving
// 1M vertices + 1.5M indices = 26 MiB mesh, plus 1 MiB block indices. Atlas pixels allow the full
// 8192x8192 current exporter ceiling: 256 MiB RGBA + 64 MiB alpha, ~640 MiB for two fading scenes,
// excluding render targets/file buffers. These are per-artifact admission limits, not a global memory quota.
// Validation keeps only two reusable bit masks, a header and a scalar buffer: < 517 KiB, independent of JPEG size.
internal static class SpatialFormat {
 internal const long MaxFileBytes=1L<<30,MaxDenseSlots=1L<<22,MaxBlocks=1L<<18,MaxVertices=MaxBlocks*4,MaxIndices=MaxBlocks*6;
 internal const long MaxMeshBytes=MaxVertices*20+MaxIndices*4,MaxAtlasPixels=1L<<26,MaxValidationBytes=(MaxDenseSlots+7)/8+4096;
 internal const int MaxSlices=64,MaxBlockSize=256,MaxDimension=16384,HeaderBytes=68;
 internal sealed class Summary {
  internal int Slices,AtlasWidth,AtlasHeight,ColorWidth,ColorHeight,AlphaWidth,AlphaHeight;
  internal long Blocks,DenseSlots,Vertices,Indices,MeshBytes,ColorOffset,ColorBytes,AlphaOffset,AlphaBytes,ValidationBytes;
 }
 static void Require(bool ok,string why){if(!ok)throw new InvalidDataException(why);}
 static bool Finite(float f){return !float.IsNaN(f)&&!float.IsInfinity(f);}
 static bool Positive(float f){return Finite(f)&&f>0;}
 static long Product(long a,long b){return checked(a*b);}
 internal static long CornerSlots(long width,long height){return Product(checked(width+1),checked(height+1));}
 internal static bool CheckLength(Stream stream,out string why){
  why=null;if(stream==null||!stream.CanRead||!stream.CanSeek){why="readable seekable stream required";return false;}
  long size=stream.Length;if(size>MaxFileBytes){why="file exceeds 1 GiB cap";return false;}if(size<HeaderBytes){why="truncated header";return false;}return true;
 }
 internal static bool Validate(Stream stream,out string why){Summary summary;return Validate(stream,out summary,out why);}
 // The optional observer runs before each input-sized allocation; tests assert the same cumulative quota.
 internal static bool Validate(Stream stream,out Summary summary,out string why,Action<long> allocating=null){
  summary=null;why=null;
  try{
   if(!CheckLength(stream,out why))return false;
   stream.Position=0;var r=new Reader(stream,allocating);byte[] header=r.Bytes(HeaderBytes);
   Require(U32(header,0)==4280403,"not a Spatial file");
   long iw=U32(header,4),ih=U32(header,8),ns=U32(header,12),bs=U32(header,16),aw=U32(header,24),ah=U32(header,28);
   long totalCorners=U32(header,40),totalBlocks=U32(header,44),geometry=U32(header,48),cb=U32(header,52),ab=U32(header,56),ow=U32(header,60),oh=U32(header,64);
   float focal=BitConverter.ToSingle(header,20),padding=BitConverter.ToSingle(header,36);
   Require(ns>=1&&ns<=MaxSlices,"slice count out of range");Require(bs>=1&&bs<=MaxBlockSize,"block size out of range");
   Require(Dimension(iw)&&Dimension(ih),"image dimensions out of range");Require(Dimension(ow)&&Dimension(oh),"original dimensions out of range");
   Require(Dimension(aw)&&Dimension(ah),"atlas dimensions out of range");
   Require(Positive(focal),"focal must be finite and positive");Require(Finite(padding)&&padding>=0&&padding<=bs/2.0,"uvPadding out of range");
   long gw=iw/bs,gh=ih/bs,corners=CornerSlots(gw,gh),cells=Product(gw,gh),dense=Product(checked(corners+cells),ns);
   Require(gw>0&&gh>0,"empty block grid");Require(dense<=MaxDenseSlots,"dense slot budget exceeded");
   Require(totalCorners>0&&totalCorners<=Product(corners,ns),"totalCorners out of range");
   Require(totalBlocks>0&&totalBlocks<=Product(cells,ns),"totalBlocks out of range");Require(totalBlocks<=MaxBlocks,"block budget exceeded");
   long vertices=Product(totalBlocks,4),indices=Product(totalBlocks,6),vb=Product(vertices,20),ib=Product(indices,4),mesh=checked(vb+ib);
   Require(vertices<=MaxVertices&&indices<=MaxIndices&&mesh<=MaxMeshBytes&&vb<=uint.MaxValue&&ib<=uint.MaxValue&&indices<=uint.MaxValue,"mesh budget exceeded");
   Require(Product(aw,ah)<=MaxAtlasPixels,"atlas pixel budget exceeded");Require(aw/bs>0&&ah/bs>0,"empty atlas grid");
   long co=checked(HeaderBytes+geometry),ao=checked(co+cb),end=checked(ao+ab);
   Require(co<=stream.Length,"truncated geometry section");Require(cb>0&&ab>0,"empty image section");Require(end<=stream.Length,"truncated image sections");
   Require(end==stream.Length,"trailing bytes after image sections");
   var info=new Summary{Slices=(int)ns,Blocks=totalBlocks,DenseSlots=dense,Vertices=vertices,Indices=indices,MeshBytes=mesh,AtlasWidth=(int)aw,AtlasHeight=(int)ah,ColorOffset=co,ColorBytes=cb,AlphaOffset=ao,AlphaBytes=ab};
   r.Limit=co;byte[] cm=r.Allocate((corners+7)/8),bm=r.Allocate((cells+7)/8);long sumCorners=0,sumBlocks=0;float minZ=float.PositiveInfinity,maxZ=0;
   for(long s=0;s<ns;s++){
    long nd=r.UInt(),nb=r.UInt();Require(nd<=corners&&nb<=cells,"slice counts exceed mask size");
    sumCorners=checked(sumCorners+nd);sumBlocks=checked(sumBlocks+nb);
    Require(sumCorners<=totalCorners&&sumBlocks<=totalBlocks,"slice sums exceed header totals");
    r.Mask(cm,corners,nd);
    for(long n=0;n<nd;n++){
     float depth=r.Float();Require(Positive(depth),"depth must be finite and positive");minZ=Math.Min(minZ,depth);maxZ=Math.Max(maxZ,depth);
     // BuildMesh rounds the coordinate*depth product to float before division by focal.
     float extent=(float)(Math.Max(iw,ih)*.5),scaled=(float)(extent*depth);
     Require(Finite(scaled)&&Finite((float)(scaled/focal)),"mesh projection is not finite");
    }
    r.Mask(bm,cells,nb);
    for(long n=0;n<cells;n++)if(Bit(bm,n)){
     long x=n%gw,y=n/gw,c=checked(y*(gw+1)+x);
     Require(Bit(cm,c)&&Bit(cm,c+1)&&Bit(cm,c+gw+1)&&Bit(cm,c+gw+2),"block is missing depth corners");
     int ax=r.Byte(),ay=r.Byte();Require(ax<aw/bs&&ay<ah/bs,"atlas coordinate out of range");
    }
   }
   Require(sumCorners==totalCorners&&sumBlocks==totalBlocks,"slice sums differ from header totals");
   Require(r.Position==co,"geometry end differs from geometryBytes");
   // DrawScene's contain-fit aspect is the original photo's aspect. Match its float intermediates.
   float aspect=(float)((double)ow/oh);double tanHalf=oh/(2.0*focal)*Math.Min(1.0,((double)ow/oh)/aspect);
   float scale=(float)(1/tanHalf),nearZ=Math.Max(1e-4f,(float)(minZ*.001f)),farZ=Math.Max(100f,(float)(maxZ*100f));
   float range=(float)(nearZ-farZ),product=(float)(nearZ*farZ);
   Require(Positive(scale)&&Positive((float)(scale/aspect))&&Finite(farZ)&&range!=0&&Finite(product)&&Finite((float)(farZ/range))&&Finite((float)(product/range)),"camera projection is not finite");
   Jpeg(r,co,cb,aw,ah,"color",out info.ColorWidth,out info.ColorHeight);
   Jpeg(r,ao,ab,aw,ah,"alpha",out info.AlphaWidth,out info.AlphaHeight);
   info.ValidationBytes=r.Allocated;summary=info;return true;
  }catch(InvalidDataException e){why=e.Message;}catch(IOException e){why=e.Message;}catch(OverflowException){why="arithmetic overflow";}catch(ArgumentException e){why=e.Message;}catch(NotSupportedException e){why=e.Message;}catch(ObjectDisposedException e){why=e.Message;}
  return false;
 }
 static bool Dimension(long n){return n>=1&&n<=MaxDimension;}
 static long U32(byte[] b,int n){return (long)b[n]|((long)b[n+1]<<8)|((long)b[n+2]<<16)|((long)b[n+3]<<24);}
 static bool Bit(byte[] mask,long n){return (mask[(int)(n/8)]&(1<<(int)(n%8)))!=0;}
 // Only build diagnostic strings on failure: a long JPEG metadata chain must not allocate per marker.
 static void JpegRequire(bool ok,string name,string why){if(!ok)throw new InvalidDataException(name+" JPEG "+why);}
 static void Jpeg(Reader r,long offset,long length,long expectedW,long expectedH,string name,out int width,out int height){
  width=height=0;r.Limit=checked(offset+length);r.Seek(offset);
  JpegRequire(length>=2&&r.Byte()==255&&r.Byte()==216,name,"SOI missing");
  while(r.Position<r.Limit){
   JpegRequire(r.Byte()==255,name,"marker missing");int marker;do{marker=r.Byte();}while(marker==255);
   JpegRequire(marker!=0&&marker!=216&&marker!=217&&marker!=218&&!(marker>=208&&marker<=215),name,"SOF missing");
   if(marker==1)continue;int size=r.BigWord();JpegRequire(size>=2,name,"segment length invalid");long end=checked(r.Position+size-2);
   JpegRequire(end<=r.Limit,name,"segment truncated");
   bool sof=marker>=192&&marker<=207&&marker!=196&&marker!=200&&marker!=204;
   if(sof){
    JpegRequire(size>=8,name,"SOF truncated");int precision=r.Byte();height=r.BigWord();width=r.BigWord();int components=r.Byte();
    JpegRequire(precision>0&&components>=1&&components<=4&&size==8+3*components,name,"SOF layout invalid");
    JpegRequire(Dimension(width)&&Dimension(height)&&Product(width,height)<=MaxAtlasPixels,name,"pixel budget exceeded");
    JpegRequire(width==expectedW&&height==expectedH,name,"dimensions differ from atlas");r.Seek(end);return;
   }
   r.Seek(end);
  }
  throw new InvalidDataException(name+" JPEG SOF missing");
 }
 sealed class Reader {
  readonly Stream stream;readonly Action<long> allocating;readonly byte[] scalar;internal long Limit,Allocated;
  internal long Position{get{return stream.Position;}}
  internal Reader(Stream s,Action<long> observer){stream=s;allocating=observer;Limit=s.Length;scalar=Allocate(4);}
  internal byte[] Allocate(long bytes){long next=checked(Allocated+bytes);Require(bytes>=0&&next<=MaxValidationBytes,"validator allocation budget exceeded");if(allocating!=null)allocating(next);Allocated=next;return new byte[checked((int)bytes)];}
  void Need(long bytes){Require(bytes>=0&&Position<=Limit&&bytes<=Limit-Position,"truncated section");}
  internal void Seek(long position){Require(position>=0&&position<=Limit,"section seek out of range");stream.Position=position;}
  internal void Read(byte[] bytes){Need(bytes.Length);int n=0;while(n<bytes.Length){int got=stream.Read(bytes,n,bytes.Length-n);Require(got>0,"truncated stream");n+=got;}}
  internal byte[] Bytes(int size){var bytes=Allocate(size);Read(bytes);return bytes;}
  internal int Byte(){Need(1);int b=stream.ReadByte();Require(b>=0,"truncated stream");return b;}
  internal int BigWord(){return (Byte()<<8)|Byte();}
  internal long UInt(){Read(scalar);return U32(scalar,0);}
  internal float Float(){Read(scalar);return BitConverter.ToSingle(scalar,0);}
  internal void Mask(byte[] bytes,long count,long expected){
   Read(bytes);long set=0;foreach(byte b in bytes){int v=b;while(v!=0){set++;v&=v-1;}}
   Require(count%8==0||(bytes[bytes.Length-1]>>(int)(count%8))==0,"mask padding bits are set");
   Require(set==expected,"mask popcount differs from slice count");
  }
 }
}
