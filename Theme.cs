using System.Drawing;
using System.IO;
using System.Text;

// Small shared helpers.
static class Theme {
 public static string Hex(Color c){return "#"+c.R.ToString("X2")+c.G.ToString("X2")+c.B.ToString("X2");}
 // Writes a file in one step: a crash or power cut mid-write leaves the old contents, never half a file.
 public static void AtomicWrite(string path,string contents){string tmp=path+".tmp";File.WriteAllText(tmp,contents,Encoding.UTF8);if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);}
}
// The settings file: written whole to a temporary file and swapped in, the version before kept as .bak; read back from
// the .bak when the file itself does not read (cut short by a crash or a power cut, edited by hand), the unreadable one
// kept aside as .bad. Neither reads: null, and Matte starts with fresh settings.
static class SettingsStore {
 // Recovered from the .bak but the damaged file could not be replaced by it (in use, say): the next save writes over the
 // damaged file without keeping it, so the .bak stays the good copy.
 static readonly System.Collections.Generic.HashSet<string> damaged=new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
 public static void Save(string path,string text){
  string tmp=path+".tmp";File.WriteAllText(tmp,text,new UTF8Encoding(false));
  bool keep=!damaged.Contains(path);
  if(File.Exists(path))File.Replace(tmp,path,keep?path+".bak":null);else File.Move(tmp,path);
  damaged.Remove(path);   // only once the damaged file is replaced: a save that failed leaves it marked for the retry
 }
 public static T Load<T>(string path,System.Func<string,T> parse,System.Action<string> log) where T:class{
  foreach(var p in new[]{path,path+".bak"}){
   if(!File.Exists(p))continue;
   try{var v=parse(File.ReadAllText(p));if(v!=null){
     // recovered: the good copy goes back as the file itself, so the next save (which keeps the file it replaces as
     // .bak) keeps a good .bak too, not the damaged file
     if(p!=path){log("Settings: "+Path.GetFileName(path)+" did not read; using the copy before it");try{File.Copy(p,path,true);}catch(System.Exception e){damaged.Add(path);log("Settings: restoring the copy: "+e.Message);}}
     return v;}
    log("Settings: "+Path.GetFileName(p)+" is empty");}
   catch(System.Exception e){log("Settings: "+Path.GetFileName(p)+" does not read: "+e.Message);}
   if(p==path)try{File.Copy(p,path+".bad",true);}catch{}
  }
  return null;
 }
}
