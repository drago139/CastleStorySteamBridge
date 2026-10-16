using System;
using System.IO;

namespace CastleStorySteam
{
    internal static class UpdateFileStore
    {
        internal delegate void Writer(string path, byte[] bytes);
        // Caller verifies signature and version first. Keep both prior files together for rollback.
        internal static void Apply(string target, byte[] dll, byte[] signature, Writer writer)
        {
            if (writer == null) writer = File.WriteAllBytes;
            string temp = target + ".new", sigPath = target + ".sig", sigTemp = sigPath + ".new";
            string old = target + ".old", oldSig = sigPath + ".old";
            bool moved = false, hadSig = File.Exists(sigPath);
            try
            {
                writer(temp, dll); writer(sigTemp, signature);
                if (File.Exists(old)) File.Delete(old);
                if (File.Exists(oldSig)) File.Delete(oldSig);
                if (hadSig) File.Copy(sigPath, oldSig);
                File.Move(target, old); moved = true;
                File.Move(temp, target);
                // Stage and validate the signature before replacing the DLL; restore BOTH on any failure here.
                writer(sigPath, File.ReadAllBytes(sigTemp));
            }
            catch
            {
                if (moved)
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(old, target);
                    if (hadSig && File.Exists(oldSig)) File.Copy(oldSig, sigPath, true);
                    else if (!hadSig && File.Exists(sigPath)) File.Delete(sigPath);
                }
                throw;
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
                if (File.Exists(sigTemp)) File.Delete(sigTemp);
            }
        }
    }
}
