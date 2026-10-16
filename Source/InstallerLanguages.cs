using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using CastleStorySteam;

sealed partial class InstallerForm : Form {
 TextBox path=new TextBox();CheckBox timeout=new CheckBox();Label result=new Label();Button install=new Button();
 ComboBox interfaceLanguage=new ComboBox(),gameLanguage=new ComboBox(),country=new ComboBox();
 Label title=new Label(),interfaceLabel=new Label(),gameLabel=new Label(),languageInfo=new Label(),note=new Label(),footer=new Label(),countryLabel=new Label();
 Button workshopLanguages=new Button();
 Button browse=new Button(),applyLanguage=new Button();bool initializing,isPreview;
 List<LanguageChoice> available=new List<LanguageChoice>();
 static string L(string text){return AppLanguage.Text(text);}
 static string F(string text,params object[] args){return AppLanguage.Format(text,args);}
 public InstallerForm(string initial):this(initial,false){}
 internal InstallerForm(string initial,bool preview) {
  initializing=true;isPreview=preview;AppLanguage.Initialize();
  ClientSize=new Size(770,725);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;Font=new Font("Segoe UI",10);
  title.SetBounds(20,16,690,24);path.SetBounds(20,48,566,28);path.Text=String.IsNullOrEmpty(initial)?Setup.FindGame():initial;
  browse.SetBounds(596,46,114,32);browse.Click+=delegate{using(FolderBrowserDialog dialog=new FolderBrowserDialog()){dialog.Description=L("Оберіть папку, де є Castle Story.exe");if(dialog.ShowDialog(this)==DialogResult.OK)path.Text=dialog.SelectedPath;}};
  interfaceLabel.SetBounds(20,193,235,24);interfaceLanguage.SetBounds(265,188,445,32);interfaceLanguage.DropDownStyle=ComboBoxStyle.DropDownList;
  interfaceLanguage.Items.Add(new LanguageChoice("","auto"));foreach(LanguageChoice choice in AppLanguage.Choices)interfaceLanguage.Items.Add(new LanguageChoice(choice.Name,choice.Code));
  interfaceLanguage.SelectedIndex=0;for(int i=1;i<interfaceLanguage.Items.Count;i++)if(((LanguageChoice)interfaceLanguage.Items[i]).Code==LanguageCatalog.Canonical(AppLanguage.Selection)&&AppLanguage.Selection!="auto")interfaceLanguage.SelectedIndex=i;
  countryLabel.SetBounds(20,236,235,24);country.SetBounds(265,230,445,32);country.DropDownStyle=ComboBoxStyle.DropDownList;RefreshRegions();
  gameLabel.SetBounds(20,239,235,24);gameLanguage.SetBounds(265,234,445,32);gameLanguage.DropDownStyle=ComboBoxStyle.DropDownList;
  languageInfo.SetBounds(20,280,690,57);languageInfo.Font=new Font("Segoe UI",9);
  timeout.SetBounds(20,344,690,30);timeout.Checked=false;note.SetBounds(20,386,690,52);
  applyLanguage.SetBounds(20,447,690,36);applyLanguage.Click+=ApplyLanguageOnly;
  install.SetBounds(20,495,690,44);install.Click+=Run;result.SetBounds(20,548,690,48);footer.SetBounds(20,603,690,48);
  gameLabel.Top+=44;gameLanguage.Top+=44;languageInfo.Top+=44;timeout.Top+=64;note.Top+=64;applyLanguage.Top+=64;install.Top+=64;result.Top+=64;footer.Top+=64;
  workshopLanguages.SetBounds(20,381,690,28);workshopLanguages.Click+=delegate{Process.Start("https://steamcommunity.com/app/227860/workshop/");};
  Controls.AddRange(new Control[]{title,path,browse,interfaceLabel,interfaceLanguage,countryLabel,country,gameLabel,gameLanguage,languageInfo,timeout,note,applyLanguage,install,result,footer,workshopLanguages});
  RefreshGameLanguages();InitializeWorkshopUpdates();checkUpdates.SetBounds(20,92,690,38);updateInfo.SetBounds(20,137,690,46);
  interfaceLanguage.SelectedIndexChanged+=delegate{if(initializing)return;string chosen=((LanguageChoice)interfaceLanguage.SelectedItem).Code;AppLanguage.RegionSelection="auto";AppLanguage.Use(chosen);RefreshRegions();SaveChoices();Translate();};
  country.SelectedIndexChanged+=delegate{if(initializing)return;LanguageChoice chosen=country.SelectedItem as LanguageChoice;if(chosen!=null){AppLanguage.RegionSelection=chosen.Code;AppLanguage.Use(AppLanguage.Selection);SaveChoices();Translate();}};
  gameLanguage.SelectedIndexChanged+=delegate{if(initializing)return;SaveChoices();UpdateLanguageInfo();};
  path.TextChanged+=delegate{RefreshGameLanguages();};initializing=false;Translate();
  if(preview){Shown-=OnUpdatesShown;updateTimer.Stop();SetUpdateInfo("Попередній перегляд — файли гри не змінюються.");applyLanguage.Enabled=false;install.Enabled=false;checkUpdates.Enabled=false;}
 }
 void SaveChoices(){LanguageChoice selected=gameLanguage.SelectedItem as LanguageChoice;if(selected!=null)AppLanguage.GameSelection=selected.Code=="auto"||selected.Code=="keep"?selected.Code:(!selected.Available?"locale:"+selected.Code:selected.Name);try{AppLanguage.Save();}catch(Exception e){result.Text=L("Не вдалося зберегти вибір мови.")+" "+e.Message;}}
 void Translate() {
  Text=F("Castle Story — програма {0}",BridgeVersion.Text);title.Text=L("Папка оригінальної Castle Story");browse.Text=L("Обрати…");
  interfaceLabel.Text=L("Мова програми");gameLabel.Text=L("Мова гри");
  countryLabel.Text=L("Країна / регіон");workshopLanguages.Text=L("Переклади гри — Steam Workshop");
  RightToLeft=AppLanguage.Locale.StartsWith("ar",StringComparison.Ordinal)?RightToLeft.Yes:RightToLeft.No;RightToLeftLayout=RightToLeft==RightToLeft.Yes;path.RightToLeft=RightToLeft.No;
  bool previous=initializing;initializing=true;int uiIndex=interfaceLanguage.SelectedIndex,gameIndex=gameLanguage.SelectedIndex;
  interfaceLanguage.Items[0]=new LanguageChoice(L("Автоматично — мова Windows"),"auto");
  if(gameLanguage.Items.Count>0)gameLanguage.Items[0]=new LanguageChoice(L("Автоматично — мова Windows"),"auto");
  if(gameLanguage.Items.Count>1)gameLanguage.Items[1]=new LanguageChoice(L("Не змінювати мову гри"),"keep");
  interfaceLanguage.SelectedIndex=uiIndex;gameLanguage.SelectedIndex=gameIndex;initializing=previous;
  previous=initializing;initializing=true;int countryIndex=country.SelectedIndex;if(country.Items.Count>0)country.Items[0]=new LanguageChoice(L("Автоматично — регіон Windows"),"auto");country.SelectedIndex=countryIndex;initializing=previous;
  timeout.Text=L("Тайм-аут з'єднання: 10 хвилин");note.Text=L("Оновлення перевіряються при відкритті програми. Гру запускати не потрібно.\nПеред заміною файлів Castle Story має бути закрита.");
  applyLanguage.Text=L("Застосувати мову гри");install.Text=L("Встановити та запустити гру");checkUpdates.Text=L("Перевірити оновлення — Steam Workshop");
  footer.Text=L("Після підписки Steam автоматично завантажує релізи мода.\nЦя програма застосовує перевірені оновлення у вибраній папці.");UpdateLanguageInfo();
  if(updateMessageKey!=null)SetUpdateInfo(updateMessageKey,updateMessageArgs);
 }
 void RefreshRegions() {
  bool previous=initializing;initializing=true;
  try{country.Items.Clear();country.Items.Add(new LanguageChoice(L("Автоматично — регіон Windows"),"auto"));foreach(LanguageChoice region in LanguageCatalog.Regions(AppLanguage.Locale))country.Items.Add(region);country.SelectedIndex=0;for(int i=1;i<country.Items.Count;i++)if(((LanguageChoice)country.Items[i]).Code==AppLanguage.RegionSelection)country.SelectedIndex=i;}finally{initializing=previous;}
 }
 void RefreshGameLanguages() {
  bool previous=initializing;initializing=true;
  try {
   available=AppLanguage.GameLanguages(path.Text);gameLanguage.Items.Clear();gameLanguage.Items.Add(new LanguageChoice(L("Автоматично — мова Windows"),"auto"));gameLanguage.Items.Add(new LanguageChoice(L("Не змінювати мову гри"),"keep"));
   List<string> displayed=new List<string>();
   foreach(LanguageChoice offered in LanguageCatalog.Languages){LanguageChoice found=null;foreach(LanguageChoice installed in available)if(!String.IsNullOrEmpty(installed.Code)&&LanguageCatalog.Pack(installed.Code)==LanguageCatalog.Pack(offered.Code)){found=installed;break;}
    if(found!=null){if(!displayed.Contains(found.Name)){displayed.Add(found.Name);gameLanguage.Items.Add(found);}}
    else{LanguageChoice missing=new LanguageChoice(offered.Name,offered.Code);missing.Available=false;gameLanguage.Items.Add(missing);}
   }
   foreach(LanguageChoice choice in available)if(!displayed.Contains(choice.Name))gameLanguage.Items.Add(choice);
   gameLanguage.SelectedIndex=AppLanguage.GameSelection=="keep"?1:0;
   for(int i=2;i<gameLanguage.Items.Count;i++){LanguageChoice item=(LanguageChoice)gameLanguage.Items[i];if(item.Name==AppLanguage.GameSelection||(AppLanguage.GameSelection.StartsWith("locale:")&&!String.IsNullOrEmpty(item.Code)&&LanguageCatalog.Pack(item.Code)==LanguageCatalog.Pack(AppLanguage.GameSelection.Substring(7))))gameLanguage.SelectedIndex=i;}
  }catch(Exception e){result.Text=L("Не вдалося прочитати список мов.")+" "+e.Message;}
  finally{initializing=previous;}UpdateLanguageInfo();
 }
 LanguageChoice ResolvedGame(out bool fallback) {
  LanguageChoice choice=gameLanguage.SelectedItem as LanguageChoice;
  if(choice==null||choice.Code=="keep"){fallback=false;return null;}
  if(!choice.Available){fallback=false;return choice;}
  return AppLanguage.ResolveGame(available,choice.Code=="auto"?"auto":choice.Name,AppLanguage.WindowsLanguage.Name,out fallback);
 }
 void UpdateLanguageInfo() {
  bool fallback;LanguageChoice chosen=ResolvedGame(out fallback);
  languageInfo.Text=F("Windows: {0}. Програма: {1}. Гра: {2}.",AppLanguage.WindowsLanguage.Name,AppLanguage.Locale,chosen==null?L("без змін"):chosen.Name);
  if(fallback)languageInfo.Text+="\n"+L("Мови Windows немає серед встановлених перекладів гри; використано доступну англійську.");
  if(chosen!=null&&!chosen.Available)languageInfo.Text+="\n"+L("Переклад гри не встановлений. Завантаж його з Workshop.");
  applyLanguage.Enabled=!isPreview&&(chosen==null||chosen.Available);
 }
 void ApplyLanguageOnly(object sender,EventArgs e) {
  try {bool fallback;LanguageChoice chosen=ResolvedGame(out fallback);if(chosen==null){result.Text=L("Мову гри залишено без змін.");return;}
   if(!chosen.Available)throw new InvalidOperationException(L("Переклад гри не встановлений. Завантаж його з Workshop."));
   string backup=Setup.ApplyLanguage(path.Text,chosen.Name,true);SaveChoices();result.Text=F("Мову гри змінено на {0}. Резервна копія: {1}",chosen.Name,backup);
  }catch(Exception error){result.Text=L(error.Message);MessageBox.Show(this,L(error.Message),L("Помилка"),MessageBoxButtons.OK,MessageBoxIcon.Error);}
 }
 void Run(object sender,EventArgs e) {
  try {
   if(GameGuard.Running()){MessageBox.Show(this,L("Закрийте Castle Story, потім натисніть цю кнопку знову. Збереження гри не буде перервано."),L("Гра вже відкрита"));return;}
   install.Enabled=false;bool fallback;LanguageChoice chosen=ResolvedGame(out fallback);
   if(chosen!=null&&!chosen.Available)throw new InvalidOperationException(L("Переклад гри не встановлений. Завантаж його з Workshop."));
   string backup=Setup.Install(path.Text,chosen==null?null:chosen.Name,timeout.Checked,true);SaveChoices();result.Text=L("Готово. Запускаємо Castle Story.");
   MessageBox.Show(this,F("Встановлено.\n\nРезервні копії: {0}",backup)+(timeout.Checked?"\n\n"+L("Усі гравці також мають встановити тайм-аут 10 хвилин."):""),L("Готово"),MessageBoxButtons.OK,MessageBoxIcon.Information);Process.Start("steam://rungameid/227860");
  }catch(Exception error){result.Text=L("Встановлення не завершено.");MessageBox.Show(this,L(error.Message),L("Помилка"),MessageBoxButtons.OK,MessageBoxIcon.Error);}
  finally{install.Enabled=true;}
 }
}
