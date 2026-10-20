using System.Drawing;
using System.IO;
using System.Text;

// Small shared helpers.
static class Theme {
 public static string Hex(Color c){return "#"+c.R.ToString("X2")+c.G.ToString("X2")+c.B.ToString("X2");}
 // Writes a file in one step: a crash or power cut mid-write leaves the old contents, never half a file.
 public static void AtomicWrite(string path,string contents){string tmp=path+".tmp";File.WriteAllText(tmp,contents,Encoding.UTF8);if(File.Exists(path))File.Replace(tmp,path,null);else File.Move(tmp,path);}
}
