using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;

namespace Easydict.WinUI.Services.Memory;

internal static class PixelMemory
{
    public static byte[] ToArrayForInterop(ReadOnlyMemory<byte> memory, out int offset, out int length)
    {
        if (MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment) && segment.Array is not null)
        {
            offset = segment.Offset;
            length = segment.Count;
            return segment.Array;
        }

        var array = memory.ToArray();
        offset = 0;
        length = array.Length;
        return array;
    }

    public static IBuffer AsBufferForInterop(ReadOnlyMemory<byte> memory, out byte[]? temporaryArray)
    {
        var array = ToArrayForInterop(memory, out var offset, out var length);
        temporaryArray = ReferenceEquals(array, MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment) ? segment.Array : null)
            ? null
            : array;
        return array.AsBuffer(offset, length);
    }
}
