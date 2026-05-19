using System.Buffers;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Easydict.WinUI.Services.Memory;

internal sealed class PooledDenseTensor<T> : IDisposable
{
    private T[]? _buffer;

    private PooledDenseTensor(T[] buffer, int length, int[] dimensions)
    {
        _buffer = buffer;
        Length = length;
        Tensor = new DenseTensor<T>(buffer.AsMemory(0, length), dimensions);
    }

    public DenseTensor<T> Tensor { get; }

    public int Length { get; }

    public static PooledDenseTensor<T> Rent(params int[] dimensions)
    {
        var length = 1;
        foreach (var dimension in dimensions)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
            length = checked(length * dimension);
        }

        var buffer = ArrayPool<T>.Shared.Rent(length);
        return new PooledDenseTensor<T>(buffer, length, dimensions);
    }

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            ArrayPool<T>.Shared.Return(buffer, clearArray: true);
        }
    }
}
