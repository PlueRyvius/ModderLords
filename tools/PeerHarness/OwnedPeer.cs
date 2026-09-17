using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ModderLords.TestHarness
{
    // Original test infrastructure. No game assemblies or mod code are loaded here.
    public sealed class OwnedPeer : IDisposable
    {
        public Process Process { get; private set; }
        public string OutputFailure { get; private set; }
        public bool Forced { get; private set; }
        public int? ExitBeforeCleanup { get; private set; }
        private IntPtr job;
        private Task stdout, stderr;
        private bool disposed;
        private const long MaxOutputBytes = 16 * 1024 * 1024;

        public OwnedPeer(ProcessStartInfo info, string outPath, string errPath)
        {
            info.UseShellExecute = false; info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
            // Open evidence before starting a child; a bad evidence path cannot orphan it.
            var output = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            FileStream error = null;
            try
            {
                error = new FileStream(errPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                job = CreateJobObjectW(IntPtr.Zero, null);
                if (job == IntPtr.Zero) throw new IOException("Cannot create peer job");
                var limits = new Limits { Basic = new BasicLimits { Flags = 0x2000 } };
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(Limits))))
                    throw new IOException("Cannot configure peer job");
                Process = Process.Start(info);
                if (!AssignProcessToJobObject(job, Process.Handle)) throw new IOException("Cannot contain peer process");
                stdout = Pump(Process.StandardOutput.BaseStream, output);
                stderr = Pump(Process.StandardError.BaseStream, error);
            }
            catch
            {
                if (Process != null) { try { if (!Process.HasExited) Process.Kill(true); } catch { } Process.Dispose(); }
                if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
                output.Dispose(); if (error != null) error.Dispose(); throw;
            }
        }
        private async Task Pump(Stream input, FileStream output)
        {
            using (output)
            {
                var buffer = new byte[8192]; long total = 0;
                try
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                        if (total + read <= MaxOutputBytes)
                        { await output.WriteAsync(buffer, 0, read).ConfigureAwait(false); await output.FlushAsync().ConfigureAwait(false); }
                        else OutputFailure = "Peer output exceeded 16 MiB per stream";
                        total += read;
                    }
                }
                catch (Exception ex) { if (!disposed) OutputFailure = "Output capture failed: " + ex.Message; }
            }
        }
        public void Send(string line)
        { Process.StandardInput.WriteLine(line); Process.StandardInput.Flush(); }
        public void Stop(bool gracefulServer)
        {
            if (disposed) return;
            if (Process.HasExited) ExitBeforeCleanup = Process.ExitCode;
            else if (gracefulServer)
            { try { Send("quit"); Process.WaitForExit(3000); } catch { } }
            // Close the whole job even when the original parent has exited: descendants may still own pipes.
            Forced = !Process.HasExited;
            if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
            if (!Process.WaitForExit(3000)) throw new IOException("Peer did not terminate after job closure");
            if (!Task.WaitAll(new[] { stdout, stderr }, 3000)) throw new IOException("Peer output did not close after job termination");
        }
        public void Dispose()
        {
            if (disposed) return;
            try { Stop(false); }
            finally { disposed = true; if (job != IntPtr.Zero) CloseHandle(job); Process.Dispose(); }
        }
        // Called only by the handshake worker after its parent has assigned it to a job.
        public static int RunTarget(ProcessStartInfo info, bool visible = false)
        {
            info.UseShellExecute = false; info.CreateNoWindow = !visible; info.WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden;
            info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
            using (var child = Process.Start(info))
            {
                var output = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
                var error = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
                Task.Run(() =>
                {
                    try
                    {
                        string line;
                        while ((line = Console.ReadLine()) != null)
                        { child.StandardInput.WriteLine(line); child.StandardInput.Flush(); }
                    }
                    catch { /* Target exit closes stdin. */ }
                });
                child.WaitForExit();
                Task.WaitAll(new[] { output, error }, 3000);
                return child.ExitCode;
            }
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref Limits limits, uint size);
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        { public long A,B; public uint Flags; public UIntPtr C,D; public uint E; public UIntPtr F; public uint G,H; }
        [StructLayout(LayoutKind.Sequential)] private struct Counters { public ulong A,B,C,D,E,F; }
        [StructLayout(LayoutKind.Sequential)] private struct Limits
        { public BasicLimits Basic; public Counters Counters; public UIntPtr A,B,C,D; }
    }
}
