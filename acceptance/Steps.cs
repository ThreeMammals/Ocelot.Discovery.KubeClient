using KubeClient;
using KubeClient.Models;
using KubeClient.ResourceClients;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newtonsoft.Json;
using Ocelot.Configuration.File;
using Ocelot.DependencyInjection;
using Ocelot.Infrastructure.Extensions;
using Ocelot.Testing.Steps;
using System.Net.Mime;
using System.Runtime.CompilerServices;

namespace Ocelot.Discovery.KubeClient.Acceptance;

public class Steps : DiscoverySteps
{
    public const string AccessToken = "txpc696iUhbVoudg164r93CxDTrKRVWG";
    protected readonly string _kubernetesUrl = DownstreamUrl(PortFinder.GetRandomPort());
    private readonly Action<KubeClientOptions> _kubeClientOptionsConfigure;

    public Steps()
    {
        _kubernetesUrl = DownstreamUrl(PortFinder.GetRandomPort());
        _kubeClientOptionsConfigure = opts =>
        {
            opts.ApiEndPoint = new Uri(_kubernetesUrl);
            opts.AccessToken = AccessToken;
            opts.AuthStrategy = KubeAuthStrategy.BearerToken;
            opts.AllowInsecure = true;
        };
    }

    public static JsonSerializerSettings JsonSerializerSettings
        => KubeResourceClient.SerializerSettings;

    public static readonly FileRoute[] NoRoutes = null;

    /// <summary>
    /// Dynamic Routing upstream path.
    /// </summary>
    public virtual string Path(string path = "/", [CallerMemberName] string serviceName = null)
        => $"/{ServiceNamespace()}.{serviceName}/{path.TrimStart('/')}"; // first segment is always Service Name

    private static ServiceDescriptor GetValidateScopesDescriptor()
        => ServiceDescriptor.Singleton<IServiceProviderFactory<IServiceCollection>>(
            new DefaultServiceProviderFactory(new() { ValidateScopes = true }));
    protected IOcelotBuilder AddKubernetes(IServiceCollection services) => services
        .Replace(GetValidateScopesDescriptor())
        .AddOcelot().AddKubernetes(_kubeClientOptionsConfigure);

    protected void WithKubernetes(IServiceCollection services) => AddKubernetes(services);

    public (string[], string[]) GivenTheServicesAreRegisteredWithKubernetes(int[] ports, [CallerMemberName] string serviceName = null)
    {
        // serviceName ??= ServiceName();
        var downstreamUrls = ports
            .Select(port => LoopbackLocalhostUrl(port, Array.IndexOf(ports, port))) // TODO Develop thread-safe version of the method
            .ToArray(); // based on localhost aka loopback network interface
        var downstreams = downstreamUrls.Select(url => new Uri(url))
            .ToList();
        var downstreamResponses = downstreams
            .Select(ds => $"{serviceName}:{ds.Host}:{ds.Port}")
            .ToArray();
        var subset = new EndpointSubsetV1();
        downstreams.ForEach(ds => GivenSubsetAddress(ds, subset));
        GivenEndpoints(subset, serviceName); // X service instances with different ports
        return (downstreamUrls, downstreamResponses);
    }

    protected EndpointsV1 _endpoints;
    public void GivenEndpoints(EndpointSubsetV1 subset, [CallerMemberName] string serviceName = "")
    {
        var e = new EndpointsV1()
        {
            Kind = "endpoint",
            ApiVersion = "1.0",
            Metadata = new()
            {
                Name = serviceName,
                Namespace = ServiceNamespace(),
            },
        };
        e.Subsets.Add(subset);
        _endpoints = e;
    }

    protected static EndpointSubsetV1 GivenSubsetAddress(Uri downstream, EndpointSubsetV1 subset = null)
    {
        subset ??= new();
        subset.Addresses.Add(new()
        {
            Ip = Dns.GetHostAddresses(downstream.Host).Select(x => x.ToString()).First(a => a.Contains('.')), // 127.0.0.1
            Hostname = downstream.Host,
        });
        subset.Ports.Add(new()
        {
            Name = downstream.Scheme,
            Port = downstream.Port,
        });
        return subset;
    }

    protected int _k8sCounter, _k8sServiceGeneration;
    public void ThenK8sShouldBeCalledExactly(int totalRequests) => _k8sCounter.ShouldBe(totalRequests);

#if NET9_0_OR_GREATER
    protected static readonly Lock K8sCounterLocker = new();
#else
    protected static readonly object K8sCounterLocker = new();
#endif

    private string _receivedToken;
    protected void ThenTheTokenIs(string token) => _receivedToken.ShouldBe(token);

    protected void GivenThereIsAFakeKubernetesProvider([CallerMemberName] string serviceName = nameof(ServiceDiscoveryTests))
        => GivenThereIsAFakeKubernetesProvider(_endpoints, true, 0, 0, serviceName, ServiceNamespace());

    protected const int MaxKubernetesDelay = 10; // ms
    protected void GivenThereIsAFakeKubernetesProvider(EndpointsV1 endpoints, bool isStable, int offlineServicesNo, int offlinePerThreads,
        [CallerMemberName] string serviceName = null, string namespaces = null)
    {
        _k8sCounter = 0;
        serviceName ??= ServiceName();
        namespaces ??= ServiceNamespace();
        handler.GivenThereIsAServiceRunningOn(_kubernetesUrl, MapServiceName);

        async Task MapServiceName(HttpContext context)
        {
            await Task.Delay(Random.Shared.Next(1, MaxKubernetesDelay)); // emulate integration delay up to 10 milliseconds
            var request = context.Request;
            if (request.Path.Value == $"/api/v1/namespaces/{namespaces}/endpoints/{serviceName}")
            {
                string json;
                lock (K8sCounterLocker)
                {
                    _k8sCounter++;
                    var subset = endpoints.Subsets[0];

                    // Each offlinePerThreads-th request to integrated K8s endpoint should fail
                    if (!isStable && _k8sCounter % offlinePerThreads == 0 && _k8sCounter >= offlinePerThreads)
                    {
                        while (offlineServicesNo-- > 0)
                        {
                            int index = subset.Addresses.Count - 1; // Random.Shared.Next(0, subset.Addresses.Count - 1);
                            subset.Addresses.RemoveAt(index);
                            subset.Ports.RemoveAt(index);
                        }
                        _k8sServiceGeneration++;
                    }

                    endpoints.Metadata.Generation = _k8sServiceGeneration;
                    json = JsonConvert.SerializeObject(endpoints, KubeResourceClient.SerializerSettings);
                }

                if (request.Headers.Authorization.Count > 0)
                    _receivedToken = request.Headers.Authorization.First();

                var response = context.Response;
                response.Headers.ContentType = MediaTypeNames.Application.Json;
                await response.WriteAsync(json);
            }

            await GivenHandleWatchRequest(context,
                [new() { EventType = ResourceEventType.Added, Resource = endpoints }],
                namespaces, serviceName);
        }
    }

    private readonly AutoResetEvent _k8sWatchResetEvent = new(false);
    protected int GivenWatchReceivedEvent() => _k8sWatchResetEvent.Set() ? 1 : 0;

    protected async Task GivenHandleWatchRequest(HttpContext context,
        IEnumerable<ResourceEventV1<EndpointsV1>> events,
        string namespaces, string serviceName)
    {
        await Task.Delay(Random.Shared.Next(1, 10)); // emulate integration delay up to 10 milliseconds
        var request = context.Request;
        if (request.Path.Value == $"/api/v1/watch/namespaces/{namespaces}/endpoints/{serviceName}")
        {
            _k8sCounter++;

            if (request.Headers.Authorization.Count > 0)
                _receivedToken = request.Headers.Authorization.First();

            var response = context.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.Headers.ContentType = MediaTypeNames.Application.Json;

            foreach (var @event in events)
            {
                _k8sWatchResetEvent.WaitOne();
                var json = JsonConvert.SerializeObject(@event, KubeResourceClient.SerializerSettings);
                await using var sw = new StreamWriter(response.Body);
                await sw.WriteLineAsync(json);
                await sw.FlushAsync();
                _k8sWatchResetEvent.Reset();
            }
            // keeping open connection like kube api will slow down tests
        }
    }
}
