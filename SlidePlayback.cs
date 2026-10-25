using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

partial class App {
 IDisposable currentDisplay,previousDisplay;
 internal static string DisplayCacheDir{get{return Path.Combine(Dir,"display-cache");}}
 void ReleaseDisplayPins(){if(currentDisplay!=null)currentDisplay.Dispose();if(previousDisplay!=null)previousDisplay.Dispose();currentDisplay=previousDisplay=null;}
 long slideRequest;SlideJob slideJob,aheadJob;SlideSnapshot currentImage;SpatialIdentity.Pair shownPair;
 System.Windows.Forms.Timer verifyTimer;bool currentMismatch;
 readonly HashSet<string> resetHints=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
 readonly HashSet<string> artifactRechecks=new HashSet<string>(StringComparer.OrdinalIgnoreCase);bool verificationRefresh;
 readonly SpatialRejections rejections=new SpatialRejections();SpatialLoad spatialLoad;string currentFailedDigest;internal Task rejectionWork;
 internal Action<SpatialVerification> beforeVerifyDelivery; // tests can hold a completed read before delivery
 internal Action<SlideSnapshot,SpatialVerification> presentSlide; // tests replace only the desktop/native sink
 string HintFor(string path){SpatialCatalog.Photo p;return spatialCatalog!=null&&spatialCatalog.Photos.TryGetValue(path,out p)&&p.Status==SpatialCatalog.State.Hinted?p.Digest:null;}
 SpatialIdentity.Pair PairFor(string path){SpatialCatalog.Photo p;
  return spatialEnabled&&!spatialFlat&&!spatialPaused&&spatialCatalog!=null&&spatialCatalog.Photos.TryGetValue(path,out p)&&p.Status==SpatialCatalog.State.Hinted&&p.Pair!=null&&!rejections.Contains(SpatialContent.Digest(p.Pair))?p.Pair:null;
 }
 static bool SamePair(SpatialIdentity.Pair a,SpatialIdentity.Pair b){return a==null?b==null:b!=null&&a.SourceSha==b.SourceSha&&a.RecipeSha==b.RecipeSha&&a.Artifact==b.Artifact&&SpatialContent.Digest(a)==SpatialContent.Digest(b);}
 internal IPhotoFileSystem prefetchFiles=new PhotoFileSystem();
 SlideJob MakeSlideJob(string path,SlideSnapshot reuse,bool prefetch=false){
  var job=new SlideJob(++slideRequest,path,BackgroundMode,AmbientScreen,PairFor(path),HintFor(path));var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
  var colors=new Dictionary<string,uint>(overrides,StringComparer.OrdinalIgnoreCase);
  Action changed=()=>{try{if(dispatcher.HasShutdownStarted){job.Dispose();return;}dispatcher.BeginInvoke(new Action(()=>{if(ReferenceEquals(job,slideJob))SlideProgress(job);else if(!ReferenceEquals(job,aheadJob))job.Dispose();}));}catch{job.Dispose();}};
  job.Start(()=>{var image=reuse==null?new SlideSnapshot(path,prefetch?PhotoReadPolicy.Read(prefetchFiles,path):File.ReadAllBytes(path)):reuse.Copy();try{image.MakeDisplay(DisplayCacheDir);uint color;
    image.Color=colors.TryGetValue(path,out color)||colors.TryGetValue(image.Display,out color)?color:AutoColor(image.Display);return image;}catch{image.Dispose();throw;}},
   image=>{string backdrop;image.Wallpaper=WallpaperImage(image,job.Mode,job.Screen,out backdrop);image.Backdrop=backdrop;},changed,verified=>{if(job.Cancelled)return;var image=verified.Image;if((verified.Pair!=null||verified.ArtifactInvalid)&&image.Wallpaper==image.Display){image.MakeDisplay(DisplayCacheDir,true);image.Wallpaper=image.Display;}
    if(beforeVerifyDelivery!=null)beforeVerifyDelivery(verified);});
  return job;
 }
 void BeginSlide(string path,bool keepTimer,SlideSnapshot reuse=null){
  if(slideJob!=null)slideJob.Dispose();slideJob=null;StopVerifyTimer();
  currentSourcePath=path;showingEmpty=false;shownFlat=spatialFlat;currentMismatch=false;currentFailedDigest=null;verificationRefresh=false;
  if(!keepTimer)nextSlide=DateTime.Now.AddMilliseconds(settings.Interval);
  var pair=PairFor(path);
  if(reuse==null&&aheadJob!=null&&aheadJob.Path==path&&aheadJob.Mode==BackgroundMode&&aheadJob.Screen==AmbientScreen&&SamePair(aheadJob.Candidate,pair)&&aheadJob.HintDigest==HintFor(path)){
   slideJob=aheadJob;aheadJob=null;slideJob.Deadline=DateTime.UtcNow.AddSeconds(2);
  }else{if(aheadJob!=null){aheadJob.Dispose();aheadJob=null;}slideJob=MakeSlideJob(path,reuse);}
  var job=slideJob;verifyTimer=new System.Windows.Forms.Timer{Interval=2000};verifyTimer.Tick+=delegate{SlideProgress(job);if(!CurrentSlideJob(job)||job.Finished||job.Presented)StopVerifyTimer();else if(verifyTimer!=null)verifyTimer.Interval=10;};verifyTimer.Start();SlideProgress(job);
 }
 void StopVerifyTimer(){if(verifyTimer!=null){verifyTimer.Stop();verifyTimer.Dispose();verifyTimer=null;}}
 void CancelSlideRequests(){StopVerifyTimer();if(slideJob!=null)slideJob.Dispose();if(aheadJob!=null)aheadJob.Dispose();slideJob=aheadJob=null;shownPair=null;spatialLoad=null;}
 bool CurrentSlideJob(SlideJob job){return ReferenceEquals(job,slideJob)&&!job.Cancelled&&currentSourcePath==job.Path;}
 void SlideProgress(SlideJob job){
  if(!CurrentSlideJob(job)){job.Dispose();return;}
  if(job.Finished)return;
  if(job.Done){StopVerifyTimer();job.Finished=true;var result=job.Take();
   if(result==null){if(job.Error!=null)Log("Slide preparation: "+job.Error.Message);return;}
   try{
    if(result.Pair!=null&&(!CurrentSlideJob(job)||!spatialEnabled||spatialFlat||spatialPaused||!SamePair(result.Pair,PairFor(job.Path)))){result.Dispose();result.Pair=null;}
    PresentSnapshot(result.Image,result,!job.Presented);job.Presented=true;
    if(result.SourceMismatch||result.ArtifactInvalid)HealSpatial(result);
   }finally{result.Dispose();}
   PrepareNextAmbient();return;
  }
  if(!job.Presented&&DateTime.UtcNow>=job.Deadline&&job.Image!=null){PresentSnapshot(job.Image,null,true);job.Presented=true;}
 }
 void PresentSnapshot(SlideSnapshot image,SpatialVerification verified,bool first){
  var pin=image.PinDisplay();if(previousDisplay!=null)previousDisplay.Dispose();previousDisplay=currentDisplay;currentDisplay=pin;
  bool newPhoto=currentImage==null||currentImage.Source!=image.Source;currentImage=image;
  bool spatial=verified!=null&&verified.Pair!=null&&verified.HasHandle;
  string shown=image.ShowPath(spatial,verified!=null);string backdrop=verified==null?null:image.Backdrop;uint color=image.Color;
  if(verified!=null&&verified.ArtifactInvalid){if(presentSlide==null&&first)PanelWallpaperChanged(image.Display,color,180,newPhoto);FlatSpatialFailure(image);return;}
  if(presentSlide!=null){presentSlide(image,spatial?verified:null);shownPair=spatial?verified.Pair:null;shownColor=color;return;}
  fade.Stop();shownColor=color;if(first)PanelWallpaperChanged(image.Display,color,1600,newPhoto);
  if(spatial){if(!StartSpatial(shown,verified,color,backdrop))spatial=false;}
  else{
   FlushUnderneath();bool same=spatialDesktop!=null&&string.Equals(wall.GetWallpaper(wall.GetMonitorDevicePathAt(0)),shown,StringComparison.OrdinalIgnoreCase);CloseSpatial(true);
   if(same){wall.SetBackgroundColor(color);last=Key(shown);}else if(!StartBlend(shown,color)){wall.SetBackgroundColor(color);wall.SetWallpaper(null,shown);last=Key(shown);}
  }
  shownPair=spatial?verified.Pair:null;UpdateTray();
 }
 void HealSpatial(SpatialVerification result){
  currentMismatch=result.SourceMismatch; // A changed source is reconsidered on the next request, after its hint is rebuilt.
  if(result.SourceMismatch){resetHints.Add(result.Image.Source);if(spatialCatalog!=null)spatialCatalog.DropHint(result.Image.Source);spatialFiles.Remove(result.Image.Source);spatialSlides.Remove(result.Image.Source);}
  if(result.ArtifactInvalid&&result.Candidate!=null)RejectSpatial(result.Candidate,result.ArtifactDigest??SpatialContent.Digest(result.Candidate),result.ContentFailure,result.Image.Source,result.ArtifactReason);
  if(result.ArtifactInvalid&&!result.ContentFailure){artifactRechecks.Add(result.Candidate.Artifact);verificationRefresh=true;currentFailedDigest=null;}
  if(spatialCatalog!=null&&catalogEntries!=null)ApplySpatialCatalog(spatialCatalog,catalogEntries,false);
  LoadSpatialCatalog();
 }
 // Only the current request changes. A catalogue refresh never rebuilds the rotation or resets its countdown.
 void ReevaluateCurrentSpatial(){
  if(currentSourcePath==null||currentMismatch||verificationRefresh)return;var pair=PairFor(currentSourcePath);
  if(pair!=null&&currentFailedDigest!=null&&currentFailedDigest==SpatialContent.Digest(pair))return;
  if(SamePair(shownPair,pair)&&(pair!=null||slideJob==null||slideJob.Finished))return;
  if(slideJob!=null&&!slideJob.Cancelled&&!slideJob.Finished&&SamePair(slideJob.Candidate,pair))return;
  if(pair==null&&currentImage!=null&&currentImage.Source==currentSourcePath){if(slideJob!=null)slideJob.Dispose();slideJob=null;StopVerifyTimer();
   using(var flat=new SpatialVerification(++slideRequest,currentImage,null))PresentSnapshot(currentImage,flat,false);return;}
  BeginSlide(currentSourcePath,true,currentImage!=null&&currentImage.Source==currentSourcePath&&(pair==null||pair.SourceSha==currentImage.Digest)?currentImage:null);
 }
 // Install the current snapshot before CloseSpatial composes its fade target. Never flush an old pending
 // timer here: a failed verification may be for a photo newer than that pending native navigation.
 void FlatSpatialFailure(SlideSnapshot image){
  fade.Stop();string path=image.ShowPath(true,true);shownPair=null;spatialLoad=null;shownColor=image.Color;
  try{SetUnderneath(path,image.Color);last=Key(path);}catch(Exception e){Log("Spatial fallback wallpaper: "+e.Message);}
  finally{CloseSpatial(true,180);}
  if(presentSlide!=null)presentSlide(image,null);else UpdateTray();
 }
 void SpatialLoadFailed(SpatialLoad load,int kind,NativeSpatial.FailureReason reason,string detail){
  if(!load.Matches(spatialLoad,slideJob==null?-1:slideJob.Id)||slideJob.Cancelled||currentSourcePath!=load.Image.Source)return;
  FlatSpatialFailure(load.Image); // Callback owner released the failed lease before invoking us.
  RejectSpatial(load.Pair,load.Digest,kind==NativeSpatial.LoadError,load.Image.Source,reason+": "+detail);
  if(spatialCatalog!=null&&catalogEntries!=null)ApplySpatialCatalog(spatialCatalog,catalogEntries,false);
 }
 void RejectSpatial(SpatialIdentity.Pair pair,string digest,bool content,string source,string reason){
  currentFailedDigest=digest;rejections.Failed(digest,content,SpatialBad.For(pair,source,reason));
  if(!content||digest==null)return;
  if(aheadJob!=null&&SpatialContent.Digest(aheadJob.Candidate)==digest){aheadJob.Dispose();aheadJob=null;}
  if(spatialCatalog!=null)spatialCatalog.SuppressPairs(rejections.Content.Keys);
  if(panel!=null&&page=="spatialstore")RefreshStoreView();
  string store=SpatialCacheDir;var records=new Dictionary<string,SpatialBad.Record>(rejections.Content,StringComparer.Ordinal);
  rejectionWork=Task.Run(()=>{try{SpatialBad.Save(store,records);SpatialBad.Quarantine(store,Log);}catch(Exception e){Log("Spatial rejection cache: "+e.Message);}});
 }
 void PrepareNextSlide(){
  if(slideFiles.Count==0)return;string next=slideAhead.Count>0?slideAhead[slideAhead.Count-1]:slideFiles[slideIndex%slideFiles.Count];
  if(next==currentSourcePath)return; // Do not retain an old snapshot of a single-photo rotation across edits.
  if(aheadJob!=null)aheadJob.Dispose();aheadJob=MakeSlideJob(next,null,true);
 }
}
