using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using CastleStorySteam;
class BootstrapLauncher {
 static byte[] Resource(string name){using(Stream source=Assembly.GetExecutingAssembly().GetManifestResourceStream(name))using(MemoryStream target=new MemoryStream()){source.CopyTo(target);return target.ToArray();}}
 [STAThread] static int Main(string[] args){try{byte[] app=Resource("desktop.app"),sig=Resource("desktop.sig");if(!DesktopRelease.Verify(BridgeVersion.Text,app,sig))throw new IOException("Підпис вбудованої програми пошкоджений.");if(args.Length>0&&args[0]=="--self-test"){string file=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"CastleStory-Steam-App-SelfTest.exe");File.WriteAllBytes(file,app);File.WriteAllBytes(file+".sig",sig);using(Process child=Process.Start(new ProcessStartInfo(file,"--self-test"){UseShellExecute=false})){child.WaitForExit();return child.ExitCode;}}string current=DesktopRelease.Store(DesktopRelease.CacheRoot,BridgeVersion.Text,app,sig),latest=DesktopRelease.Latest(DesktopRelease.CacheRoot,BridgeVersion.Text);Process.Start(new ProcessStartInfo(latest??current,args.Length>0?DesktopRelease.Quote(args[0]):""){UseShellExecute=false});return 0;}catch(Exception e){MessageBox.Show(e.Message,"Castle Story Steam Bridge",MessageBoxButtons.OK,MessageBoxIcon.Error);return 1;}}
}
