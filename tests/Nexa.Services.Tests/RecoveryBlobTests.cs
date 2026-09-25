using Nexa.Services.Minecraft.Management;

namespace Nexa.Services.Tests;

internal static partial class Program
{
    private static async ValueTask RecoveryObjectsDeduplicateAndVerifyActualBytes()
    {
        string root = CreateTempDirectory();
        try
        {
            var store = new RecoveryBlobStore(root);
            byte[] data = Enumerable.Repeat((byte)42, 10000).ToArray();
            using var first = new MemoryStream(data);
            var blob = await store.StoreAsync(first, data.Length, new(data.Length));
            using var second = new MemoryStream(data);
            AssertEqual(blob, await store.StoreAsync(second, data.Length, new(data.Length)));
            string[] objects = Directory.GetFiles(Path.Combine(root, "objects"), "*.br");
            AssertEqual(1, objects.Length);
            AssertTrue(new FileInfo(objects[0]).Length < data.Length);
            using var restored = new MemoryStream();
            await store.CopyVerifiedAsync(blob, restored, new(data.Length));
            AssertTrue(data.SequenceEqual(restored.ToArray()));

            async Task Reject(Func<Task> action)
            {
                try { await action(); throw new InvalidOperationException("Invalid object accepted."); }
                catch (InvalidDataException) { }
            }
            using var tooLong = new MemoryStream(data);
            await Reject(() => store.StoreAsync(tooLong, data.Length - 1, new(data.Length)));
            using var tooShort = new MemoryStream(data);
            await Reject(() => store.StoreAsync(tooShort, data.Length + 1, new(data.Length + 1)));
            using var limited = new MemoryStream(data);
            await Reject(() => store.StoreAsync(limited, data.Length, new(1)));
            await Reject(() => store.CopyVerifiedAsync(blob with { Length = 1 }, Stream.Null, new(data.Length)));
            await Reject(() => store.CopyVerifiedAsync(blob with { Sha256 = "../outside" }, Stream.Null, new(data.Length)));
            AssertEqual(0, Directory.GetFiles(Path.Combine(root, "objects"), "*.part").Length);
            AssertEqual(1, Directory.GetFiles(Path.Combine(root, "objects"), "*.br").Length);
            var sharedBudget = new RecoveryByteBudget(data.Length + 1);
            using var allowed = new MemoryStream(data);
            await store.StoreAsync(allowed, data.Length, sharedBudget);
            using var exceedingTotal = new MemoryStream(data);
            await Reject(() => store.StoreAsync(exceedingTotal, data.Length, sharedBudget));
            await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
            {
                using var concurrent = new MemoryStream(data);
                AssertEqual(blob, await store.StoreAsync(concurrent, data.Length, new(data.Length)));
            }));
            AssertEqual(1, Directory.GetFiles(Path.Combine(root, "objects"), "*.br").Length);

            // Corrupt bytes are never accepted; a new capture of the original content repairs
            // the object instead of trusting a hash-shaped filename.
            File.WriteAllBytes(objects[0], [0, 1, 2]);
            await Reject(() => store.CopyVerifiedAsync(blob, Stream.Null, new(data.Length)));
            using var repair = new MemoryStream(data);
            AssertEqual(blob, await store.StoreAsync(repair, data.Length, new(data.Length)));
            await store.CopyVerifiedAsync(blob, Stream.Null, new(data.Length));
            using var stop = new CancellationTokenSource(); stop.Cancel();
            using var cancelled = new MemoryStream(data);
            try { await store.StoreAsync(cancelled, data.Length, new(data.Length), stop.Token); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            AssertEqual(0, Directory.GetFiles(Path.Combine(root, "objects"), "*.part").Length);
            await store.CopyVerifiedAsync(blob, Stream.Null, new(data.Length));
        }
        finally { Directory.Delete(root, true); }
    }
}
