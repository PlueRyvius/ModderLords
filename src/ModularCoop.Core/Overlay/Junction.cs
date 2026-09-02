using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ModularCoop.Core.Overlay;

/// <summary>
/// NTFS directory junctions. They need no admin rights, survive reboots, and are what lets the engine see a
/// mod folder under engine\Modules without copying anything. Removing a junction removes only the link.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Junction
{
    public static bool IsJunction(string path)
    {
        var di = new DirectoryInfo(path);
        return di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint) && di.LinkTarget is not null;
    }

    /// <summary>Target of a junction (or symlink) as stored, or null when the path is a real directory / missing.</summary>
    public static string? Target(string path)
    {
        var di = new DirectoryInfo(path);
        if (!di.Exists || !di.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
        var t = di.LinkTarget;
        if (t is null) return null;
        if (t.StartsWith(@"\??\", StringComparison.Ordinal)) t = t[4..];
        return t;
    }

    public static bool PointsTo(string junctionPath, string target)
    {
        var t = Target(junctionPath);
        return t is not null && PathsEqual(t, target);
    }

    public static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates (or repoints) a junction. Refuses to replace a real directory.</summary>
    public static void Create(string junctionPath, string target)
    {
        target = Path.GetFullPath(target);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException($"Junction target does not exist: {target}");

        if (Directory.Exists(junctionPath) || File.Exists(junctionPath))
        {
            if (!IsJunction(junctionPath))
                throw new IOException($"Refusing to replace a real directory or file with a junction: {junctionPath}");
            if (PointsTo(junctionPath, target)) return;
            Remove(junctionPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(junctionPath))!);
        try
        {
            CreateNative(junctionPath, target);
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            // Fallback: cmd's mklink /J does exactly the same DeviceIoControl call.
            CreateViaMklink(junctionPath, target, ex);
        }
    }

    /// <summary>Removes a junction (only the link). Throws if the path is a real directory.</summary>
    public static void Remove(string junctionPath)
    {
        if (!Directory.Exists(junctionPath)) return;
        if (!IsJunction(junctionPath))
            throw new IOException($"Refusing to delete a real directory (not a junction): {junctionPath}");
        Directory.Delete(junctionPath, recursive: false);
    }

    // ---- native ------------------------------------------------------------------------------------

    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, byte[] lpInBuffer, int nInBufferSize,
        IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static void CreateNative(string junctionPath, string target)
    {
        Directory.CreateDirectory(junctionPath);
        var handle = CreateFileW(junctionPath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
            var print = Encoding.Unicode.GetBytes(target);
            // REPARSE_DATA_BUFFER (mount point): tag(4) length(2) reserved(2) subOffset(2) subLen(2) printOffset(2) printLen(2) path data
            var pathBuffer = new byte[substitute.Length + 2 + print.Length + 2];
            Buffer.BlockCopy(substitute, 0, pathBuffer, 0, substitute.Length);
            Buffer.BlockCopy(print, 0, pathBuffer, substitute.Length + 2, print.Length);
            var dataLength = 8 + pathBuffer.Length;
            var buffer = new byte[8 + dataLength];
            BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(buffer, 0);
            BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6);
            BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
            BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
            BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
            BitConverter.GetBytes((ushort)print.Length).CopyTo(buffer, 14);
            pathBuffer.CopyTo(buffer, 16);
            if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                var err = Marshal.GetLastWin32Error();
                CloseHandle(handle); handle = IntPtr.Zero;
                try { Directory.Delete(junctionPath); } catch { }
                throw new Win32Exception(err);
            }
        }
        finally
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle);
        }
    }

    private static void CreateViaMklink(string junctionPath, string target, Exception inner)
    {
        try { if (Directory.Exists(junctionPath) && !IsJunction(junctionPath)) Directory.Delete(junctionPath); } catch { }
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("/c"); psi.ArgumentList.Add("mklink"); psi.ArgumentList.Add("/J"); psi.ArgumentList.Add(junctionPath); psi.ArgumentList.Add(target);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 || !IsJunction(junctionPath))
            throw new IOException($"Could not create junction {junctionPath} -> {target}: {err.Trim()}", inner);
    }
}
