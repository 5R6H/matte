using System;
using System.IO;
using System.Collections.Generic;

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
 void LoadSpatialCatalog(){
  spatialFiles.Clear();spatialSlides.Clear();
  try{
   string cache=SpatialCacheDir;
   if(!Directory.Exists(cache))return;
   // .spatial files are keyed by file name only; a name used by two different images could show the wrong depth,
   // so such names get no Spatial at all.
   var sources=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);var clashes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(string source in SourceImages()){
    string stem=Path.GetFileNameWithoutExtension(source);
    if(sources.ContainsKey(stem))clashes.Add(stem);else sources[stem]=source;
   }
   foreach(string stem in clashes)sources.Remove(stem);if(clashes.Count>0)Log("Spatial skipped for "+clashes.Count+" file name(s) used by two different photos");
   foreach(string map in Directory.GetFiles(cache,"*.spatial",SearchOption.TopDirectoryOnly)){
    string stem=Path.GetFileNameWithoutExtension(map);string source;
    if(!sources.TryGetValue(stem,out source)||spatialFiles.ContainsKey(source))continue;
    spatialSlides.Add(source);spatialFiles[source]=map;
   }
  }catch(Exception e){Log("Spatial catalog: "+e.Message);}
 }
 // A Spatial file older than its photo (rotated or flipped since, in 调整图片 or elsewhere) shows the old picture, so
 // it does not count until 生成 Spatial 图片 makes it again (the generator remakes exactly these).
 string SpatialPathFor(string path){string map;if(!spatialFiles.TryGetValue(path,out map)||!File.Exists(map))return null;
  try{if(!path.StartsWith(Path.Combine(Dir,"display-cache"),StringComparison.OrdinalIgnoreCase)&&File.Exists(path)&&File.GetLastWriteTimeUtc(path)>File.GetLastWriteTimeUtc(map).AddSeconds(2))return null;}catch{}
  return map;}
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
