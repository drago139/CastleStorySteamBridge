using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class LanguageChoice {
 internal string Name,Code;internal bool Available=true;
 internal LanguageChoice(string name,string code){Name=name;Code=code;}
 public override string ToString(){return Available?Name:Name+" — "+AppLanguage.Text("Переклад гри не встановлений. Завантаж його з Workshop.");}
}
internal static class AppLanguage {
 internal static string Selection="auto", GameSelection="auto",RegionSelection="auto";
 static readonly CultureInfo windowsUi=CultureInfo.CurrentUICulture;
 static readonly CultureInfo windowsRegion=CultureInfo.CurrentCulture;
 internal static CultureInfo WindowsLanguage {get{return windowsUi;}}
 internal static CultureInfo FormatCulture {get{return RegionSelection=="auto"?windowsRegion:CultureInfo.GetCultureInfo(RegionSelection);}}
 internal static string Locale="uk-UA";
 static readonly Dictionary<string,int> sourceIndices=new Dictionary<string,int>();
 static readonly Dictionary<string,string[]> texts=ReadTexts();
 internal static string PreferencesPath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CastleStorySteamBridge","language-preferences.json");}}
 internal static readonly LanguageChoice[] Choices=LanguageCatalog.Languages;
 internal static string Resolve(string choice,string windows) {
  string selected=choice=="auto"?windows:choice;
  try {CultureInfo c=CultureInfo.GetCultureInfo(selected);if(LanguageCatalog.Supported(c.Name))return c.IsNeutralCulture?CultureInfo.CreateSpecificCulture(c.Name).Name:c.Name;}catch(CultureNotFoundException){}
  return "en-US";
 }
 internal static void Initialize() {
  Selection="auto";GameSelection="auto";RegionSelection="auto";
  try{if(File.Exists(PreferencesPath)){Dictionary<string,object> data=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(PreferencesPath)) as Dictionary<string,object>;object value;if(data!=null){if(data.TryGetValue("interface",out value)&&value is string)Selection=(string)value;if(data.TryGetValue("game",out value)&&value is string)GameSelection=(string)value;if(data.TryGetValue("region",out value)&&value is string)RegionSelection=(string)value;else if(Selection!="auto")RegionSelection=Resolve(Selection,WindowsLanguage.Name);}}}catch{}
  if(RegionSelection!="auto")try{CultureInfo.GetCultureInfo(RegionSelection);}catch{RegionSelection="auto";}
  Use(Selection);
 }
 internal static void Use(string selected) {
  Selection=selected;Locale=Resolve(selected,WindowsLanguage.Name);
  if(selected!="auto"&&RegionSelection!="auto"&&CultureInfo.GetCultureInfo(RegionSelection).TwoLetterISOLanguageName==CultureInfo.GetCultureInfo(Locale).TwoLetterISOLanguageName)Locale=RegionSelection;
  // Use the user's language list through CurrentUICulture, not an IP address or country guess.
  CultureInfo culture=CultureInfo.GetCultureInfo(Locale);
  Thread.CurrentThread.CurrentCulture=FormatCulture;Thread.CurrentThread.CurrentUICulture=culture;
 }
 internal static void Save() {
  Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath));
  File.WriteAllText(PreferencesPath,new JavaScriptSerializer().Serialize(new Dictionary<string,string>{{"interface",Selection},{"game",GameSelection},{"region",RegionSelection}}),new UTF8Encoding(false));
 }
 static Dictionary<string,string[]> ReadTexts() {
  Dictionary<string,string[]> result=new Dictionary<string,string[]>();
  using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("ui-localization.tsv")) {
   if(stream==null)return result;
   using(StreamReader reader=new StreamReader(stream,Encoding.UTF8)){string row;int index=0;while((row=reader.ReadLine())!=null){if(row.StartsWith("#")||row.Length==0)continue;string[] cells=row.Split('\t');if(cells.Length!=3)throw new InvalidDataException("Invalid UI language row");for(int i=0;i<3;i++)cells[i]=cells[i].Replace("\\n","\n");result[cells[0]]=cells;sourceIndices[cells[0]]=index++;}}
  }
  return result;
 }
 internal static string Text(string original) {
  if(original==null)return "";
  string[] translated;if(!texts.TryGetValue(original,out translated)) {
   foreach(string[] row in texts.Values)if(row[1]==original||row[2]==original){translated=row;break;}
   if(translated==null){int source=LanguageCatalog.SourceForTranslated(original);if(source>=0)foreach(KeyValuePair<string,int> item in sourceIndices)if(item.Value==source){translated=texts[item.Key];break;}}
   if(translated==null){
    string familyUnknown=CultureInfo.GetCultureInfo(Locale).TwoLetterISOLanguageName;if(familyUnknown=="uk")return original;
    foreach(string[] row in texts.Values)if((row[0].EndsWith(": ")||sourceIndices[row[0]]==82)&&original.StartsWith(row[0],StringComparison.Ordinal)) {
     string tail=original.Substring(row[0].Length);const string unchanged=". Файли гри не змінено.";
     if(tail.EndsWith(unchanged,StringComparison.Ordinal))tail=tail.Substring(0,tail.Length-unchanged.Length)+Text(unchanged);
     string prefix=LanguageCatalog.Translate(sourceIndices[row[0]],Locale)??row[familyUnknown=="fr"?2:1];return prefix+tail;
    }
    return original;
   }
  }
  string family=CultureInfo.GetCultureInfo(Locale).TwoLetterISOLanguageName;
  string expanded=LanguageCatalog.Translate(sourceIndices[translated[0]],Locale);if(expanded!=null)return expanded;
  return translated[family=="uk"?0:family=="fr"?2:1];
 }
 internal static string Format(string original,params object[] args){return String.Format(FormatCulture,Text(original),args);}
 internal static List<LanguageChoice> GameLanguages(string game) {
  List<LanguageChoice> result=new List<LanguageChoice>();
  string root=Path.Combine(game??"","Info","Languages");
  string master=Path.Combine(root,"master.csv");
  if(File.Exists(master)) {
   using(StreamReader r=new StreamReader(master,Encoding.UTF8)){List<string> row=CsvRow(r.ReadLine()??"");for(int i=1;i<row.Count;i++){string name=row[i].Trim();if(name.Length>0)Add(result,name,NativeCode(name));}}
  }
  ReadFolders(root,result);
  string workshop=Path.GetFullPath(Path.Combine(game??"","..","..","workshop","content","227860"));
  ReadFolders(workshop,result);
  // The bundled Ukrainian translation is always available to install.
  Add(result,"Українська","uk-UA");
  return result;
 }
 static void ReadFolders(string root,List<LanguageChoice> choices) {
  if(!Directory.Exists(root))return;
  foreach(string folder in Directory.GetDirectories(root)) {
   string meta=Path.Combine(folder,"meta.json"),csv=Path.Combine(folder,"language.csv");
   if(!File.Exists(meta)||!File.Exists(csv))continue;
   try {
    Dictionary<string,object> data=new JavaScriptSerializer().DeserializeObject(File.ReadAllText(meta)) as Dictionary<string,object>;object name,code;
    if(data!=null&&data.TryGetValue("__LANGUAGE_NAME",out name)&&name is string&&data.TryGetValue("__LANGUAGE_CODE",out code)&&code is string)Add(choices,(string)name,(string)code);
   }catch(ArgumentException){}catch(IOException){}catch(UnauthorizedAccessException){}
  }
 }
 static void Add(List<LanguageChoice> list,string name,string code){foreach(LanguageChoice item in list)if(item.Name==name)return;list.Add(new LanguageChoice(name,code));}
 internal static string NativeCode(string name) {
  if(name=="English (Canada)")return "en-CA";if(name=="French (Canada)")return "fr-CA";
  foreach(LanguageChoice item in Choices)if(String.Equals(item.Name,name,StringComparison.OrdinalIgnoreCase))return item.Code;
  return "";
 }
 internal static LanguageChoice ResolveGame(List<LanguageChoice> available,string selected,string windows,out bool fallback) {
  fallback=false;
  if(selected!="auto"){foreach(LanguageChoice item in available)if(item.Name==selected)return item;fallback=true;foreach(LanguageChoice item in available)if(item.Code=="en-CA")return item;return available.Count>0?available[0]:null;}
  foreach(LanguageChoice item in available)if(String.Equals(item.Code,windows,StringComparison.OrdinalIgnoreCase))return item;
  string family="";try{family=CultureInfo.GetCultureInfo(windows).TwoLetterISOLanguageName;}catch(CultureNotFoundException){}
  foreach(LanguageChoice item in available){if(String.IsNullOrEmpty(item.Code))continue;try{if(CultureInfo.GetCultureInfo(item.Code).TwoLetterISOLanguageName==family)return item;}catch(CultureNotFoundException){}}
  fallback=true;foreach(LanguageChoice item in available)if(item.Code=="en-CA")return item;
  return available.Count>0?available[0]:null;
 }
 internal static List<string> CsvRow(string row) {
  List<string> cells=new List<string>();StringBuilder cell=new StringBuilder();bool quoted=false;
  for(int i=0;i<row.Length;i++){char c=row[i];if(c=='"'){if(quoted&&i+1<row.Length&&row[i+1]=='"'){cell.Append('"');i++;}else quoted=!quoted;}else if(c==','&&!quoted){cells.Add(cell.ToString());cell.Length=0;}else cell.Append(c);}cells.Add(cell.ToString());return cells;
 }
}
