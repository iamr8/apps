using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace apps.Infrastructure;

public static class ConcurrencyHelper
{
    public delegate Task OnPublicationDelegate<in T, TMessage>(T item, ChannelWriter<TMessage> writer, CancellationToken cancellationToken);

    public static async IAsyncEnumerable<TMessage> WhenAll<T, TMessage>(
        this IEnumerable<T> tasks,
        OnPublicationDelegate<T, TMessage> onPublication,
        int capacity = 512,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<TMessage>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        var producerTask = Task.WhenAll(tasks.Select(task => onPublication(task, channel.Writer, cancellationToken)))
            .ContinueWith(t =>
            {
                // Observe the faulted task to prevent UnobservedTaskException in GC.
                _ = t.Exception;
                channel.Writer.TryComplete();
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        await foreach (var result in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return result;
        }

        await producerTask.ConfigureAwait(false);
    }
}