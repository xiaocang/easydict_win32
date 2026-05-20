using System.Buffers;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Easydict.OpenVINO.Inference;

internal sealed class PooledDenseTensor<T> : IDisposable
{
    private T[]? _buffer;
    private readonly DenseTensor<T> _tensor;

    private PooledDenseTensor(T[] buffer, int length, int[] dimensions)
    {
        _buffer = buffer;
        Length = length;
        _tensor = new DenseTensor<T>(buffer.AsMemory(0, length), dimensions);
    }

    public DenseTensor<T> Tensor => _buffer is null
        ? throw new ObjectDisposedException(nameof(PooledDenseTensor<T>))
        : _tensor;

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

    public static PooledDenseTensor<T> CopyFrom(Tensor<T> source)
    {
        var dimensions = source.Dimensions.ToArray();
        var owner = Rent(dimensions);
        var destination = owner.Tensor.Buffer.Span;
        if (source is DenseTensor<T> dense)
        {
            dense.Buffer.Span.CopyTo(destination);
        }
        else
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = source.GetValue(i);
            }
        }

        return owner;
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
