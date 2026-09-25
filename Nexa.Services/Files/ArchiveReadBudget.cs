using System.Buffers;

namespace Nexa.Services.Files;

// Owned by one sequential import transaction; never shared between concurrent copies.
internal sealed class ArchiveReadBudget(long limit)
{
    internal long Remaining { get; private set; } = limit >= 0 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));

    internal void Consume(long bytes)
    {
        if (bytes < 0 || bytes > Remaining) throw new InvalidDataException("归档实际展开大小超过限制。");
        Remaining -= bytes;
    }

    internal static async Task CopyAsync(Stream input, Stream output, long declaredLength, long perEntryLimit,
        ArchiveReadBudget budget, CancellationToken token)
    {
        if (declaredLength < 0 || declaredLength > perEntryLimit)
            throw new InvalidDataException("归档条目大小超过限制。");
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        long received = 0;
        try
        {
            while (true)
            {
                // Probe at most one byte beyond the remaining allowance; never write that byte.
                long allowance = Math.Min(declaredLength - received, budget.Remaining);
                int count = (int)Math.Min(buffer.Length, allowance < buffer.Length ? allowance + 1 : buffer.Length);
                int read = await input.ReadAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                if (read == 0) break;
                if (read > declaredLength - received) throw new InvalidDataException("归档条目实际长度与声明不一致。");
                budget.Consume(read);
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;
            }
            if (received != declaredLength) throw new InvalidDataException("归档条目实际长度与声明不一致。");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
