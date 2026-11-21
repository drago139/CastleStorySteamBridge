using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using CastleStorySteam;

class JoinLoadTests
{
    static int checks;
    static void Check(bool yes,string name) { checks++;if(!yes)throw new Exception(name); }
    static void Main()
    {
        System.Threading.Thread.CurrentThread.CurrentCulture=System.Globalization.CultureInfo.InvariantCulture;
        JoinLoadRing ring=new JoinLoadRing();
        for(int i=0;i<400;i++)ring.Add(new JoinLoadSample {At=i,Route="bridge/P2P",Server=true,Conn=2,Rtt=32,Queue=0,Bytes=i*100,Lost=0,Step=50,MaxStep=72});
        string report=ring.Report("fixture");
        Check(ring.Count==121,"preallocated ring wraps");
        Check(!report.Contains("t=1.0 ") && report.Contains("t=399.0 "),"retains newest samples");
        Check(Encoding.UTF8.GetByteCount(report)<=16384,"report byte cap");
        Check(!JoinLoadWire.Clean("peer=76561199999999999").Contains("765611"),"ID removal");
        Check(!JoinLoadWire.CanBind(111,222,false,false),"another player's identity rejected");
        Check(!JoinLoadWire.CanBind(0,222,false,false),"missing native Steam claim fails closed");
        Check(JoinLoadWire.CanBind(222,222,false,false),"native direct/Radmin player identity matches sender");
        Check(!JoinLoadWire.CanBind(222,222,true,false),"bridge peer port mismatch rejected");
        Check(JoinLoadWire.CanBind(222,222,true,true),"bridge peer port and native identity match");
        for(int bits=0;bits<16;bits++)
        {
            bool friend=(bits&1)!=0, consent=(bits&2)!=0, hostLoading=(bits&4)!=0, guestLoading=(bits&8)!=0;
            Check(JoinLoadWire.CanDeliver(friend,consent,hostLoading,guestLoading,3,0)==(bits==3),"friend/consent/loading gate");
        }
        Check(!JoinLoadWire.CanDeliver(true,true,false,false,13,0),"stale end state");
        Check(!JoinLoadWire.CanDeliver(true,true,false,false,3,32769),"Steam backlog yields");
        Check(!JoinLoadWire.CanDeliver(true,true,false,false,3,-1),"unknown queue fails closed");
        Check(report.Split('\n').Length>=123,"all 121 compact samples fit");
        byte[] data=Encoding.UTF8.GetBytes(report);
        JoinLoadReceiver rx=new JoinLoadReceiver();string got=null;
        for(int offset=0;offset<data.Length;offset+=JoinLoadWire.Chunk)
        {
            int n=Math.Min(JoinLoadWire.Chunk,data.Length-offset);byte[] payload=new byte[n];Buffer.BlockCopy(data,offset,payload,0,n);
            byte[] p=JoinLoadWire.Packet(JoinLoadWire.Report,12,(uint)data.Length,offset,payload,n);
            Check(JoinLoadWire.Valid(p,p.Length),"valid chunk");
            Check(rx.Accept(p,p.Length,13)==null,"wrong session rejected");
            got=rx.Accept(p,p.Length,12);
            if(offset+n<data.Length)Check(got==null,"partial not emitted");
            if(offset==0)Check(rx.Accept(p,p.Length,12)==null,"duplicate rejected");
        }
        Check(got==report,"fake channel exact roundtrip");
        rx.Reset();byte[] bad=JoinLoadWire.Packet(JoinLoadWire.Report,12,20000,0,new byte[1],1);
        Check(rx.Accept(bad,bad.Length,12)==null,"oversize declaration");
        bad[2]=2;Check(!JoinLoadWire.Valid(bad,bad.Length),"future protocol rejected");
        Check(!JoinLoadWire.Valid(new byte[1],1),"truncated header");
        byte[] shuffle=JoinLoadWire.Packet(JoinLoadWire.Report,12,3,1,new byte[1],1);
        Check(rx.Accept(shuffle,shuffle.Length,12)==null,"out of order rejected");
        byte[] utf=JoinLoadWire.Packet(JoinLoadWire.Report,12,1,0,new byte[]{255},1);
        Check(rx.Accept(utf,utf.Length,12)==null,"invalid UTF8 rejected");
        string dir=Path.Combine(Path.GetTempPath(),"JoinLoadTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        string path=Path.Combine(dir,"perf-trace.txt");
        try
        {
            for(int i=0;i<100;i++)RollingTrace.Append(path,"sample-"+i+" "+new string('x',50)+"\r\n",256);
            Check(new FileInfo(path).Length<=256 && new FileInfo(Path.Combine(dir,"perf-trace.1.txt")).Length<=256,"bounded trace segments");
            Check(File.ReadAllText(path).Contains("sample-99"),"trace keeps newest");
        }
        finally {Directory.Delete(dir,true);}
        Stopwatch watch=Stopwatch.StartNew();long baseline=0;
        for(int i=0;i<1000000;i++)baseline+=i;watch.Stop();double off=watch.Elapsed.TotalMilliseconds;
        watch.Reset();watch.Start();
        for(int i=0;i<1000000;i++)ring.Add(new JoinLoadSample {At=i,Route="bridge/P2P"});watch.Stop();
        Console.WriteLine("PASS "+checks+" checks; ring A/B 1M iterations offMs="+off.ToString("F2")+" onMs="+watch.Elapsed.TotalMilliseconds.ToString("F2")+" (pure recorder only; not Unity frame A/B), baseline="+baseline);
    }
}
