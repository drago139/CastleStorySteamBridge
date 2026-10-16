using System;
internal static class SavePreviewRules {
 const int MaxBytes=32*1024*1024;
 static uint Big(byte[] data,int at) { return ((uint)data[at]<<24)|((uint)data[at+1]<<16)|((uint)data[at+2]<<8)|data[at+3]; }
 internal static bool ValidPng(byte[] data) {
  if(data==null||data.Length<33||data.Length>MaxBytes)return false;
  byte[] signature={137,80,78,71,13,10,26,10};for(int i=0;i<8;i++)if(data[i]!=signature[i])return false;
  if(Big(data,8)!=13||data[12]!=73||data[13]!=72||data[14]!=68||data[15]!=82)return false;
  uint width=Big(data,16),height=Big(data,20);
  if(width==0||height==0||width>8192||height>8192||(ulong)width*height>16777216)return false;
  int offset=8;bool image=false;
  while(offset<=data.Length-12) {
   uint size=Big(data,offset);if(size>(uint)(data.Length-offset-12))return false;
   if(data[offset+4]==73&&data[offset+5]==68&&data[offset+6]==65&&data[offset+7]==84)image=true;
   if(data[offset+4]==73&&data[offset+5]==69&&data[offset+6]==78&&data[offset+7]==68)return image&&size==0&&offset+12==data.Length;
   offset+=(int)size+12;
  }
  return false;
 }
 internal static void Fit(int width,int height,out int w,out int h) {
  if(width<=0||height<=0)throw new ArgumentOutOfRangeException("size");
  double scale=Math.Min(1.0,Math.Min(512.0/width,512.0/height));
  w=Math.Max(1,(int)Math.Round(width*scale));h=Math.Max(1,(int)Math.Round(height*scale));
 }
}
