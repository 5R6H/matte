using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

class SpatialRecord { public string source {get;set;} public string cache {get;set;} }
class SpatialManifest { public SpatialRecord[] records {get;set;} }
partial class App {
 bool spatialEnabled;
 Dictionary<string,string> spatialFiles=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
 List<string> spatialSlides=new List<string>();
 void LoadSpatialMode(){
  string state=Path.Combine(Dir,"spatial-enabled.txt");
  spatialEnabled=File.Exists(state)&&File.ReadAllText(state).Trim()=="1";
  LoadSpatialCatalog();
 }
 SpatialCatalog spatialCatalog;int catalogRequest;bool catalogLoading;
 readonly SpatialCatalog.RecipeCache desiredRecipe=new SpatialCatalog.RecipeCache();
 static readonly object catalogGate=new object(); // One hashing worker, including superseded requests.
 void LoadSpatialCatalog(){
  int request=System.Threading.Interlocked.Increment(ref catalogRequest);catalogLoading=true;
  spatialFiles.Clear();spatialSlides.Clear();spatialCatalog=null;spatialNeeds=null;
  string store=SpatialCacheDir;string[] entries=PlaylistEntries().ToArray();
  bool installed=!CustomGenerator&&SharpInstalled,describe=panel!=null;
  var screen=System.Windows.Forms.Screen.PrimaryScreen.Bounds;var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
  Func<bool> cancelled=()=>System.Threading.Volatile.Read(ref catalogRequest)!=request;
  Action<Action> post=a=>{if(!dispatcher.HasShutdownStarted)dispatcher.BeginInvoke(a);};
  Task.Run(()=>{lock(catalogGate){
   if(cancelled())return;
   try{
    string desired=describe?desiredRecipe.Resolve(SharpModel,screen.Width,screen.Height,installed,()=>SpatialCatalog.Describe(OwnGenerator)):null;
    if(cancelled())return;
    var per=SpatialPhotoEntries(entries);var photos=per.SelectMany(x=>x.Item2).ToList();
    var hinted=SpatialCatalog.Load(store,photos,desired);
    post(()=>{if(!cancelled())ApplySpatialCatalog(hinted,per,false);});
    var complete=hinted.Complete(cancelled);
    post(()=>{if(!cancelled())ApplySpatialCatalog(complete,per,true);});
   }catch(Exception e){Log("Spatial catalog: "+e.Message);post(()=>{if(!cancelled())catalogLoading=false;});}
  }});
 }
 void ApplySpatialCatalog(SpatialCatalog catalog,List<Tuple<string,List<string>>> per,bool complete){
  spatialCatalog=catalog;catalogLoading=!complete;spatialFiles.Clear();spatialSlides.Clear();
  foreach(var kv in catalog.Photos)if(kv.Value.Status==SpatialCatalog.State.Hinted&&kv.Value.Pair!=null){spatialSlides.Add(kv.Key);spatialFiles[kv.Key]=kv.Value.Pair.Artifact;}
  spatialNeeds=SpatialNeedsByEntry(catalog,per);spatialCacheBytes=catalog.Bytes;
  int files=catalog.Valid+catalog.Legacy;spatialFileBytes=files>=3?catalog.Bytes/(double)files:3.6e6;
  if(panel!=null){RefreshSpatial(false);if(page=="sharp")PaintSharp();if(page=="spatialstore")PaintCatalogStore();}
 }
 // Probable catalogue entries only. No source/artifact hash here is an attach-time verification.
 string SpatialPathFor(string path){string map;return spatialFiles.TryGetValue(path,out map)?map:null;}
 void PaintCatalogStore(){if(panel==null)return;P<System.Windows.Controls.TextBlock>("StoreTotal").Text=spatialCatalog==null?"正在统计…":spatialCatalog.StoreText()+" · "+SizeText(spatialCatalog.Bytes).Replace("约 ","");}
 void SetSpatialEnabled(bool enabled){
  bool previous=spatialEnabled;
  try{
   // on is allowed with no 3D photos yet: it is the preference; photos without a Spatial file just play flat
   LoadSpatialCatalog();
   if(spatialEnabled==enabled)return;
   spatialEnabled=enabled;Theme.AtomicWrite(Path.Combine(Dir,"spatial-enabled.txt"),enabled?"1":"0");
   fade.Stop();UseSlideshow(currentSourcePath);
   Log("Spatial mode "+(enabled?"on":"off")+"; slides="+slideFiles.Count);
  }catch(Exception e){spatialEnabled=previous;Theme.AtomicWrite(Path.Combine(Dir,"spatial-enabled.txt"),previous?"1":"0");Log("Spatial toggle: "+e.Message);}
 }
}
