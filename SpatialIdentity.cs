using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// Spatial v1 contract only. No app wiring, writes, retention decisions or playback leases.
static class SpatialIdentity {
 internal enum Classification{Valid,Unsupported,Invalid,Unreadable}
 // Parsed integers retain all digits, without rounding or a platform-dependent integer limit.
 internal sealed class Integer {
  internal readonly string Text;
  internal Integer(string text){if(text==null||!Regex.IsMatch(text,@"\A-?(0|[1-9][0-9]*)\z"))throw Bad("expected integer");Text=text=="-0"?"0":text;}
  public override string ToString(){return Text;}
 }
 internal sealed class Pair {
  internal readonly string SourceSha,RecipeSha,Artifact;
  internal readonly DateTime Created;
  internal readonly Dictionary<string,object> Fields;
  internal Pair(string source,string recipe,Dictionary<string,object> fields,DateTime created,string artifact){
   SourceSha=source;RecipeSha=recipe;Fields=fields;Created=created;Artifact=artifact;}
 }
 static readonly CultureInfo Inv=CultureInfo.InvariantCulture;
 static readonly UTF8Encoding Utf8=new UTF8Encoding(false,true);
 static FormatException Bad(string why){return new FormatException(why);}
 static void Unicode(string s){
  for(int i=0;i<s.Length;i++)if(char.IsHighSurrogate(s[i])){if(++i==s.Length||!char.IsLowSurrogate(s[i]))throw Bad("lone surrogate");}
   else if(char.IsLowSurrogate(s[i]))throw Bad("lone surrogate");
 }
 static string Str(object o){var s=o as string;if(s==null)throw Bad("expected string");Unicode(s);return s;}
 // Ordinal UTF-16 order differs from Unicode code-point order for astral keys versus U+E000..FFFF.
 static int CodePointOrder(string a,string b){
  int i=0,j=0;while(i<a.Length&&j<b.Length){int x=char.ConvertToUtf32(a,i),y=char.ConvertToUtf32(b,j);if(x!=y)return x.CompareTo(y);i+=x>65535?2:1;j+=y>65535?2:1;}
  return (a.Length-i).CompareTo(b.Length-j);
 }
 static string Number(object o){
  var n=o as Integer;if(n!=null)return n.Text;
  if(o is sbyte||o is byte||o is short||o is ushort||o is int||o is uint||o is long||o is ulong)return Convert.ToString(o,Inv);
  throw Bad("expected integer");
 }
 static void Quote(StringBuilder b,string s){
  Unicode(s);b.Append('"');foreach(char c in s){switch(c){
   case '"':b.Append("\\\"");break;case '\\':b.Append("\\\\");break;
   case '\b':b.Append("\\b");break;case '\f':b.Append("\\f");break;case '\n':b.Append("\\n");break;case '\r':b.Append("\\r");break;case '\t':b.Append("\\t");break;
   default:if(c<32||c>126)b.Append("\\u").Append(((int)c).ToString("x4",Inv));else b.Append(c);break;
  }}b.Append('"');
 }
 internal static string Canonical(object value){var b=new StringBuilder();Write(b,value,0);return b.ToString();}
 static void Write(StringBuilder b,object value,int depth){
  if(depth>128)throw Bad("JSON too deep");
  if(value is string){Quote(b,(string)value);return;}
  var obj=value as IDictionary<string,object>;
  if(obj!=null){var keys=obj.Keys.ToList();foreach(var k in keys)Str(k);keys.Sort(CodePointOrder);b.Append('{');bool first=true;
   foreach(string k in keys){if(!first)b.Append(',');first=false;Quote(b,k);b.Append(':');Write(b,obj[k],depth+1);}b.Append('}');return;}
  var array=value as IList;if(array!=null){b.Append('[');for(int i=0;i<array.Count;i++){if(i>0)b.Append(',');Write(b,array[i],depth+1);}b.Append(']');return;}
  b.Append(Number(value));
 }
 // JSON booleans/null retain their types here, so schemas can reject them and future envelopes can be retained.
 // Canonical accepts neither. Fractional/exponent numbers are rejected even before schema validation.
 internal static object ParseStrict(string text){if(text==null)throw Bad("missing JSON");Unicode(text);return new Parser(text).Parse();}
 sealed class Parser {
  readonly string s;int p;
  internal Parser(string text){s=text;}
  void Space(){while(p<s.Length&&(s[p]==' '||s[p]=='\t'||s[p]=='\r'||s[p]=='\n'))p++;}
  bool Take(char c){Space();if(p<s.Length&&s[p]==c){p++;return true;}return false;}
  void Need(char c){if(!Take(c))throw Bad("expected "+c);}
  internal object Parse(){var v=Value(0);Space();if(p!=s.Length)throw Bad("trailing data");return v;}
  object Value(int depth){
   if(depth>128)throw Bad("JSON too deep");Space();if(p==s.Length)throw Bad("missing value");char c=s[p];
   if(c=='"')return String();
   if(c=='{'){p++;var d=new Dictionary<string,object>(StringComparer.Ordinal);if(Take('}'))return d;
    do{Space();string key=String();Need(':');object v=Value(depth+1);if(d.ContainsKey(key))throw Bad("duplicate key");d[key]=v;}while(Take(','));Need('}');return d;}
   if(c=='['){p++;var a=new List<object>();if(Take(']'))return a;do{a.Add(Value(depth+1));}while(Take(','));Need(']');return a;}
   foreach(string word in new[]{"true","false","null"})if(p+word.Length<=s.Length&&s.Substring(p,word.Length)==word){p+=word.Length;return word=="null"?null:(object)(word=="true");}
   int start=p;if(c=='-')p++;if(p==s.Length||s[p]<'0'||s[p]>'9')throw Bad("expected integer");
   if(s[p]=='0')p++;else while(p<s.Length&&s[p]>='0'&&s[p]<='9')p++;
   if(p<s.Length&&(s[p]=='.'||s[p]=='e'||s[p]=='E'||(s[p]>='0'&&s[p]<='9')))throw Bad("only decimal integers allowed");
   return new Integer(s.Substring(start,p-start));
  }
  string String(){
   if(p>=s.Length||s[p++]!='"')throw Bad("expected string");var b=new StringBuilder();
   while(p<s.Length){char c=s[p++];if(c=='"'){string v=b.ToString();Unicode(v);return v;}if(c<32)throw Bad("unescaped control");
    if(c!='\\'){b.Append(c);continue;}if(p==s.Length)throw Bad("unfinished escape");c=s[p++];
    switch(c){case '"':case '\\':case '/':b.Append(c);break;case 'b':b.Append('\b');break;case 'f':b.Append('\f');break;
     case 'n':b.Append('\n');break;case 'r':b.Append('\r');break;case 't':b.Append('\t');break;
     case 'u':if(p+4>s.Length)throw Bad("unfinished unicode escape");int n=0;for(int i=0;i<4;i++){char h=s[p++];int x=h>='0'&&h<='9'?h-'0':h>='a'&&h<='f'?h-'a'+10:h>='A'&&h<='F'?h-'A'+10:-1;if(x<0)throw Bad("bad unicode escape");n=(n<<4)|x;}b.Append((char)n);break;
     default:throw Bad("bad escape");}
   }throw Bad("unterminated string");
  }
 }
 internal static string Sha256(byte[] bytes){using(var sha=SHA256.Create())return Hex(sha.ComputeHash(bytes));}
 static string Hex(byte[] bytes){return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
 static Dictionary<string,object> Obj(object o,string keys){
  var d=o as Dictionary<string,object>;string[] names=keys.Split(' ');
  if(d==null||d.Count!=names.Length||names.Any(k=>!d.ContainsKey(k)))throw Bad("incorrect object fields");return d;
 }
 static long Int(object o,long min,long max){long n;if(!long.TryParse(Number(o),NumberStyles.AllowLeadingSign,Inv,out n)||n<min||n>max)throw Bad("integer out of range");return n;}
 static string Digest(object o){string s=Str(o);if(!Regex.IsMatch(s,@"\A[0-9a-f]{64}\z"))throw Bad("invalid SHA-256");return s;}
 internal static Dictionary<string,object> ValidateRecipe(object value){
  var d=Obj(value,"exporter generator height model upright width");
  if(!Regex.IsMatch(Str(d["exporter"]),@"\Aspatial-photos/(0|[1-9][0-9]*)\z")||!Regex.IsMatch(Str(d["generator"]),@"\Amatte-sharp/(0|[1-9][0-9]*)\z"))throw Bad("invalid revision");
  Int(d["width"],64,16384);Int(d["height"],64,16384);Digest(d["model"]);if(Str(d["upright"])!="exif-v1")throw Bad("invalid upright");return d;
 }
 internal static string RecipeSha256(object fields){return Sha256(Utf8.GetBytes(Canonical(ValidateRecipe(fields))));}
 internal static bool Supported(object fields){try{var d=ValidateRecipe(fields);return (string)d["generator"]=="matte-sharp/1"&&(string)d["exporter"]=="spatial-photos/1";}catch(FormatException){return false;}}
 internal static string ArtifactPath(string store,string sourceSha,string recipeSha){return Path.Combine(Path.GetFullPath(store),"v1",Digest(sourceSha),Digest(recipeSha)+".spatial");}
 internal static string SidecarPath(string store,string sourceSha,string recipeSha){return ArtifactPath(store,sourceSha,recipeSha)+".json";}
 internal static string TempName(string final){var bytes=new byte[8];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(bytes);return final+".tmp-"+Hex(bytes);}
 internal static string QuarantinePath(string store,string sourceSha,string recipeSha){return QuarantinePath(store,sourceSha,recipeSha,DateTime.UtcNow);}
 internal static string QuarantinePath(string store,string sourceSha,string recipeSha,DateTime now){
  if(now.Kind!=DateTimeKind.Utc)throw Bad("UTC timestamp required");
  return Path.Combine(Path.GetFullPath(store),"v1","quarantine",now.ToString("yyyyMMddTHHmmssZ",Inv)+"-"+Digest(sourceSha)+"-"+Digest(recipeSha)+".spatial");
 }
 internal static bool StoreTooLong(string store){try{string d=new string('0',64);return Math.Max((SidecarPath(store,d,d)+".tmp-"+new string('0',16)).Length,QuarantinePath(store,d,d).Length)>259;}catch(PathTooLongException){return true;}}
 internal static Dictionary<string,object> ValidateSidecar(object value){
  var d=Obj(value,"version source recipe artifact created");Int(d["version"],1,1);
  var source=Obj(d["source"],"sha256 size name path");var recipe=Obj(d["recipe"],"sha256 fields");var artifact=Obj(d["artifact"],"file sha256 size");
  Digest(source["sha256"]);Digest(recipe["sha256"]);Digest(artifact["sha256"]);Int(source["size"],0,long.MaxValue);Int(artifact["size"],0,long.MaxValue);Str(source["name"]);Str(source["path"]);
  if(RecipeSha256(recipe["fields"])!=(string)recipe["sha256"])throw Bad("recipe digest mismatch");
  if(!Supported(recipe["fields"]))throw Bad("unsupported recipe");
  if(Str(artifact["file"])!=(string)recipe["sha256"]+".spatial")throw Bad("artifact filename mismatch");
  Created(d);return d;
 }
 static DateTime Created(Dictionary<string,object> d){DateTime t;string s=Str(d["created"]);
  if(!DateTime.TryParseExact(s,"yyyy-MM-dd'T'HH:mm:ss'Z'",Inv,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out t))throw Bad("invalid created");return t;}
 internal static string SidecarJson(object value){return Canonical(ValidateSidecar(value));}
 internal static Classification Classify(string sidecar,bool verifyArtifact){Pair pair;return Classify(sidecar,verifyArtifact,out pair);}
 internal static Classification Classify(string sidecar,bool verifyArtifact,out Pair pair){
  pair=null;
  try{
   string path=Path.GetFullPath(sidecar);object parsed=ParseStrict(Utf8.GetString(File.ReadAllBytes(path)));var envelope=parsed as Dictionary<string,object>;object version;
   if(envelope!=null&&envelope.TryGetValue("version",out version)&&version is Integer&&Number(version)!="1")return Classification.Unsupported;
   var d=ValidateSidecar(parsed);var source=(Dictionary<string,object>)d["source"];var recipe=(Dictionary<string,object>)d["recipe"];var artifact=(Dictionary<string,object>)d["artifact"];
   string parent=Path.GetDirectoryName(path);
   if(Path.GetFileName(path)!=(string)artifact["file"]+".json"||Path.GetFileName(parent)!=(string)source["sha256"]||Path.GetFileName(Path.GetDirectoryName(parent))!="v1")return Classification.Invalid;
   string file=Path.Combine(parent,(string)artifact["file"]);
   try{using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read)){
    if(stream.Length!=Int(artifact["size"],0,long.MaxValue))return Classification.Invalid;
    if(verifyArtifact)using(var sha=SHA256.Create())if(Hex(sha.ComputeHash(stream))!=(string)artifact["sha256"])return Classification.Invalid;
   }}catch(FileNotFoundException){return Classification.Invalid;}catch(DirectoryNotFoundException){return Classification.Invalid;}
   pair=new Pair((string)source["sha256"],(string)recipe["sha256"],(Dictionary<string,object>)recipe["fields"],Created(d),file);return Classification.Valid;
  }catch(IOException){return Classification.Unreadable;}catch(UnauthorizedAccessException){return Classification.Unreadable;}
   catch(System.Security.SecurityException){return Classification.Unreadable;}catch(FormatException){return Classification.Invalid;}
   catch(ArgumentException){return Classification.Invalid;}catch(NotSupportedException){return Classification.Invalid;}
 }
 internal static Pair Select(string sourceSha,string desiredRecipe,IEnumerable<Pair> pairs){
  Digest(sourceSha);if(desiredRecipe!=null)Digest(desiredRecipe);
  return pairs.Where(p=>p!=null&&p.SourceSha==sourceSha&&Supported(p.Fields))
   .OrderByDescending(p=>p.RecipeSha==desiredRecipe).ThenByDescending(p=>p.Created).ThenBy(p=>p.RecipeSha,StringComparer.Ordinal).FirstOrDefault();
 }
}
