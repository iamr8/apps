using System.Threading.Channels;

namespace apps.Tests;

/// <summary>
/// Covers <see cref="ConcurrencyHelper.WhenAll{T,TMessage}"/>, the fan-out combinator that
/// drives every scanner/checker stage. Guarantees: all results surface, a faulting producer
/// doesn't deadlock the channel, and cancellation propagates.
/// </summary>
public sealed class ConcurrencyHelperTests
{
    [Test]
    public async Task WhenAll_SurfacesEveryResult()
    {
        var input = Enumerable.Range(1, 100).ToArray();

        var results = new List<int>();
        await foreach (var n in input.WhenAll<int, int>(
                           async (item, writer, ct) => await writer.WriteAsync(item * 2, ct)))
        {
            results.Add(n);
        }

        await Assert.That(results.Count).IsEqualTo(100);
        await Assert.That(results.Order()).IsEquivalentTo(input.Select(i => i * 2));
    }

    [Test]
    public async Task WhenAll_MultipleWritesPerProducer_AllSurface()
    {
        var input = new[] { "a", "b" };

        var results = new List<string>();
        await foreach (var s in input.WhenAll<string, string>(
                           async (item, writer, ct) =>
                           {
                               await writer.WriteAsync($"{item}1", ct);
                               await writer.WriteAsync($"{item}2", ct);
                           }))
        {
            results.Add(s);
        }

        await Assert.That(results.Order()).IsEquivalentTo(new[] { "a1", "a2", "b1", "b2" });
    }

    [Test]
    public async Task WhenAll_EmptyInput_CompletesWithNoResults()
    {
        var results = new List<int>();
        await foreach (var n in Array.Empty<int>().WhenAll<int, int>(
                           async (item, writer, ct) => await writer.WriteAsync(item, ct)))
        {
            results.Add(n);
        }

        await Assert.That(results).IsEmpty();
    }

    [Test]
    public async Task WhenAll_FaultingProducer_DoesNotDeadlock_OthersStillSurface()
    {
        var input = new[] { 1, 2, 3 };

        var results = new List<int>();
        await foreach (var n in input.WhenAll<int, int>(Producer))
        {
            results.Add(n);
        }

        // The faulting producer (item 2) is swallowed; the rest must still surface.
        await Assert.That(results.Order()).IsEquivalentTo(new[] { 1, 3 });

        static async Task Producer(int item, ChannelWriter<int> writer, CancellationToken ct)
        {
            if (item == 2)
            {
                throw new InvalidOperationException("boom");
            }

            await writer.WriteAsync(item, ct);
        }
    }
}
