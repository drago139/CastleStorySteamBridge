using System;
using System.IO;
using System.Text;

namespace CastleStorySteam
{
    // Worker-only, two bounded segments. Rotate before appending; retain the newest data.
    internal static class RollingTrace
    {
        internal const int SegmentBytes = 6 * 1024 * 1024;
        internal static void Append(string path, string text, int limit, string header = null)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            int start = Math.Max(0, data.Length - limit);
            if (start > 0) while (start < data.Length && data[start] != 10) start++;
            if (start < data.Length && start > 0) start++;
            if (File.Exists(path) && new FileInfo(path).Length + data.Length - start > limit)
            {
                string previous = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + ".1.txt");
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(path, previous);
            }
            using (FileStream f = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                if(f.Length==0 && !string.IsNullOrEmpty(header)) { byte[] h=Encoding.UTF8.GetBytes(header);f.Write(h,0,h.Length); }
                f.Write(data, start, data.Length - start);
            }
        }
    }
}
