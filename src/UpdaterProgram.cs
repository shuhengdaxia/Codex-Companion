using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

internal static class UpdaterProgram
{
    private static int Main(string[] args)
    {
        if (args.Length != 4) return 2;
        int parentId;
        if (!Int32.TryParse(args[0], out parentId) || parentId <= 0) return 2;
        string target = Path.GetFullPath(args[1]);
        string source = Path.GetFullPath(args[2]);
        if (!String.Equals(Path.GetFileName(target), "CodexCompanion.exe", StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(Path.GetFileName(source), "CodexCompanion.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(target) || !File.Exists(source) || args[3].Length != 64) return 2;
        foreach (char c in args[3]) if (!Uri.IsHexDigit(c)) return 2;
        try
        {
            using (var sha = SHA256.Create())
            using (var input = File.OpenRead(source))
                if (!String.Equals(BitConverter.ToString(sha.ComputeHash(input)).Replace("-", ""), args[3], StringComparison.OrdinalIgnoreCase))
                    return 3;
            try { using (Process parent = Process.GetProcessById(parentId)) if (!parent.WaitForExit(60000)) return 4; }
            catch (ArgumentException) { }
            string pending = target + ".pending";
            string backup = target + ".previous";
            try
            {
                File.Copy(source, pending, true);
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Replace(pending, target, backup, true); break; }
                    catch (IOException) { if (attempt >= 20) throw; Thread.Sleep(500); }
                }
                try { Process.Start(new ProcessStartInfo(target) { WorkingDirectory = Path.GetDirectoryName(target), UseShellExecute = true }); }
                catch
                {
                    File.Replace(backup, target, null, true);
                    Process.Start(new ProcessStartInfo(target) { WorkingDirectory = Path.GetDirectoryName(target), UseShellExecute = true });
                    return 5;
                }
                return 0;
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
        catch (Exception error)
        {
            try { File.WriteAllText(source + ".error.txt", error.ToString()); } catch { }
            return 1;
        }
    }
}
