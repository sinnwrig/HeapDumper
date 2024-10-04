using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security;

namespace HeapDumper;

#pragma warning disable CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type

public static class HeapDumper
{
    public static void Main() { }

    [HandleProcessCorruptedStateExceptions]
    [SecurityCritical]
    public static unsafe List<HeapInfo> DumpHeap()
    {
        if (!GCSettings.IsServerGC)
            throw new InvalidOperationException("Server GC must be enabled to dump heap.");

        if (IntPtr.Size != 8)
            throw new InvalidProgramException("Cannot dump heap on non-64-bit architecture.");

        if (Environment.Version.Major != 8)
            throw new InvalidProgramException("Dumping heap requires .NET 8");

        // Preallocate the dictionary with a large size, to reduce the number of potential allocations in the no-gc region
        var stats = new Dictionary<nint, (ulong count, ulong totalSize)>(1000);

        try
        {
            // Force a GC to minimize the amount of memory dangling in the allocation contexts
            GC.Collect(2, GCCollectionMode.Forced, true, true);

            GC.TryStartNoGCRegion(200 * 1024 * 1024);
            GC.RegisterNoGCRegionCallback(150 * 1024 * 1024, () => throw new OutOfMemoryException("Interrupted by GC."));

            ref var thread = ref NativeThread.GetCurrentNativeThread();
            var heap = thread.m_alloc_context.gc_reserved_1;

            var vtablePtr = (nint*)heap->vtable;

            var diagDescrGenerationsAddr = *(vtablePtr + 69);
            var diagDescrGenerations = (delegate* unmanaged<GCHeap*, delegate* unmanaged<void*, int, IntPtr, IntPtr, IntPtr, void>, void*, void>)diagDescrGenerationsAddr;

            var walkGeneration = (delegate* unmanaged<void*, int, IntPtr, IntPtr, IntPtr, void>)&WalkGeneration;

            var context = ValueTuple.Create(stats);

            diagDescrGenerations(thread.m_alloc_context.gc_reserved_1, walkGeneration, &context);

            WalkNGCH(&context);

            GC.EndNoGCRegion();

            return ParseStats(stats);
        }
        catch (Exception ex) // If an exception is ever caught here, it's guaranteed to be pretty bad.
        {
            Console.WriteLine($"Fatal error dumping heap: {ex}");
            Environment.Exit(1);
            throw;
        }
    }

    private static unsafe List<HeapInfo> ParseStats(Dictionary<nint, (ulong count, ulong totalSize)> stats)
    {
        List<HeapInfo> info = new List<HeapInfo>();

        foreach (var (key, value) in stats.OrderBy(kvp => kvp.Value.totalSize))
        {
            var mt = ReadMethodTable(&key);

            Type? type = null;

            if (!(mt.HasComponentSize && mt.ParentMethodTable == null))
            {
                RuntimeTypeHandle handle = RuntimeTypeHandle.FromIntPtr(key);
                type = Type.GetTypeFromHandle(handle)!;
            }

            info.Add(new HeapInfo()
            {
                MethodTable = key,
                InstanceCount = value.count,
                TotalSize = value.totalSize,
                Type = type,
            });
        }

        return info;
    }

    [UnmanagedCallersOnly]
    private static unsafe void WalkGeneration(void* context, int generation, IntPtr rangeStart, IntPtr rangeEnd, IntPtr rangeReserved)
    {
        WalkHeap(rangeStart, rangeEnd, context);
    }

    private static unsafe void WalkObject(nint* obj, int size, void* context)
    {
        var stats = (*(ValueTuple<Dictionary<nint, (ulong count, ulong totalSize)>>*)context).Item1;

        ref var value = ref CollectionsMarshal.GetValueRefOrAddDefault(stats, *obj, out _);

        value.count += 1;
        value.totalSize += (ulong)size;
    }

    private static unsafe void WalkHeap(IntPtr heap, IntPtr limit, void* context)
    {
        var mem = heap;

        while (mem < limit)
        {
            var mt = (nint*)mem;

            var size = ComputeSize(mt);

            if (size == 0)
            {
                break;
            }

            WalkObject(mt, size, context);

            var alignment = sizeof(nint) - 1;
            mem += (size + alignment) & ~alignment;
        }
    }

    private static unsafe void WalkNGCH(void* context)
    {
        var segment = RegisterFrozenSegment(0x0, 0);

        var heapSegment = *(HeapSegment*)segment;

        var next = heapSegment.next;

        while (next != null)
        {
            if (next->flags == HeapSegment.RegionFlags.Readonly)
            {
                WalkHeap((nint)next->mem, next->allocated, context);
            }

            next = next->next;
        }

        UnregisterFrozenSegment(segment);
    }
    private static unsafe int ComputeSize(nint* address)
    {
        ref var methodTable = ref ReadMethodTable(address);

        if (Unsafe.IsNullRef(ref methodTable))
        {
            return 0;
        }

        if (!methodTable.HasComponentSize)
        {
            return methodTable.BaseSize;
        }

        var length = *(int*)((nint)address + sizeof(nint));
        var size = methodTable.ComponentSize;

        return methodTable.BaseSize + length * size;
    }

    private static unsafe ref MethodTable ReadMethodTable(nint* address) => ref *(MethodTable*)*address;

    private static IntPtr RegisterFrozenSegment(IntPtr sectionAddress, nint sectionSize)
    {
        return (IntPtr)typeof(GC).GetMethod("_RegisterFrozenSegment", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [sectionAddress, sectionSize])!;
    }

    private static void UnregisterFrozenSegment(IntPtr segment)
    {
        typeof(GC).GetMethod("_UnregisterFrozenSegment", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [segment]);
    }
}
