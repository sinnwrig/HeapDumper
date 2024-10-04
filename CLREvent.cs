using System.Runtime.InteropServices;

namespace HeapDumper;

[StructLayout(LayoutKind.Sequential)]
unsafe struct CLREvent
{
    public nint m_handle;
    public nint m_dwFlags;
}