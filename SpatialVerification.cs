using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

// One source read per request. Verified underlays and decoded composites come from this snapshot.
sealed class SlideSnapshot:IDisposable {
 readonly Func<byte[],BitmapSource> decode;
 internal readonly string Source,Digest;internal readonly byte[] Bytes;internal string Display,Wallpaper,Backdrop;internal uint Color;
 internal SlideSnapshot(string source,byte[] bytes,Func<byte[],BitmapSource> decoder=null){Source=source;Bytes=bytes;Digest=Hash(bytes);decode=decoder;}
 internal static string Hash(byte[] bytes){using(var sha=SHA256.Create())return Hex(sha.ComputeHash(bytes));}
 internal static string Hash(Stream stream){using(var sha=SHA256.Create())return Hex(sha.ComputeHash(stream));}
 static string Hex(byte[] bytes){return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
 internal BitmapSource Pixels(){if(decode!=null)return decode(Bytes);using(var stream=new MemoryStream(Bytes,false)){
  var frame=BitmapDecoder.Create(stream,BitmapCreateOptions.IgnoreColorProfile,BitmapCacheOption.OnLoad).Frames[0];
  var result=App.UprightWpf(frame,App.Orientation(frame));result.Freeze();return result;
 }}
 internal System.Drawing.Bitmap Decode(){using(var stream=new MemoryStream()){var encoder=new BmpBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(Pixels()));encoder.Save(stream);stream.Position=0;using(var image=System.Drawing.Image.FromStream(stream))return new System.Drawing.Bitmap(image);}}
 readonly object displayGate=new object();DisplayCache.Lease displayLease;
 internal SlideSnapshot Copy(){return new SlideSnapshot(Source,Bytes,decode);}
 internal string ShowPath(bool spatial,bool prepared){string display=spatial||Source.EndsWith(".webp",StringComparison.OrdinalIgnoreCase)?Display:Source;return prepared&&Wallpaper!=null&&Wallpaper!=Display?Wallpaper:display;}
 internal IDisposable PinDisplay(){lock(displayGate)return Display==Source?null:DisplayCache.Pin(Display);}
 public void Dispose(){lock(displayGate){if(displayLease!=null){displayLease.Dispose();displayLease=null;}}}
 internal string MakeDisplay(string folder,bool verifiedUnderlay=false){
  bool webp=Source.EndsWith(".webp",StringComparison.OrdinalIgnoreCase);
  if(!webp&&!verifiedUnderlay){Display=Source;return Display;}
  string ext=webp?".jpg":Path.GetExtension(Source).ToLowerInvariant();
  var lease=DisplayCache.Acquire(folder,Digest+"-d3"+ext,temp=>{
    if(webp){var encoder=new JpegBitmapEncoder{QualityLevel=96};encoder.Frames.Add(BitmapFrame.Create(Pixels()));using(var stream=File.Create(temp))encoder.Save(stream);}else File.WriteAllBytes(temp,Bytes);
   });lock(displayGate){if(displayLease!=null)displayLease.Dispose();displayLease=lease;Display=lease.Path;return Display;}
 }

}

sealed class SpatialVerification:IDisposable {
 internal readonly long Request;internal readonly SlideSnapshot Image;internal readonly SpatialIdentity.Pair Candidate;
 internal SpatialIdentity.Pair Pair;internal bool SourceMismatch,ArtifactInvalid;FileStream artifact;
 internal SpatialVerification(long request,SlideSnapshot image,SpatialIdentity.Pair candidate){Request=request;Image=image;Candidate=candidate;}
 internal IDisposable TakeSceneLease(){var held=TakeHandle();if(held==null)return null;try{return new SceneLease(held,Image.PinDisplay());}catch{held.Dispose();throw;}}
 sealed class SceneLease:IDisposable {IDisposable artifact,display;internal SceneLease(IDisposable a,IDisposable d){artifact=a;display=d;}public void Dispose(){if(artifact!=null){artifact.Dispose();artifact=null;}if(display!=null){display.Dispose();display=null;}}}
 internal FileStream TakeHandle(){var h=artifact;artifact=null;return h;}
 internal bool HasHandle{get{return artifact!=null;}}
 public void Dispose(){var h=TakeHandle();if(h!=null)h.Dispose();}
 internal static SpatialVerification Check(long request,SlideSnapshot image,SpatialIdentity.Pair candidate,string hintedDigest=null){
  var result=new SpatialVerification(request,image,candidate);string expectedSource=hintedDigest??(candidate==null?null:candidate.SourceSha);
  if(expectedSource!=null&&image.Digest!=expectedSource){result.SourceMismatch=true;return result;}if(candidate==null)return result;
  try{
   // Keep the sidecar fixed while classifying and reading its expected artifact hash. Hold the artifact through native use.
   using(var side=new FileStream(candidate.Artifact+".json",FileMode.Open,FileAccess.Read,FileShare.Read)){
    SpatialIdentity.Pair pair;if(SpatialIdentity.Classify(candidate.Artifact+".json",false,out pair)!=SpatialIdentity.Classification.Valid||pair.SourceSha!=candidate.SourceSha||pair.RecipeSha!=candidate.RecipeSha)throw new IOException("sidecar changed");
    string text;using(var reader=new StreamReader(side,new UTF8Encoding(false,true),false,4096,true))text=reader.ReadToEnd();
    var metadata=SpatialIdentity.ValidateSidecar(SpatialIdentity.ParseStrict(text));var expected=(Dictionary<string,object>)metadata["artifact"];
    result.artifact=new FileStream(pair.Artifact,FileMode.Open,FileAccess.Read,FileShare.Read);
    if(result.artifact.Length!=long.Parse(expected["size"].ToString(),System.Globalization.CultureInfo.InvariantCulture)||SlideSnapshot.Hash(result.artifact)!=(string)expected["sha256"])throw new IOException("artifact hash mismatch");
    result.Pair=pair;
   }
  }catch(Exception){result.ArtifactInvalid=true;result.Dispose();}return result;
 }
}

// The native loader is serial. TransitionDone(n) means scenes before n have left the screen, including interrupted fades.
sealed class SpatialLeases:IDisposable {
 readonly Dictionary<int,IDisposable> held=new Dictionary<int,IDisposable>();
 internal void Add(int sequence,IDisposable lease){held.Add(sequence,lease);}
 internal void NativeEvent(int kind,int sequence){if(kind==NativeSpatial.TransitionDone)TransitionDone(sequence);else if(kind==NativeSpatial.LoadError)Failed(sequence);}
 internal void TransitionDone(int sequence){foreach(int n in held.Keys.Where(n=>n<sequence).ToArray())Failed(n);}
 internal void Failed(int sequence){IDisposable h;if(held.TryGetValue(sequence,out h)){held.Remove(sequence);h.Dispose();}}
 public void Dispose(){foreach(int n in held.Keys.ToArray())Failed(n);}
}

// A cancelled/abandoned request always disposes a late result, even when the UI no longer accepts callbacks.
sealed class SlideJob:IDisposable {
 static readonly SemaphoreSlim workers=new SemaphoreSlim(2);readonly object gate=new object();bool cancelled,done;SpatialVerification result;SlideSnapshot image;
 internal readonly long Id;internal readonly string Path,Mode,HintDigest;internal readonly SpatialIdentity.Pair Candidate;internal readonly System.Drawing.Size Screen;
 internal DateTime Deadline;internal bool Presented,Finished;internal Exception Error;
 internal SlideJob(long id,string path,string mode,System.Drawing.Size screen,SpatialIdentity.Pair pair,string hintDigest=null){HintDigest=hintDigest;Id=id;Path=path;Mode=mode;Screen=screen;Candidate=pair;Deadline=DateTime.UtcNow.AddSeconds(2);}
 internal SlideSnapshot Image{get{lock(gate)return image;}}
 internal bool Cancelled{get{lock(gate)return cancelled;}}
 internal bool Done{get{lock(gate)return done;}}
 internal void Start(Func<SlideSnapshot> capture,Action<SlideSnapshot> prepare,Action changed,Action<SpatialVerification> beforeDelivery){
  Task.Run(()=>{workers.Wait();SlideSnapshot snap=null;try{if(Cancelled)return;snap=capture();lock(gate){if(cancelled)return;image=snap;}changed();if(Cancelled)return;
    prepare(snap);if(Cancelled)return;var verified=SpatialVerification.Check(Id,snap,Candidate,HintDigest);
    try{if(beforeDelivery!=null)beforeDelivery(verified);}catch{verified.Dispose();throw;}
    lock(gate){if(cancelled){verified.Dispose();return;}result=verified;done=true;}changed();
   }catch(Exception e){lock(gate){Error=e;done=true;}changed();}finally{lock(gate){if(cancelled&&snap!=null)snap.Dispose();}workers.Release();}});
 }
 internal SpatialVerification Take(){lock(gate){var r=result;result=null;return r;}}
 public void Dispose(){lock(gate){cancelled=true;if(result!=null){result.Dispose();result=null;}if(done&&image!=null)image.Dispose();image=null;}}
}
