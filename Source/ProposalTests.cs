using System;using CastleStorySteam;
class ProposalTests {
 static int checks; static void Check(bool ok,string reason){checks++;if(!ok)throw new Exception(reason);}
 static int Main(){try {
  MapOffer offer;MapOffer good=new MapOffer {Author=123,Workshop=55,OfferId=6,Code="we_helix_abc",Name="Зимова карта ✨"};byte[] data=ProposalProtocol.Encode(good);
  Check(ProposalProtocol.Decode(data,out offer)&&offer.Name==good.Name&&offer.Workshop==55&&offer.OfferId==6,"identity roundtrip");
  Check(ProposalProtocol.SameOffer(good,offer),"same map identity");offer.OfferId++;Check(!ProposalProtocol.SameOffer(good,offer),"stale readiness excluded");
  Check(!ProposalProtocol.Decode(null,out offer),"null");Check(!ProposalProtocol.Decode(new byte[8],out offer),"short");
  byte[] bad=(byte[])data.Clone();bad[0]=255;Check(!ProposalProtocol.Decode(bad,out offer),"version");bad=(byte[])data.Clone();bad[1]=4;Check(!ProposalProtocol.Decode(bad,out offer),"action");
  for(int cut=1;cut<data.Length;cut++)Check(!ProposalProtocol.Decode(new ArraySegment<byte>(data,0,cut).ToArray(),out offer),"truncation "+cut);
  bad=new byte[data.Length+1];Buffer.BlockCopy(data,0,bad,0,data.Length);Check(!ProposalProtocol.Decode(bad,out offer),"trailing bytes");
  good.Author=0;Check(!ProposalProtocol.Decode(ProposalProtocol.Encode(good),out offer),"author");good.Author=123;good.OfferId=0;Check(!ProposalProtocol.Decode(ProposalProtocol.Encode(good),out offer),"offer id");good.OfferId=6;
  good.Workshop=0;good.Code="";Check(!ProposalProtocol.Decode(ProposalProtocol.Encode(good),out offer),"map must have identity");good.Code="helix";
  foreach(byte action in new byte[]{0,1,2,3}){good.Action=action;Check(ProposalProtocol.Decode(ProposalProtocol.Encode(good),out offer),"valid action "+action);}
  PeerReportGate gate=new PeerReportGate();Check(gate.Accept(1,0,0,5)&&!gate.Accept(1,0,1,5),"flood");Check(gate.Accept(2,0,1,5)&&gate.Accept(3,0,1,5),"independent peers");
  Console.WriteLine("PASS: "+checks+" map identity, stale readiness and packet checks.");return 0;
 }catch(Exception e){Console.WriteLine(e);return 1;}}
}
static class Seg {internal static byte[] ToArray(this ArraySegment<byte> s){byte[] b=new byte[s.Count];Buffer.BlockCopy(s.Array,s.Offset,b,0,s.Count);return b;}}
