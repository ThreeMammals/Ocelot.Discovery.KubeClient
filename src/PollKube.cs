using Ocelot.Logging;
using Ocelot.Values;
using System.Collections.Concurrent;

namespace Ocelot.Discovery.KubeClient;

/// <summary>
/// It polls the <see cref="Kube"/> provider in the specified intervals and update the queue with new versions of services.
/// </summary>
public class PollKube(
    int pollingInterval,
    IOcelotLoggerFactory factory,
    IServiceDiscoveryProvider kubeProvider) : IServiceDiscoveryProvider, IDisposable
{
    private readonly IOcelotLogger _logger = factory.CreateLogger<PollKube>();
    private readonly IServiceDiscoveryProvider _provider = kubeProvider; // TODO IDisposable
    private readonly ConcurrentQueue<List<Service>> _queue = new();
    public static readonly List<Service> Empty = new(0);

    private Task _timing;
    private PeriodicTimer _timer = new(TimeSpan.FromMilliseconds(pollingInterval));
    private CancellationTokenSource _cts = new();
    private volatile bool _polling, _disposed, _stopped;

    public async Task<List<Service>> GetAsync()
    {
        _timing ??= StartAsync(); // (_cts.Token);
        if (_disposed || _cts.IsCancellationRequested)
            return Empty;

        // First cold request must call the provider
        if (_queue.IsEmpty)
        {
            return await PollAsync(_cts.Token);
        }
        else if (_polling && _queue.TryPeek(out var oldVersion))
        {
            return oldVersion;
        }

        // For services with multiple versions, remove outdated versions and retain only the latest one
        while (!_polling && _queue.Count > 1 && _queue.TryDequeue(out _))
        {
        }

        _queue.TryPeek(out var latestVersion);
        return latestVersion;
    }

    protected virtual async Task<List<Service>> PollAsync(CancellationToken token)
    {
        if (_disposed || token.IsCancellationRequested)
            return Empty;

        // Avoid polling if already in progress due to a slow completion of the PollAsync task,
        // and ensure no more than three versions of services remain in the queue.
        if (_polling || _queue.Count > 3)
            return Empty; // but don't enqueue

        try
        {
            _polling = true;
            var services = await _provider.GetAsync(); // TODO Add cancellation
            if (_disposed || token.IsCancellationRequested)
                return Empty;

            _queue.Enqueue(services);
            return services;
        }
        catch (ObjectDisposedException)
        {
            return Empty;
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>
    /// Endless task which should be stopped during disposing or when the provider is no longer needed.
    /// </summary>
    protected async Task StartAsync()
    {
        try
        {
            while (!_disposed && !_stopped &&
                await _timer.WaitForNextTickAsync(_cts.Token))
            {
                await PollAsync(_cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation aka _cts.Cancel()
        }
        finally
        {
            _queue.Clear();
        }
    }

    protected void Stop()
    {
        if (_disposed)
            return;

        _cts.Cancel();
        _timer?.Dispose();
        _stopped = true; // the flag ensures the loop will exit
        _timing?.GetAwaiter().GetResult(); // due to the flag this wait should complete in a reasonable time, in polling interval at most
        _timing?.Dispose();
        _cts.Dispose();
    }

    #region Dispose pattern
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            Stop();
            _logger?.Dispose();
        }

        //_cts = null;        
        _timer = null;
        _timing = null;
        _disposed = true;
    }

    ~PollKube() => Dispose(false);
    #endregion
}
