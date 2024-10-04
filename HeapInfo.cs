namespace HeapDumper;

public struct HeapInfo
{
    public IntPtr MethodTable;
    public ulong InstanceCount;
    public ulong TotalSize;
    public Type? Type;

    public override readonly string ToString()
    {
        string typeName = Type == null ? "Free" : Type.Name;
        return $"Method Table: {MethodTable,12:x2}. Instances: {InstanceCount,8}. Total size: {TotalSize,12}. Type: {typeName}";
    }
}