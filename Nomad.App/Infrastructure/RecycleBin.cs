using System.Runtime.InteropServices;

namespace Nomad.App.Infrastructure;

/// <summary>Deletes files and folders into the Recycle Bin, so a deleted session can still be restored.</summary>
public static class RecycleBin
{
    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4;
    private const ushort FofNoConfirmation = 0x10;
    private const ushort FofAllowUndo = 0x40;
    private const ushort FofNoErrorUi = 0x400;

    // Pointers only, so the struct is blittable (NativeAOT, no runtime marshalling).
    [StructLayout(LayoutKind.Sequential)]
    private struct ShFileOpStruct
    {
        public IntPtr Hwnd;
        public uint Func;
        public IntPtr From;
        public IntPtr To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public IntPtr NameMappings;
        public IntPtr ProgressTitle;
    }

    [DllImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShFileOpStruct op);

    /// <summary>Moves the existing ones of <paramref name="paths"/> to the Recycle Bin; false if the shell failed.</summary>
    public static bool Delete(IEnumerable<string> paths)
    {
        var existing = paths.Where(p => File.Exists(p) || Directory.Exists(p)).Select(Path.GetFullPath).ToList();
        if (existing.Count == 0)
        {
            return true;
        }

        // A double-null-terminated list of paths.
        var from = Marshal.StringToHGlobalUni(string.Join('\0', existing) + "\0\0");
        try
        {
            var op = new ShFileOpStruct
            {
                Func = FoDelete,
                From = from,
                Flags = FofAllowUndo | FofNoConfirmation | FofSilent | FofNoErrorUi,
            };
            return SHFileOperation(ref op) == 0 && op.AnyOperationsAborted == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(from);
        }
    }
}
