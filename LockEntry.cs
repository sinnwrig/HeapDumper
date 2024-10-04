using System.Runtime.InteropServices;

namespace HeapDumper;

[StructLayout(LayoutKind.Sequential)]
unsafe struct LockEntry
{
    public nint* pNext;
    public nint* pPrev;
    public int dwULockID;
    public int dwLLockID;
    public short wReaderLevel;
}