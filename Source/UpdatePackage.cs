using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CastleStorySteam
{
    internal static class BridgeVersion { internal const string Text = "0.3.53"; }

    // Signature check for bridge updates. The signed bytes are "<version>\0<dll bytes>", so a signed old DLL
    // cannot be re-labelled as a newer version.
    internal static class UpdateVerifier
    {
        internal static byte[] SignedData(string version, byte[] dll)
        {
            byte[] v = Encoding.UTF8.GetBytes(version);
            byte[] all = new byte[v.Length + 1 + dll.Length];
            Buffer.BlockCopy(v, 0, all, 0, v.Length);
            all[v.Length] = 0;
            Buffer.BlockCopy(dll, 0, all, v.Length + 1, dll.Length);
            return all;
        }
        internal static bool Verify(string version, byte[] dll, byte[] signature)
        {
            if (signature == null || signature.Length != 256 || dll == null || dll.Length == 0 || !TryParse(version, out parsedUnused)) return false;
            try
            {
                RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
                try
                {
                    rsa.FromXmlString(UpdateKey.PublicXml);
                    return rsa.VerifyData(SignedData(version, dll), new SHA256Managed(), signature);
                }
                finally { rsa.Clear(); }
            }
            catch (Exception) { return false; }
        }
        [ThreadStatic] static Version parsedUnused;
        internal static bool TryParse(string text, out Version v)
        {
            v = null;
            if (text == null || !Regex.IsMatch(text, @"^\d{1,4}\.\d{1,4}\.\d{1,4}(\.\d{1,4})?$")) return false;
            try { v = new Version(text); return true; } catch (Exception) { return false; }
        }
        internal static bool IsNewer(string candidate, string current)
        {
            Version a, b;
            return TryParse(candidate, out a) && TryParse(current, out b) && a > b;
        }
    }

    // Update package = version + dll + signature, sent in numbered chunks over the existing bridge link.
    internal static class UpdatePackage
    {
        internal const int MaxBytes = 512 * 1024, ChunkBytes = 16 * 1024, HeaderBytes = 12;

        internal static byte[] Build(string version, byte[] dll, byte[] sig)
        {
            byte[] v = Encoding.UTF8.GetBytes(version);
            if (v.Length == 0 || v.Length > 20 || dll == null || sig == null || sig.Length != 256) throw new ArgumentException("Bad package parts");
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write((byte)v.Length); w.Write(v);
                w.Write(dll.Length); w.Write(dll);
                w.Write(sig.Length); w.Write(sig);
                w.Flush();
                byte[] blob = ms.ToArray();
                if (blob.Length > MaxBytes) throw new InvalidOperationException("Update package is too large");
                return blob;
            }
        }
        internal static bool Parse(byte[] blob, out string version, out byte[] dll, out byte[] sig)
        {
            version = null; dll = null; sig = null;
            if (blob == null || blob.Length < 10 || blob.Length > MaxBytes) return false;
            try
            {
                using (MemoryStream ms = new MemoryStream(blob))
                using (BinaryReader r = new BinaryReader(ms))
                {
                    int vl = r.ReadByte();
                    if (vl == 0 || vl > 20 || ms.Position + vl + 8 > blob.Length) return false;
                    version = Encoding.UTF8.GetString(r.ReadBytes(vl));
                    int dl = r.ReadInt32();
                    if (dl <= 0 || dl > blob.Length - ms.Position - 4) return false;
                    dll = r.ReadBytes(dl);
                    int sl = r.ReadInt32();
                    if (sl != 256 || sl != blob.Length - ms.Position) return false;
                    sig = r.ReadBytes(sl);
                    return true;
                }
            }
            catch (Exception) { return false; }
        }
        // Chunk = id(4) total(4) offset(4) data.
        internal static List<byte[]> Chunks(uint id, byte[] blob)
        {
            List<byte[]> result = new List<byte[]>();
            for (int offset = 0; offset < blob.Length; offset += ChunkBytes)
            {
                int n = Math.Min(ChunkBytes, blob.Length - offset);
                byte[] c = new byte[HeaderBytes + n];
                Buffer.BlockCopy(BitConverter.GetBytes(id), 0, c, 0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(blob.Length), 0, c, 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(offset), 0, c, 8, 4);
                Buffer.BlockCopy(blob, offset, c, HeaderBytes, n);
                result.Add(c);
            }
            return result;
        }

        internal sealed class Receiver
        {
            readonly uint id;
            byte[] buffer;
            internal int Got { get; private set; }
            internal int Total { get { return buffer == null ? 0 : buffer.Length; } }
            internal bool Complete { get { return buffer != null && Got == buffer.Length; } }
            internal byte[] Blob { get { return Complete ? buffer : null; } }
            internal Receiver(uint expectedId) { id = expectedId; }
            // Strictly sequential: reliable Steam delivery keeps order, so anything else is rejected.
            internal bool Accept(byte[] chunk, out string error)
            {
                error = null;
                if (chunk == null || chunk.Length <= HeaderBytes) { error = "порожній шматок"; return false; }
                uint cid = BitConverter.ToUInt32(chunk, 0); int total = BitConverter.ToInt32(chunk, 4); int offset = BitConverter.ToInt32(chunk, 8);
                int n = chunk.Length - HeaderBytes;
                if (cid != id) { error = "чужий ідентифікатор передачі"; return false; }
                if (total <= 0 || total > MaxBytes) { error = "некоректний розмір"; return false; }
                if (buffer == null) buffer = new byte[total];
                else if (total != buffer.Length) { error = "розмір змінився під час передачі"; return false; }
                if (offset != Got || n > ChunkBytes || offset + n > total) { error = "порушено порядок шматків"; return false; }
                Buffer.BlockCopy(chunk, HeaderBytes, buffer, offset, n);
                Got += n;
                return true;
            }
        }
    }
}
