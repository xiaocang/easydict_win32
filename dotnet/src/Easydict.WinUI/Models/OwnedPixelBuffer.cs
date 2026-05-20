using System.Buffers;

namespace Easydict.WinUI.Models;

/// <summary>
/// Owns a rented BGRA pixel buffer and returns it to the shared pool on dispose.
/// </summary>
public sealed class OwnedPixelBuffer : IMemoryOwner<byte>
{
    private byte[]? _buffer;

    private OwnedPixelBuffer(byte[] buffer, int length)
    {
        _buffer = buffer;
        Length = length;
    }

    public int Length { get; }

    public Memory<byte> Memory
    {
        get
        {
            var buffer = _buffer ?? throw new ObjectDisposedException(nameof(OwnedPixelBuffer));
            return buffer.AsMemory(0, Length);
        }
    }

    public static OwnedPixelBuffer Rent(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return new OwnedPixelBuffer(ArrayPool<byte>.Shared.Rent(length), length);
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
