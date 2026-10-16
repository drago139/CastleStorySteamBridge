using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
class LanguageTests {
 static int checks;
 static void Check(bool condition,string name){checks++;if(!condition)throw new Exception(name);}
 static object Call(Type type,string method,Type[] parameters,params object[] args){return type.GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic,null,parameters,null).Invoke(null,args);}
 static int Main(string[] args) {
  try {
   Assembly app=Assembly.LoadFrom(args[0]);Type languages=app.GetType("AppLanguage"),setup=app.GetType("Setup"),choice=app.GetType("LanguageChoice");
   CultureInfo ui=Thread.CurrentThread.CurrentUICulture,regional=Thread.CurrentThread.CurrentCulture;
   Check((string)Call(languages,"Resolve",new[]{typeof(string),typeof(string)},"auto","uk-UA")=="uk-UA","Automatic Ukrainian Windows");
   foreach(string locale in new[]{"en-US","en-GB","en-CA","en-AU","fr-FR","fr-CA"})Check((string)Call(languages,"Resolve",new[]{typeof(string),typeof(string)},"auto",locale)==locale,"Automatic exact region "+locale);
   Check((string)Call(languages,"Resolve",new[]{typeof(string),typeof(string)},"auto","de-DE")=="de-DE","German Windows resolves to German");
   Array choices=(Array)languages.GetField("Choices",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);HashSet<string> codes=new HashSet<string>();Check(choices.Length==31,"Full 31-language Steam catalog");
   foreach(object item in choices){string code=(string)choice.GetField("Code",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(item);Check(codes.Add(code),"Unique locale "+code);Check(!CultureInfo.GetCultureInfo(code).IsNeutralCulture,"Exact region "+code);}
   Type catalog=app.GetType("LanguageCatalog");
   foreach(object item in choices) {
    string code=(string)choice.GetField("Code",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(item);
    languages.GetField("RegionSelection",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,code);Call(languages,"Use",new[]{typeof(string)},code);
    Check((string)Call(languages,"Resolve",new[]{typeof(string),typeof(string)},"auto",code)==code,"Exact Windows detection "+code);
    Check((bool)Call(catalog,"Supported",new[]{typeof(string)},code),"Complete pack "+code);
    using(Stream resource=app.GetManifestResourceStream("ui-localization.tsv"))using(StreamReader reader=new StreamReader(resource)){
     string row;while((row=reader.ReadLine())!=null){if(row.Length==0||row.StartsWith("#"))continue;string original=row.Split('\t')[0].Replace("\\n","\n");string translated=(string)Call(languages,"Text",new[]{typeof(string)},original);
      Check(!String.IsNullOrEmpty(translated),"Nonempty "+code);
      System.Text.RegularExpressions.MatchCollection sourceSlots=System.Text.RegularExpressions.Regex.Matches(original,@"\{\d+\}");
      foreach(System.Text.RegularExpressions.Match slot in sourceSlots)Check(translated.Contains(slot.Value),"Placeholder preserved "+code+" "+original);
      bool sharedWord=((code=="ru-RU"||code=="bg-BG")&&original=="Готово")||(code=="bg-BG"&&original=="Castle Story — програма {0}");
      if(code!="uk-UA"&&!sharedWord)Check(translated!=original,"No Ukrainian fallback "+code+" "+original);
     }
    }
   }
   foreach(string locale in new[]{"uk-UA","en-US","fr-CA","fr-FR"}) {
    languages.GetField("RegionSelection",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,locale);Call(languages,"Use",new[]{typeof(string)},locale);
    string translated=(string)Call(languages,"Text",new[]{typeof(string)},"Застосувати мову гри");
    Check(translated==(locale=="uk-UA"?"Застосувати мову гри":locale.StartsWith("en")?"Apply game language":"Appliquer la langue du jeu"),"Accurate UI text "+locale);
    Check(Thread.CurrentThread.CurrentCulture.Name==locale,"Manual regional formats "+locale);
   }
   languages.GetField("RegionSelection",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,"auto");Call(languages,"Use",new[]{typeof(string)},"auto");Check(Thread.CurrentThread.CurrentUICulture.Name==(string)Call(languages,"Resolve",new[]{typeof(string),typeof(string)},"auto",ui.Name),"Automatic retains original Windows language after manual switches");
   Check(Thread.CurrentThread.CurrentCulture.Name==regional.Name,"Automatic retains original Windows regional formats");
   string root=Path.Combine(Path.GetTempPath(),"CastleStoryLanguages_"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.Combine(root,"Info","Languages"));Directory.CreateDirectory(Path.Combine(root,"Info","UserSettings"));Directory.CreateDirectory(Path.Combine(root,"Castle Story_Data","Managed"));File.WriteAllText(Path.Combine(root,"Castle Story.exe"),"fixture");
   File.WriteAllText(Path.Combine(root,"Info","Languages","master.csv"),"Keys,English (Canada),\"French (Canada)\",\nkey,hello,bonjour,\n");
   string local=Path.Combine(root,"Info","Languages","MexicanSpanish");Directory.CreateDirectory(local);File.WriteAllText(Path.Combine(local,"meta.json"),"{\"__LANGUAGE_NAME\":\"Español (México)\",\"__LANGUAGE_CODE\":\"es-MX\"}");File.WriteAllText(Path.Combine(local,"language.csv"),"Keys,Español (México),\nkey,hola,\n");
   object available=Call(languages,"GameLanguages",new[]{typeof(string)},root);Check((int)available.GetType().GetProperty("Count").GetValue(available,null)==4,"All installed languages plus bundled Ukrainian");
   MethodInfo resolve=languages.GetMethod("ResolveGame",BindingFlags.Static|BindingFlags.NonPublic);
   foreach(string locale in new[]{"uk-UA","en-GB","fr-FR","es-MX"}){
    object[] values={available,"auto",locale,false};object chosen=resolve.Invoke(null,values);string code=(string)choice.GetField("Code",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(chosen);Check(code==(locale=="en-GB"?"en-CA":locale=="fr-FR"?"fr-CA":locale),"Available game language matching "+locale);
   }
   object[] unsupported={available,"auto","de-DE",false};object fallback=resolve.Invoke(null,unsupported);Check((bool)unsupported[3]&&(string)choice.GetField("Code",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(fallback)=="en-CA","Missing game translation explicitly falls back");
   string json="{\"network\":{\"Name\":\"Friend\",\"Port\":3000},\"graphic\":{\"fullscreen\":false},\"localization\":{\"language\":\"Українська\",\"other\":123},\"keep\":42}";
   foreach(string name in new[]{"Current.json","Default.json"})File.WriteAllText(Path.Combine(root,"Info","UserSettings",name),json);
   string sentinel=Path.Combine(root,"save-preserved.txt");File.WriteAllText(sentinel,"world");
   string backup=(string)Call(setup,"ApplyLanguage",new[]{typeof(string),typeof(string),typeof(bool)},root,"French (Canada)",false);
   JavaScriptSerializer parser=new JavaScriptSerializer();Dictionary<string,object> result=parser.DeserializeObject(File.ReadAllText(Path.Combine(root,"Info","UserSettings","Current.json"))) as Dictionary<string,object>;
   Check((string)((Dictionary<string,object>)result["localization"])["language"]=="French (Canada)","Exact I2 game language name selected");
   Check((int)((Dictionary<string,object>)result["localization"])["other"]==123&&(int)result["keep"]==42,"Other JSON fields preserved");
   Check(File.ReadAllText(Path.Combine(backup,"Info","UserSettings","Current.json"))==json,"Original settings backed up");Check(File.ReadAllText(sentinel)=="world","Save data preserved");
   File.WriteAllText(Path.Combine(root,"Info","UserSettings","Default.json"),"broken JSON");string prior=File.ReadAllText(Path.Combine(root,"Info","UserSettings","Current.json"));bool refused=false;try{Call(setup,"ApplyLanguage",new[]{typeof(string),typeof(string),typeof(bool)},root,"English (Canada)",false);}catch(TargetInvocationException){refused=true;}
   Check(refused&&File.ReadAllText(Path.Combine(root,"Info","UserSettings","Current.json"))==prior,"Invalid settings refused before partial language change");
   languages.GetField("RegionSelection",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,"pt-BR");Call(languages,"Use",new[]{typeof(string)},"pt-PT");
   Check(Thread.CurrentThread.CurrentUICulture.Name=="pt-BR","Brazil country selects Brazilian Portuguese");
   string brazil=(string)Call(languages,"Text",new[]{typeof(string)},"Не вдалося зберегти вибір мови.");
   languages.GetField("RegionSelection",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,"pt-PT");Call(languages,"Use",new[]{typeof(string)},"pt-PT");
   Check(brazil!=(string)Call(languages,"Text",new[]{typeof(string)},"Не вдалося зберегти вибір мови."),"Portuguese country changes save wording");
   Console.WriteLine("PASS: "+checks+" language and country checks; "+choices.Length+" language choices. Fixture: "+root);return 0;
  }catch(Exception e){Console.WriteLine(e);return 1;}
 }
}
