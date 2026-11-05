using System.Text;
using Threadbook.Desktop;
using Threadbook.Storage;
class Program {
 static int Main(string[] args) { try {
 if(args.Length!=1 || !Path.IsPathFullyQualified(args[0]) || Directory.Exists(args[0])) throw new Exception("New absolute fixture required");
 Directory.CreateDirectory(args[0]); int n=0; void Check(bool b,string s){if(!b)throw new Exception(s);n++;Console.WriteLine("PASS "+s);}
 using var owner=new JsonStore(Path.Combine(args[0],"plain")); bool allowed=true; var prefs=new PaneSizePreferenceStore(owner,()=>allowed);
 Check(prefs.Load().State.Layouts.Count==0,"missing defaults");
 var state=new PaneSizeState(1,new(){["Original"]=new(){["Sidebar"]=280,["Feed"]=420}});
 Check(prefs.Save(state).Success,"save");Check(new PaneSizePreferenceStore(owner,()=>true).Load().State.Layouts["Original"]["Feed"]==420,"persisted reload");
 var before=owner.Files.ReadStored("pane-sizes.json",65536);allowed=false;Check(!prefs.Save(state).Success && before.SequenceEqual(owner.Files.ReadStored("pane-sizes.json",65536)),"host guard preserves bytes"); allowed=true;
 foreach(var bad in new[]{"null","{broken","{\"SchemaVersion\":2,\"Layouts\":{}}","{\"SchemaVersion\":1,\"SchemaVersion\":1,\"Layouts\":{}}","{\"SchemaVersion\":1,\"Layouts\":null}","{\"SchemaVersion\":1,\"Layouts\":{\"Original\":{\"Feed\":null}}}","{\"SchemaVersion\":1,\"Layouts\":{\"Original\":{\"Other\":300}}}"}) {
 var bytes=Encoding.UTF8.GetBytes(bad); Check(owner.Files.WriteAtomic("pane-sizes.json",bytes,65536,false).Success,"fixture write");var p=new PaneSizePreferenceStore(owner,()=>true);Check(p.Load().Warning!=null && !p.Save(state).Success && bytes.SequenceEqual(owner.Files.Read("pane-sizes.json",65536)),"invalid preserved and blocked");Check(p.Reset().Success,"explicit reset");Check(owner.Files.List("ui-preferences.preserved-").Any(k=>owner.Files.Read(k,65536).SequenceEqual(bytes)),"preservation bytes"); }
 using var secure=JsonStore.CreateEncrypted(Path.Combine(args[0],"encrypted"),"synthetic-pane-password".AsSpan());var ep=new PaneSizePreferenceStore(secure,()=>true);Check(ep.Save(state).Success,"encrypted save");var cipher=secure.Files.ReadStored("pane-sizes.json",65536);Check(!Encoding.UTF8.GetString(cipher).Contains("Sidebar"),"no plaintext");secure.Lock();Check(!ep.Save(state).Success,"locked reject");secure.Unlock("synthetic-pane-password".AsSpan());Check(ep.Load().State.Layouts["Original"]["Sidebar"]==280,"encrypted roundtrip");
 foreach(var value in new[]{double.NaN,double.PositiveInfinity,199,4097}) {try{PaneSizePreferenceStore.Validate(new(1,new(){["Original"]=new(){["Sidebar"]=value}}));throw new Exception("invalid accepted");}catch(System.Text.Json.JsonException){n++;}}
 Console.WriteLine($"PASS {n} pane preference assertions");return 0;
 }catch(Exception e){Console.Error.WriteLine(e);return 1;} }
}
