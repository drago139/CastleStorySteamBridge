using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
internal static class LanguageCatalog {
 internal static readonly LanguageChoice[] Languages={
  new LanguageChoice("Українська","uk-UA"),new LanguageChoice("English","en-US"),new LanguageChoice("Français","fr-FR"),new LanguageChoice("Русский","ru-RU"),
  new LanguageChoice("Polski","pl-PL"),new LanguageChoice("Deutsch","de-DE"),new LanguageChoice("Čeština","cs-CZ"),new LanguageChoice("Español (España)","es-ES"),new LanguageChoice("Español (Latinoamérica)","es-MX"),
  new LanguageChoice("Português (Portugal)","pt-PT"),new LanguageChoice("Português (Brasil)","pt-BR"),new LanguageChoice("Italiano","it-IT"),new LanguageChoice("Türkçe","tr-TR"),
  new LanguageChoice("Български","bg-BG"),new LanguageChoice("Dansk","da-DK"),new LanguageChoice("Nederlands","nl-NL"),new LanguageChoice("Suomi","fi-FI"),
  new LanguageChoice("Ελληνικά","el-GR"),new LanguageChoice("Magyar","hu-HU"),new LanguageChoice("Bahasa Indonesia","id-ID"),new LanguageChoice("日本語","ja-JP"),new LanguageChoice("한국어","ko-KR"),
  new LanguageChoice("Bahasa Melayu","ms-MY"),new LanguageChoice("Norsk","nb-NO"),new LanguageChoice("Română","ro-RO"),new LanguageChoice("Svenska","sv-SE"),
  new LanguageChoice("ไทย","th-TH"),new LanguageChoice("Tiếng Việt","vi-VN"),new LanguageChoice("简体中文","zh-CN"),new LanguageChoice("繁體中文","zh-TW"),new LanguageChoice("العربية","ar-SA")
 };
 // Maps every legacy screen/message string to a complete localized semantic message.
 internal static readonly int[] MessageMap={0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,30,8,31,32,31,33,34,34,34,33,35,36,30,37,38,39,40,41,34,28,8,21,42,42,42,43,34,44,44,44,36,34,36,36,36,36,45,43,36,36,36,45,45,36,34,34,34,46,47,48,49,50,51,52,53,54};
 static readonly Dictionary<string,string[]> packs=Read();
 static Dictionary<string,string[]> Read() {
  using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("ui-packs.json")) {
   if(stream==null)throw new InvalidDataException("Language packs missing");
   using(StreamReader reader=new StreamReader(stream,Encoding.UTF8)) {
    Dictionary<string,string[]> data=new JavaScriptSerializer().Deserialize<Dictionary<string,string[]>>(reader.ReadToEnd());
    foreach(KeyValuePair<string,string[]> item in data){if(item.Value.Length!=55)throw new InvalidDataException("Incomplete language pack: "+item.Key);foreach(string phrase in item.Value)if(String.IsNullOrEmpty(phrase))throw new InvalidDataException("Empty translation: "+item.Key);}
    return data;
   }
  }
 }
 internal static string Pack(string locale) {
  CultureInfo culture;try{culture=CultureInfo.GetCultureInfo(locale);}catch{return "en";}
  string family=culture.TwoLetterISOLanguageName;
  if(family=="zh")return locale.IndexOf("TW",StringComparison.OrdinalIgnoreCase)>=0||locale.IndexOf("HK",StringComparison.OrdinalIgnoreCase)>=0||locale.IndexOf("MO",StringComparison.OrdinalIgnoreCase)>=0||locale.IndexOf("Hant",StringComparison.OrdinalIgnoreCase)>=0?"zh-TW":"zh-CN";
  if(family=="pt")return locale.EndsWith("BR",StringComparison.OrdinalIgnoreCase)?"pt-BR":"pt-PT";
  if(family=="es")return culture.IsNeutralCulture||locale.EndsWith("ES",StringComparison.OrdinalIgnoreCase)?"es-ES":"es-LA";
  if(family=="nb"||family=="nn"||family=="no")return "no";
  return family;
 }
 internal static bool Supported(string locale){string p=Pack(locale);if(p=="uk"||p=="en"||p=="fr")return true;return packs.ContainsKey(p);}
 internal static string Translate(int sourceIndex,string locale) {
  string[] rows;if(!packs.TryGetValue(Pack(locale),out rows)||sourceIndex>=MessageMap.Length)return null;
  if(sourceIndex==49)return rows[50]+"{0}";
  return rows[MessageMap[sourceIndex]].Replace("\\n","\n");
 }
 internal static int SourceForTranslated(string message) {
  foreach(string[] rows in packs.Values)for(int i=0;i<rows.Length;i++)if(rows[i].Replace("\\n","\n")==message){for(int j=0;j<MessageMap.Length;j++)if(MessageMap[j]==i)return j;}
  return -1;
 }
 internal static string Canonical(string locale) {
  string pack=Pack(locale);foreach(LanguageChoice item in Languages)if(Pack(item.Code)==pack)return item.Code;return "en-US";
 }
 internal static List<LanguageChoice> Regions(string locale) {
  string family=CultureInfo.GetCultureInfo(locale).TwoLetterISOLanguageName;List<LanguageChoice> result=new List<LanguageChoice>();
  foreach(CultureInfo c in CultureInfo.GetCultures(CultureTypes.SpecificCultures))if(c.TwoLetterISOLanguageName==family){string name=c.NativeName;try{name=new RegionInfo(c.Name).NativeName;}catch(ArgumentException){}result.Add(new LanguageChoice(name,c.Name));}
  result.Sort(delegate(LanguageChoice a,LanguageChoice b){return StringComparer.CurrentCulture.Compare(a.Name,b.Name);});return result;
 }
}
