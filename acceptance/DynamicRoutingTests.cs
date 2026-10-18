using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ocelot.Configuration;
using Ocelot.Configuration.File;
using Ocelot.LoadBalancer.Balancers;
using Ocelot.Logging;
using Ocelot.Requester;
using System.Collections.Concurrent;
using System.Net.Mime;
using System.Runtime.CompilerServices;

namespace Ocelot.Discovery.KubeClient.Acceptance;

public class DynamicRoutingTests : Steps
{
    [Fact]
    [Trait("Bug", "2352")] // https://github.com/ThreeMammals/Ocelot/issues/2352
    [Trait("PR", "2427")] // https://github.com/ThreeMammals/Ocelot/pull/2427
    [Trait("Release", "25.0.2")] // https://github.com/ThreeMammals/Ocelot/releases/tag/25.0.2
    public async Task ShouldRouteRequestWithChromiumClientHintHeaders()
    {
        var ports = PortFinder.GetPorts(2);
        (string[] downstreamUrls, string[] downstreamResponses) = GivenTheServicesAreRegisteredWithKubernetes(ports);
        GivenMultipleServiceInstancesAreRunning(downstreamUrls, downstreamResponses);
        var kubernetesUrl = GivenThereIsAFakeKubernetesProvider();
        var configuration = GivenDynamicRouting(kubernetesUrl);
        GivenThereIsAConfiguration(configuration);
        GivenOcelotIsRunning(WithKubernetesAndFakeKubeServiceCreator);
        GivenIAddAHeader("sec-ch-ua", "\"Microsoft Edge\";v=\"143\", \"Chromium\";v=\"143\", \"Not A(Brand\";v=\"24\"");
        GivenIAddAHeader("sec-ch-ua-mobile", "?0");
        await Task.WhenAll(
            WhenIGetUrlOnTheApiGatewayConcurrently(Path(), 2));
        ThenAllStatusCodesShouldBe(HttpStatusCode.OK);
        ThenAllServicesShouldHaveBeenCalledTimes(2);
        ThenServicesShouldHaveBeenCalledTimes(1, 1); // distribution by RoundRobin algorithm
    }

    [Fact]
    [Trait("Bug", "2428")] // https://github.com/ThreeMammals/Ocelot/issues/2428
    [Trait("PR", "2429")] // https://github.com/ThreeMammals/Ocelot/pull/2429
    [Trait("Release", "25.0.2")] // https://github.com/ThreeMammals/Ocelot/releases/tag/25.0.2
    public async Task ShouldForwardEachRequestToItsOwnDownstreamPath()
    {
        var ports = PortFinder.GetPorts(2);
        (string[] downstreamUrls, string[] downstreamResponses) = GivenTheServicesAreRegisteredWithKubernetes(ports);
        GivenMultipleServiceInstancesAreRunning(downstreamUrls, downstreamResponses);
        var kubernetesUrl = GivenThereIsAFakeKubernetesProvider();
        var configuration = GivenDynamicRouting(kubernetesUrl);
        GivenThereIsAConfiguration(configuration);
        GivenOcelotIsRunning(WithKubernetesAndFakeKubeServiceCreator);
        await WhenIGetUrlOnTheApiGateway(Path("/first"));
        ThenTheStatusCodeShouldBe(HttpStatusCode.OK);
        ThenTheResponseHeaderIs(HeaderNames.Path, "/first");
        await WhenIGetUrlOnTheApiGateway(Path("/second?id=2"));
        ThenTheStatusCodeShouldBe(HttpStatusCode.OK);
        ThenTheResponseHeaderIs(HeaderNames.Path, "/second?id=2");
        await WhenIPostUrlOnTheApiGateway(Path("/orders?flag&q=a+b"), "{}", MediaTypeNames.Application.Json);
        ThenTheStatusCodeShouldBe(HttpStatusCode.OK);
        ThenTheResponseHeaderIs(HeaderNames.Path, "/orders?flag&q=a+b");
        await WhenIDeleteUrlOnTheApiGateway(Path("/orders/7?force=true"));
        ThenTheStatusCodeShouldBe(HttpStatusCode.OK);
        ThenTheResponseHeaderIs(HeaderNames.Path, "/orders/7?force=true");
        ThenAllServicesShouldHaveBeenCalledTimes(4);
        ThenServicesShouldHaveBeenCalledTimes(2, 2); // distribution by RoundRobin algorithm
    }

    [Fact]
    [Trait("Bug", "2428")] // https://github.com/ThreeMammals/Ocelot/issues/2428
    [Trait("PR", "2429")] // https://github.com/ThreeMammals/Ocelot/pull/2429
    [Trait("Release", "25.0.2")] // https://github.com/ThreeMammals/Ocelot/releases/tag/25.0.2
    public async Task ShouldUseOneHttpHandlerForAllDownstreamPathsOfService()
    {
        var ports = PortFinder.GetPorts(2);
        (string[] downstreamUrls, string[] downstreamResponses) = GivenTheServicesAreRegisteredWithKubernetes(ports);
        GivenMultipleServiceInstancesAreRunning(downstreamUrls, downstreamResponses);
        var kubernetesUrl = GivenThereIsAFakeKubernetesProvider();
        var configuration = GivenDynamicRouting(kubernetesUrl);
        GivenThereIsAConfiguration(configuration);
        GivenOcelotIsRunning(WithKubernetesAndRequesterTesting);
        var paths = Enumerable.Range(1, 10).Select(i => $"/users/{i}").ToArray();
        var urls = paths.Select(p => Path(p)).ToArray();
        await Task.WhenAll(
            WhenIGetUrlOnTheApiGatewayConcurrently(urls.Length, urls)); // TODO As the first cold request, it sends 10 requests to the Consul discovery provider to return a service instance.
        ThenAllStatusCodesShouldBe(HttpStatusCode.OK);
        ThenAllServicesShouldHaveBeenCalledTimes(10);
        ThenServicesShouldHaveBeenCalledTimes(5, 5); // distribution by RoundRobin algorithm
        ThenEachResponsePathShouldBe(paths);
        ThenOneHttpHandlerShouldBeCreated(10);

        void WithKubernetesAndRequesterTesting(IServiceCollection services)
        {
            WithKubernetesAndFakeKubeServiceCreator(services);
            services
                .RemoveAll<IMessageInvokerPool>()
                .AddSingleton<IMessageInvokerPool, CountingMessageInvokerPool>();
        }
        void ThenEachResponsePathShouldBe(string[] paths)
        {
            foreach (var (index, response) in Responses)
            {
                response.ShouldNotBeNull().Headers.GetValues(HeaderNames.Path).ShouldHaveSingleItem()
                    .ShouldBe(paths[index], "Should forward the request to its own downstream path");
            }
        }
        void ThenOneHttpHandlerShouldBeCreated(int totalRequests, [CallerMemberName] string serviceName = null)
        {
            var pool = OcelotServices.GetService<IMessageInvokerPool>() as CountingMessageInvokerPool;
            pool.ShouldNotBeNull().CreatedHandlers.Keys.Count(route => route.ServiceName == serviceName)
                .ShouldBe(1, "Should use one HTTP handler for all downstream paths of the service");
            pool.Counter.ShouldBe(totalRequests);
            pool.AllHandlers.ShouldHaveSingleItem();
            var expected = pool.CreatedHandlers.Values.Single();
            pool.AllHandlers.ShouldAllBe(h => Equals(h, expected));
        }
    }

    private FileConfiguration GivenDynamicRouting(string kubernetesUrl)
    {
        var c = GivenConfiguration();
        c.DynamicRoutes = [];
        c.GlobalConfiguration = new()
        {
            // Don't enable Port Names feature
            // DownstreamScheme = Uri.UriSchemeHttp,
            ServiceDiscoveryProvider = new()
            {
                Type = nameof(Kube),
                Host = new Uri(kubernetesUrl).Host,
                Port = new Uri(kubernetesUrl).Port,
            },
            LoadBalancerOptions = new(nameof(RoundRobin)),
        };
        return c;
    }
}

// TODO Move to Ocelot.Testing project
public class CountingMessageInvokerPool : MessageInvokerPool, IMessageInvokerPool
{
    public CountingMessageInvokerPool(IDelegatingHandlerFactory handlerFactory, IOcelotLoggerFactory loggerFactory)
        : base(handlerFactory, loggerFactory) { }

    public volatile int Counter;
    public override HttpMessageInvoker Get(DownstreamRoute downstreamRoute)
    {
        var invoker = base.Get(downstreamRoute);
        _ = Interlocked.Increment(ref Counter);
        return invoker;
    }

    public readonly ConcurrentDictionary<DownstreamRoute, SocketsHttpHandler> CreatedHandlers = [];
    public readonly ConcurrentBag<SocketsHttpHandler> AllHandlers = [];
    protected override SocketsHttpHandler CreateHandler(DownstreamRoute route)
    {
        var handler = base.CreateHandler(route);
        CreatedHandlers[route] = handler;
        AllHandlers.Add(handler);
        return handler;
    }
}
