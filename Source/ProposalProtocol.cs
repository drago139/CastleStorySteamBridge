using System;
using System.IO;
using System.Text;
namespace CastleStorySteam {
 internal sealed class MapOffer { internal ulong Author, Workshop; internal uint OfferId; internal string Code, Name; internal byte Action; }
 internal static class ProposalProtocol {
  internal const int MaxName=96;
  internal static bool SameOffer(MapOffer a,MapOffer b) { return a!=null&&b!=null&&a.OfferId==b.OfferId&&a.Workshop==b.Workshop&&a.Code==b.Code; }
  internal static string Clean(string text) {
   StringBuilder b=new StringBuilder(); foreach(char c in text??"") { if(!Char.IsControl(c)&&c!='<'&&c!='>'&&c!='/'&&c!='\\') b.Append(c); if(b.Length>=MaxName) break; } return b.ToString().Trim();
  }
  internal static byte[] Encode(MapOffer offer) {
   using(MemoryStream stream=new MemoryStream()) using(BinaryWriter w=new BinaryWriter(stream,Encoding.UTF8)) {
    w.Write((byte)1); w.Write(offer.Action); w.Write(offer.Author); w.Write(offer.Workshop); w.Write(offer.OfferId); w.Write(Clean(offer.Code)); w.Write(Clean(offer.Name)); return stream.ToArray();
   }
  }
  internal static bool Decode(byte[] payload,out MapOffer offer) {
   offer=null; if(payload==null||payload.Length<25||payload.Length>800) return false;
   try { using(MemoryStream stream=new MemoryStream(payload)) using(BinaryReader r=new BinaryReader(stream,new UTF8Encoding(false,true))) {
    if(r.ReadByte()!=1) return false; MapOffer o=new MapOffer(); o.Action=r.ReadByte(); o.Author=r.ReadUInt64(); o.Workshop=r.ReadUInt64(); o.OfferId=r.ReadUInt32(); o.Code=r.ReadString(); o.Name=r.ReadString();
    if(stream.Position!=stream.Length||o.Action>3||o.Author==0||o.OfferId==0||o.Name.Length==0||o.Name.Length>MaxName||o.Code.Length>MaxName||Clean(o.Name)!=o.Name||Clean(o.Code)!=o.Code||(o.Workshop==0&&o.Code.Length==0)) return false; offer=o; return true;
   }} catch(Exception e) { if(e is IOException||e is ArgumentException||e is FormatException) return false; throw; }
  }
 }
}
