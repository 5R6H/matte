using System;
using System.Collections.Generic;
using System.Linq;

// Cursor is the next candidate, including Count (the explicit end of this round).
// A merge never starts a new round or permutes existing entries.
internal static class RotationMerge {
 internal static void Apply(List<string> files,ref int cursor,IEnumerable<string> ready,bool sequential,Comparison<string> compare,Random random){
  if(cursor<0||cursor>files.Count)throw new ArgumentOutOfRangeException("cursor");
  var paths=ready.Distinct(StringComparer.OrdinalIgnoreCase).ToList();var wanted=new HashSet<string>(paths,StringComparer.OrdinalIgnoreCase);
  int before=files.Take(cursor).Count(p=>!wanted.Contains(p));files.RemoveAll(p=>!wanted.Contains(p));cursor-=before;
  var known=new HashSet<string>(files,StringComparer.OrdinalIgnoreCase);
  foreach(string path in paths){if(!known.Add(path))continue;int at;
   if(sequential){at=files.FindIndex(p=>compare(path,p)<0);if(at<0)at=files.Count;}
   else at=random.Next(cursor,files.Count+1);
   files.Insert(at,path);if(sequential&&at<cursor)cursor++;
  }
 }
}

partial class App {
 bool rotationEstablished,holdInventoryDisplay;long slideRound;InventorySnapshot rotationInventory;
 readonly HashSet<string> failedThisRound=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
 List<string> InventoryReady(InventorySnapshot snapshot){
  var scanned=new HashSet<string>(snapshot.Entries.Where(e=>e.Scanned).SelectMany(e=>e.Members.Select(p=>p.Path)),StringComparer.OrdinalIgnoreCase);
  return snapshot.ReadyPaths.Where(p=>snapshot.Scanned||scanned.Contains(p)).ToList();
 }
 InventoryPhoto InventoryPhotoFor(string path){return photoInventory==null?null:photoInventory.Snapshot.Entries.SelectMany(e=>e.Members).FirstOrDefault(p=>string.Equals(p.Path,path,StringComparison.OrdinalIgnoreCase));}
 bool SourceReady(string path){if(photoInventory==null)return true;var photo=InventoryPhotoFor(path);return photo!=null&&photo.State==PhotoState.Ready;}
 bool HistoryMember(string path){return photoInventory==null?slideFiles.Contains(path,StringComparer.OrdinalIgnoreCase):InventoryPhotoFor(path)!=null;}
 bool InventoryEmpty(){return photoInventory==null||photoInventory.Snapshot.Scanned&&photoInventory.Snapshot.Entries.All(e=>e.Members.Count==0&&e.Availability.Values.All(a=>a!=InventoryAvailability.Unknown));}
 void AcceptRotationInventory(InventorySnapshot snapshot){rotationEstablished=true;rotationInventory=snapshot;failedThisRound.Clear();slideRound++;}
 void OnInventoryChanged(InventoryChange change){
  if(inventoryRefresh>=0){CompleteInventoryRefresh();return;}
  if(!rotationEstablished)return;MergeInventory(change.Snapshot);
 }
 void MergeInventory(InventorySnapshot snapshot){
  if(rotationInventory!=null&&(snapshot.Revision<rotationInventory.Revision||snapshot.Sequence<=rotationInventory.Sequence))return;
  var delta=new InventoryChange(rotationInventory??new InventorySnapshot(0,0,new InventoryEntry[0]),snapshot);
  var affected=new HashSet<string>(delta.Removed.Concat(delta.Changed),StringComparer.OrdinalIgnoreCase);
  var members=new HashSet<string>(snapshot.Entries.SelectMany(e=>e.Members).Select(p=>p.Path),StringComparer.OrdinalIgnoreCase);
  var ready=InventoryReady(snapshot);var previous=new HashSet<string>(slideFiles,StringComparer.OrdinalIgnoreCase);
  if(currentImage!=null&&(affected.Contains(currentImage.Source)||!members.Contains(currentImage.Source)))holdInventoryDisplay=true;
  InvalidateInventoryJobs();
  RotationMerge.Apply(slideFiles,ref slideIndex,ready,Sequential,PhotoComparison(),random);
  slideBack.RemoveAll(p=>!members.Contains(p));slideAhead.RemoveAll(p=>!members.Contains(p));
  rotationInventory=snapshot;
  // Hints for unchanged sources survive; same-path replacements must be hashed again even with identical stamps.
  foreach(string path in delta.Changed)resetHints.Add(path);
  foreach(string path in delta.Removed)resetHints.Remove(path);
  if(delta.Added.Count>0||delta.Removed.Count>0||delta.Changed.Count>0||!previous.SetEquals(ready))QueueInventoryCatalog();
  if(panel!=null)RefreshCoverStack();
  if(showingEmpty&&slideFiles.Count>0)AdvanceSlide();
 }
 string TakeRotationCandidate(bool allowNewRound){
  if(slideFiles.Count==0)return null;
  if(slideIndex==slideFiles.Count){if(!allowNewRound)return null;slideRound++;slideIndex=0;failedThisRound.Clear();if(!Sequential)ArrangeRotation(slideFiles);}
  return slideFiles[slideIndex++];
 }
 void RetryNextRound(){nextSlide=DateTime.Now.AddMilliseconds(settings.Interval);}
}
