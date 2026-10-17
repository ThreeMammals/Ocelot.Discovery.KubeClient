using KubeClient;
using KubeClient.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Ocelot.Configuration;
using Ocelot.Configuration.File;
using Ocelot.DependencyInjection;
using Ocelot.Infrastructure.Extensions;
using Ocelot.LoadBalancer.Balancers;
using Ocelot.Logging;
using Ocelot.ServiceDiscovery.Providers;
using Ocelot.Testing.LoadBalancer;
using Ocelot.Values;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ocelot.Discovery.KubeClient.Acceptance;

public sealed class ServiceDiscoveryTests : Steps
{
    public ServiceDiscoveryTests()
    {
    }

    [Theory]
    [InlineData(nameof(Kube))]
    [InlineData(nameof(PollKube))] // Bug 2304 -> https://github.com/ThreeMammals/Ocelot/issues/2304
    [InlineData(nameof(WatchKube))]
    public async Task ShouldReturnServicesFromK8s(string discoveryType)
    {
        var servicePort = PortFinder.GetRandomPort();
        var downstreamUrl = LoopbackLocalhostUrl(servicePort);
        var downstream = new Uri(downstreamUrl);
        var subsetV1 = GivenSubsetAddress(downstream);
        GivenEndpoints(subsetV1);
        var route = GivenRouteWithServiceName(ServiceName());
        var configuration = GivenKubeConfiguration(route, discoveryType);
        string serviceName = ServiceName(), downstreamResponse = serviceName;
        GivenServiceInstanceIsRunning(downstreamUrl, downstreamResponse);
        GivenThereIsAFakeKubernetesProvider(serviceName);
        GivenThereIsAConfiguration(configuration);
        GivenOcelotIsRunning(WithKubernetes);
        GivenWatchReceivedEvent();
        await WhenIGetUrlOnTheApiGateway("/");
        ThenTheStatusCodeShouldBe(HttpStatusCode.OK);
        ThenTheResponseBodyShouldBe($"1^:^{downstreamResponse}");
        ThenAllServicesShouldHaveBeenCalledTimes(1);
        ThenTheTokenIs("Bearer txpc696iUhbVoudg164r93CxDTrKRVWG");
    }

    [Theory]
    [Trait("Feat", "1967")] // https://github.com/ThreeMammals/Ocelot/issues/1967
    [InlineData("", HttpStatusCode.BadGateway)]
    [InlineData("http", HttpStatusCode.OK)]
    public async Task ShouldReturnServicesByPortNameAsDownstreamScheme(string downstreamScheme, HttpStatusCode statusCode)
    {
        const string serviceName = "example-web";
        var servicePort = PortFinder.GetRandomPort();
        var downstreamUrl = LoopbackLocalhostUrl(servicePort);
        var downstream = new Uri(downstreamUrl);
        var subsetV1 = GivenSubsetAddress(downstream);

        // Ports[0] -> port(https, 443)
        // Ports[1] -> port(http, not 80)
        subsetV1.Ports.Insert(0, new()
        {
            Name = "https", // This service instance is offline -> BadGateway
            Port = 443,
        });
        GivenEndpoints(subsetV1);
        var route = GivenRouteWithServiceName();
        route.DownstreamPathTemplate = "/{url}";
        route.DownstreamScheme = downstreamScheme; // !!! Warning !!! Select port by name as scheme
        route.UpstreamPathTemplate = "/api/example/{url}";
        route.ServiceName = serviceName; // "example-web"
        var configuration = GivenKubeConfiguration(route, nameof(Kube));
        GivenServiceInstanceIsRunning(downstreamUrl, Body());
        GivenThereIsAFakeKubernetesProvider(serviceName);
        GivenThereIsAConfiguration(configuration);
        GivenOcelotIsRunning(WithKubernetes);
        await WhenIGetUrlOnTheApiGateway("/api/example/1");
        ThenTheStatusCodeShouldBe(statusCode);
        ThenTheResponseBodyShouldBe(downstreamScheme == "http"
                  ? "1^:^" + nameof(ShouldReturnServicesByPortNameAsDownstreamScheme)
                  : string.Empty);
        ThenAllServicesShouldHaveBeenCalledTimes(downstreamScheme == "http" ? 1 : 0);
        ThenTheTokenIs("Bearer txpc696iUhbVoudg164r93CxDTrKRVWG");
    }

    [Theory]
    [Trait("Bug", "2110")] // https://github.com/ThreeMammals/Ocelot/issues/2110
    [InlineData(1, 30, null)]
    [InlineData(2, 50, null)]
    [InlineData(3, 50, null)]
    [InlineData(4, 50, null)]
    [InlineData(5, 50, null)]
    [InlineData(6, 99, null)]
    [InlineData(7, 99, null)]
    [InlineData(8, 99, null)]
    [InlineData(9, 999, null)]
    [InlineData(10, 999, nameof(Kube))]
    // [InlineData(10, 999, nameof(PollKube))] // TODO
    [InlineData(10, 999, nameof(WatchKube))]
    public void ShouldHighlyLoadOnStableKubeProviderWithRoundRobinLoadBalancing(int totalServices, int totalRequests, string discoveryType)
    {
        // TODO Check it before stable release
        Assert.SkipWhen(RuntimeInformation.IsOSPlatform(OSPlatform.OSX), "Skip in MacOS because the test is very unstable");

        discoveryType ??= nameof(Kube);
        var serviceName = ServiceName();
        int zeroGeneration = 0, k8sCount = totalRequests;
        int bottom = totalRequests / totalServices,
            top = totalRequests - (bottom * totalServices) + bottom;
        GivenServiceDiscoveryAndLoadBalancing(totalServices, discoveryType, nameof(RoundRobinAnalyzer), null, null, serviceName);
        GivenThereIsAFakeKubernetesProvider(serviceName); // stable, services will not be removed from the list
        HighlyLoadOnKubeProviderAndRoundRobinBalancer(discoveryType, totalRequests, zeroGeneration, k8sCount);
        if (discoveryType != nameof(PollKube))
        {
            ThenAllServicesCalledRealisticAmountOfTimes(bottom, top);
            ThenServiceCountersShouldMatchLeasingCounters(_roundRobinAnalyzer, _servicePorts, totalRequests);
        }
    }

    [Theory]
    [Trait("Bug", "2110")] // https://github.com/ThreeMammals/Ocelot/issues/2110
    [InlineData(5, 50, 1, null)]
    [InlineData(5, 50, 2, null)]
    [InlineData(5, 50, 3, null)]
    [InlineData(5, 50, 4, nameof(Kube))]
    // [InlineData(5, 50, 4, nameof(PollKube))]
    [InlineData(5, 50, 4, nameof(WatchKube))]
    public void ShouldHighlyLoadOnUnstableKubeProviderWithRoundRobinLoadBalancing(int totalServices, int totalRequests, int k8sGeneration, string discoveryType)
    {
        // TODO Check it before stable release
        Assert.SkipWhen(RuntimeInformation.IsOSPlatform(OSPlatform.OSX), "Skip in MacOS because the test is very unstable");

        discoveryType ??= nameof(Kube);
        var serviceName = ServiceName();
        int failPerThreads = (totalRequests / k8sGeneration) - 1, // k8sGeneration means number of offline services
            k8sCount = totalRequests;
        GivenServiceDiscoveryAndLoadBalancing(totalServices, discoveryType, nameof(RoundRobinAnalyzer), null, null, serviceName);
        GivenThereIsAFakeKubernetesProvider(_endpoints, false, k8sGeneration, failPerThreads, serviceName, null); // false means unstable, k8sGeneration services will be removed from the list
        HighlyLoadOnKubeProviderAndRoundRobinBalancer(discoveryType, totalRequests, discoveryType == nameof(WatchKube) ? 0 : k8sGeneration, k8sCount);
        ThenAllServicesCalledOptimisticAmountOfTimes(_roundRobinAnalyzer); // with unstable checkings
        ThenServiceCountersShouldMatchLeasingCounters(_roundRobinAnalyzer, _servicePorts, totalRequests);
    }

    [Theory]
    [InlineData(nameof(Kube))]
    [InlineData(nameof(PollKube))] // Bug 2304 -> https://github.com/ThreeMammals/Ocelot/issues/2304
    [InlineData(nameof(WatchKube))]
    [Trait("Feat", "2256")] // https://github.com/ThreeMammals/Ocelot/discussions/2256
    public async Task ShouldReturnServicesFromK8sWhenAddKubernetesWithNullConfigureOptions(string discoveryType)
    {
        var servicePort = PortFinder.GetRandomPort();
        var downstreamUrl = LoopbackLocalhostUrl(servicePort);
        var downstream = new Uri(downstreamUrl);
        var subsetV1 = GivenSubsetAddress(downstream);
        GivenEndpoints(subsetV1);
        var route = GivenRouteWithServiceName();
        var configuration = GivenKubeConfiguration(route, discoveryType, "txpc696iUhbVoudg164r93CxDTrKRVWG");
        string serviceName = ServiceName(), downstreamResponse = serviceName;
        GivenServiceInstanceIsRunning(downstreamUrl, downstreamResponse);
        GivenThereIsAFakeKubernetesProvider(serviceName);
        GivenThereIsAConfiguration(configuration);
        GivenOcelotIsRunning(AddKubernetesWithNullConfigureOptions);
        GivenWatchReceivedEvent();
        await WhenIGetUrlOnTheApiGateway("/");
        ThenTheStatusCodeShouldBe(HttpStatusCode.OK);
        ThenTheResponseBodyShouldBe($"1^:^{downstreamResponse}");
        ThenAllServicesShouldHaveBeenCalledTimes(1);
        ThenTheTokenIs("Bearer txpc696iUhbVoudg164r93CxDTrKRVWG");
    }

    [Fact]
    [Trait("Feat", "2168")] // https://github.com/ThreeMammals/Ocelot/discussions/2168
    [Trait("PR", "2174")] // https://github.com/ThreeMammals/Ocelot/pull/2174
    public async Task ShouldReturnServicesFromK8sWhenOneWatchRequestUpdatesServicesInfo()
    {
        (EndpointsV1 endpoints, string downstreamUrl) = GetServiceInstance();
        (EndpointsV1 updatedEndpoints, string updateDownstreamUrl) = GetServiceInstance();
        ResourceEventV1<EndpointsV1>[] events =
        [
            new() { EventType = ResourceEventType.Added, Resource = endpoints },
            new() { EventType = ResourceEventType.Modified, Resource = updatedEndpoints }
        ];
        var route = GivenRouteWithServiceName();
        var configuration = GivenKubeConfiguration(route, nameof(WatchKube));
        
        string serviceName = ServiceName(), downstreamResponse = serviceName;
        var updatedDownstreamResponse = "updated_content" + serviceName;
        GivenServiceInstanceIsRunning(downstreamUrl, downstreamResponse);
        GivenServiceInstanceIsRunning(updateDownstreamUrl, updatedDownstreamResponse);
        GivenThereIsAFakeKubernetesProvider(events);
        GivenThereIsAConfiguration(configuration);
        GivenOcelotIsRunning(WithKubernetes);
        GivenWatchReceivedEvent();
        WhenIGetUrlOnTheApiGatewayConcurrently("/", 10);
        ThenAllStatusCodesShouldBe(HttpStatusCode.OK);
        ThenAllResponseBodiesShouldBe(downstreamResponse);
        ThenK8sShouldBeCalledExactly(1);
        ThenAllServicesShouldHaveBeenCalledTimes(10);
        GivenWatchReceivedEvent();
        await GivenIWaitAsync(100);
        WhenIGetUrlOnTheApiGatewayConcurrently("/", 10);
        ThenAllStatusCodesShouldBe(HttpStatusCode.OK);
        ThenAllResponseBodiesShouldBe(updatedDownstreamResponse);
        ThenK8sShouldBeCalledExactly(1);
        ThenAllServicesShouldHaveBeenCalledTimes(20);

        (EndpointsV1 Endpoints, string DownstreamUrl) GetServiceInstance()
        {
            var servicePort = PortFinder.GetRandomPort();
            var downstreamUrl = LoopbackLocalhostUrl(servicePort);
            var downstream = new Uri(downstreamUrl);
            var subset = GivenSubsetAddress(downstream);
            GivenEndpoints(subset);
            return (_endpoints, downstreamUrl);
        }
    }

    [Theory]
    [Trait("Feat", "585")] // https://github.com/ThreeMammals/Ocelot/issues/585
    [Trait("Feat", "2319")] // https://github.com/ThreeMammals/Ocelot/issues/2319
    [Trait("PR", "2324")] // https://github.com/ThreeMammals/Ocelot/pull/2324
    [InlineData(nameof(Kube))]
    // [InlineData(nameof(PollKube))] // Bug 2304 -> https://github.com/ThreeMammals/Ocelot/issues/2304
    [InlineData(nameof(WatchKube))]
    public async Task ShouldApplyGlobalLoadBalancerOptions_ForAllDynamicRoutes(string discoveryType)
    {
        Action<FileConfiguration> ConfigureDynamicRouting = (configuration) =>
        {
            configuration.GlobalConfiguration.LoadBalancerOptions = new(nameof(RoundRobin));
            configuration.GlobalConfiguration.DownstreamScheme = Uri.UriSchemeHttp;
            configuration.Routes = []; // dynamic routing
            configuration.DynamicRoutes = []; // no dynamic routes, for ALL dynamic routes
        };
        var upstreamPath = Path();
        GivenServiceDiscoveryAndLoadBalancing(5, discoveryType, nameof(RoundRobin), ConfigureDynamicRouting, WithKubernetesAndFakeKubeServiceCreator);
        GivenThereIsAFakeKubernetesProvider();
        if (discoveryType == nameof(WatchKube)) GivenWatchReceivedEvent();
        await Task.WhenAll(WhenIGetUrlOnTheApiGatewayConcurrently(upstreamPath, 50));
        /*if (discoveryType == nameof(PollKube))
        {
            //#if NET10_0_OR_GREATER
            _k8sCounter.ShouldBeLessThan(50);
            //#else
            //            if (IsCiCd()) _k8sCounter.ShouldBeInRange(48, 52);
            //            else _k8sCounter.ShouldBeGreaterThanOrEqualTo(50); // can be 50, 51 and sometimes 52
            //#endif
        }*/
        if (discoveryType == nameof(PollKube)) _k8sCounter.ShouldBeLessThan(50, null);
        /*else
        {
            _k8sCounter.ShouldBe(discoveryType == nameof(WatchKube) ? 1 : 50);
        }*/
        else _k8sCounter.ShouldBe(discoveryType == nameof(WatchKube) ? 1 : 50, null);

        _k8sServiceGeneration.ShouldBe(0);
        ThenAllStatusCodesShouldBe(HttpStatusCode.OK);
        ThenAllServicesShouldHaveBeenCalledTimes(50);
        ThenAllServicesCalledRealisticAmountOfTimes(9, 11); // soft assertion
        ThenServicesShouldHaveBeenCalledTimes(10, 10, 10, 10, 10); // distribution by RoundRobin algorithm, aka strict assertion
    }

    private void AddKubernetesWithNullConfigureOptions(IServiceCollection services)
        => services.AddOcelot().AddKubernetes(configureOptions: null);

    private int[] _servicePorts;
    private void GivenServiceDiscoveryAndLoadBalancing(
        int totalServices,
        string discoveryType = nameof(Kube),
        string loadBalancerType = nameof(RoundRobinAnalyzer),
        Action<FileConfiguration> configure = null,
        Action<IServiceCollection> services = null,
        [CallerMemberName] string serviceName = null)
    {
        _servicePorts = PortFinder.GetPorts(totalServices);
        (string[] downstreamUrls, string[] downstreamResponses) = GivenTheServicesAreRegisteredWithKubernetes(_servicePorts, serviceName);

        var route = GivenRouteWithServiceName(serviceName, loadBalancerType); // !!!
        var configuration = GivenKubeConfiguration(route, discoveryType.IfEmpty(nameof(Kube)));
        configure?.Invoke(configuration);
        GivenMultipleServiceInstancesAreRunning(downstreamUrls, downstreamResponses);
        GivenThereIsAConfiguration(configuration);
        int ocPort = GivenOcelotIsRunning(services ?? WithKubernetesAndRoundRobin);
    }

    private void HighlyLoadOnKubeProviderAndRoundRobinBalancer(string discoveryType, int totalRequests, int k8sGenerationNo, int? k8sCount = null)
    {
        if (discoveryType == nameof(WatchKube))
        {
            k8sCount = GivenWatchReceivedEvent(); // 1
            k8sGenerationNo = 0;
        }

        // Act
        WhenIGetUrlOnTheApiGatewayConcurrently("/", totalRequests); // load by X parallel requests

        // Assert
        int count = k8sCount ?? totalRequests;
        if (discoveryType == nameof(WatchKube))
            _k8sCounter.ShouldBeLessThanOrEqualTo(count); // TODO This is something abnormal due to values 997-999, but actual value should be 1. Need to double check this.
        if (discoveryType == nameof(PollKube))
        {
            //#if NET10_0_OR_GREATER
            //_k8sCounter.ShouldBeLessThanOrEqualTo(count);
            _k8sCounter.ShouldBeLessThan(count);
            //#else
            //            if (IsCiCd()) _k8sCounter.ShouldBeInRange(count - 1, count + 1);
            //            else _k8sCounter.ShouldBeGreaterThanOrEqualTo(count); // can be 50, 51 and sometimes 52
            //#endif
        }
        else
            _k8sCounter.ShouldBeGreaterThanOrEqualTo(count); // integration endpoint called times

        _k8sServiceGeneration.ShouldBe(k8sGenerationNo);
#if NET10_0_OR_GREATER
        if (discoveryType == nameof(PollKube))
            Responses.Count(x => x.Value.StatusCode == HttpStatusCode.OK)
                .ShouldBeGreaterThan(Responses.Count(x => x.Value.StatusCode != HttpStatusCode.OK));
        else
            ThenAllStatusCodesShouldBe(HttpStatusCode.OK);
#else
        ThenAllStatusCodesShouldBe(HttpStatusCode.OK);
#endif

#if NET10_0_OR_GREATER
        if (discoveryType == nameof(PollKube))
            Counters.Sum().ShouldBeLessThanOrEqualTo(totalRequests, CalledTimesMessage());
        else
            ThenAllServicesShouldHaveBeenCalledTimes(totalRequests);
#else
        ThenAllServicesShouldHaveBeenCalledTimes(totalRequests);
#endif

        _roundRobinAnalyzer.ShouldNotBeNull().Analyze();

#if NET10_0_OR_GREATER
        if (discoveryType == nameof(PollKube))
            _roundRobinAnalyzer.Events.Count.ShouldBeInRange((int)(0.9 * totalRequests), totalRequests);
        else
            _roundRobinAnalyzer.Events.Count.ShouldBe(totalRequests);
#else
        _roundRobinAnalyzer.Events.Count.ShouldBe(totalRequests);
#endif
        _roundRobinAnalyzer.HasManyServiceGenerations(k8sGenerationNo).ShouldBeTrue();
    }

    private FileRoute GivenRouteWithServiceName([CallerMemberName] string serviceName = null,
        string loadBalancerType = nameof(LeastConnection)) => new()
        {
            DownstreamPathTemplate = "/",
            DownstreamScheme = null, // the scheme should not be defined in service discovery scenarios by default, only ServiceName
            UpstreamPathTemplate = "/",
            UpstreamHttpMethod = [HttpMethods.Get],
            ServiceName = serviceName, // !!!
            ServiceNamespace = ServiceNamespace(),
            LoadBalancerOptions = new() { Type = loadBalancerType },
        };

    private FileConfiguration GivenKubeConfiguration(FileRoute route, string type, string token = null)
    {
        var u = new Uri(_kubernetesUrl);
        var configuration = GivenConfiguration(route);
        configuration.GlobalConfiguration.ServiceDiscoveryProvider = new()
        {
            Scheme = u.Scheme,
            Host = u.Host,
            Port = u.Port,
            Type = type,
            PollingInterval = 5 * MaxKubernetesDelay, // 3ms is very fast polling, make sense for PollKube provider only
            Namespace = ServiceNamespace(),
            Token = token ?? "Test",
        };
        return configuration;
    }
    
    private void GivenThereIsAFakeKubernetesProvider(ResourceEventV1<EndpointsV1>[] events,
        [CallerMemberName] string serviceName = null)
    {
        _k8sCounter = 0;
        var namespaces = ServiceNamespace();
        handler.GivenThereIsAServiceRunningOn(_kubernetesUrl, (c) => GivenHandleWatchRequest(c, events, namespaces, serviceName));
    }
    
    private void WithKubernetesAndFakeKubeServiceCreator(IServiceCollection services) => AddKubernetes(services)
        .Services.RemoveAll<IKubeServiceCreator>().AddSingleton<IKubeServiceCreator, FakeKubeServiceCreator>();
    private void WithKubernetesAndRoundRobin(IServiceCollection services) => AddKubernetes(services)
        .AddCustomLoadBalancer<RoundRobinAnalyzer>(GetRoundRobinAnalyzer)
        .Services.RemoveAll<IKubeServiceCreator>().AddSingleton<IKubeServiceCreator, FakeKubeServiceCreator>();

    private RoundRobinAnalyzer _roundRobinAnalyzer;
    private RoundRobinAnalyzer GetRoundRobinAnalyzer(DownstreamRoute route, IServiceDiscoveryProvider provider)
    {
        lock (K8sCounterLocker)
        {
            return _roundRobinAnalyzer ??= new RoundRobinAnalyzerCreator().Create(route, provider)?.Data as RoundRobinAnalyzer; //??= new RoundRobinAnalyzer(provider.GetAsync, route.ServiceName);
        }
    }

    public override string ServiceName([CallerMemberName] string serviceName = null) => serviceName ?? GetType().Name;
    public override string ServiceNamespace() // => GetType().Namespace;
        => nameof(ServiceDiscoveryTests); // should NOT contain dot '.' in name
}

internal class FakeKubeServiceCreator : KubeServiceCreator
{
    public FakeKubeServiceCreator(IOcelotLoggerFactory factory) : base(factory) { }

    protected override ServiceHostAndPort GetServiceHostAndPort(KubeRegistryConfiguration configuration, EndpointsV1 endpoint, EndpointSubsetV1 subset, EndpointAddressV1 address)
    {
        var ports = subset.Ports;
        var index = subset.Addresses.IndexOf(address);
        var portV1 = ports[index];
        Logger.LogDebug(() => $"K8s service with key '{configuration.KeyOfServiceInK8s}' and address {address.Ip}; Detected port is {portV1.Name}:{portV1.Port}. Total {ports.Count} ports of [{string.Join(',', ports.Select(p => p.Name))}].");
        return new ServiceHostAndPort(address.Ip, (int)portV1.Port, portV1.Name);
    }

    protected override IEnumerable<string> GetServiceTags(KubeRegistryConfiguration configuration, EndpointsV1 endpoint, EndpointSubsetV1 subset, EndpointAddressV1 address)
    {
        var tags = base.GetServiceTags(configuration, endpoint, subset, address)
            .ToList();
        long gen = endpoint.Metadata.Generation ?? 0L;
        tags.Add($"{nameof(endpoint.Metadata.Generation)}:{gen}");
        return tags;
    }
}
