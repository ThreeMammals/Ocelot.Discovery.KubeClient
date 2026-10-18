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
    // Original test name: should_return_service_from_k8s
    // Path: test/Ocelot.UnitTests/Kubernetes/KubeServiceDiscoveryProviderTests.cs
    [Theory]
    [InlineData(nameof(Kube))]
    [InlineData(nameof(PollKube))] // Bug 2304 -> https://github.com/ThreeMammals/Ocelot/issues/2304
    [InlineData(nameof(WatchKube))]
    [Trait("Feat", "345")] // https://github.com/ThreeMammals/Ocelot/issues/345
    [Trait("PR", "772")] // https://github.com/ThreeMammals/Ocelot/pull/772
    [Trait("Release", "13.2.0")] // https://github.com/ThreeMammals/Ocelot/releases/tag/13.2.0
    public async Task ShouldReturnServicesFromK8s(string discoveryType)
    {
        var servicePort = PortFinder.GetRandomPort();
        (string[] downstreamUrls, string[] downstreamResponses) = GivenTheServicesAreRegisteredWithKubernetes([servicePort]);
        var staticRoute = GivenRouteWithServiceName();
        GivenServiceInstanceIsRunning(downstreamUrls[0], downstreamResponses[0]);
        var kubernetesUrl = GivenThereIsAFakeKubernetesProvider();
        var configuration = GivenKubeConfiguration(kubernetesUrl, staticRoute, discoveryType);
        GivenThereIsAConfiguration(configuration);
        int ocelot = GivenOcelotIsRunning(WithKubernetes);
        GivenWatchReceivedEvent();
        await WhenIGetUrlOnTheApiGateway("/");
        ThenTheStatusCodeShouldBe(HttpStatusCode.OK);
        ThenTheResponseBodyShouldBe($"1^:^{downstreamResponses[0]}");
        ThenAllServicesShouldHaveBeenCalledTimes(1);
        ThenTheTokenIs("Bearer " + AccessToken);
    }

    [Theory]
    [InlineData("", HttpStatusCode.BadGateway)]
    [InlineData("http", HttpStatusCode.OK)]
    [Trait("Feat", "1967")] // https://github.com/ThreeMammals/Ocelot/issues/1967
    [Trait("PR", "2052")] // https://github.com/ThreeMammals/Ocelot/pull/2052
    [Trait("Release", "23.3.0")] // https://github.com/ThreeMammals/Ocelot/releases/tag/23.3.0
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
        GivenServiceInstanceIsRunning(downstreamUrl, Body());
        var kubernetesUrl = GivenThereIsAFakeKubernetesProvider(serviceName);
        var configuration = GivenKubeConfiguration(kubernetesUrl, route, nameof(Kube));
        GivenThereIsAConfiguration(configuration);
        GivenOcelotIsRunning(WithKubernetes);
        await WhenIGetUrlOnTheApiGateway("/api/example/1");
        ThenTheStatusCodeShouldBe(statusCode);
        ThenTheResponseBodyShouldBe(downstreamScheme == Uri.UriSchemeHttp
                  ? "1^:^" + TestName()
                  : string.Empty);
        ThenAllServicesShouldHaveBeenCalledTimes(downstreamScheme == Uri.UriSchemeHttp ? 1 : 0);
        ThenTheTokenIs("Bearer " + AccessToken);
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
        int zeroGeneration = 0, k8sCount = totalRequests;
        int bottom = totalRequests / totalServices,
            top = totalRequests - (bottom * totalServices) + bottom;
        var ports = PortFinder.GetPorts(totalServices);
        (string[] downstreamUrls, string[] downstreamResponses) = GivenTheServicesAreRegisteredWithKubernetes(ports);
        var route = GivenRouteWithServiceName(nameof(RoundRobinAnalyzer)); // !!!
        GivenMultipleServiceInstancesAreRunning(downstreamUrls, downstreamResponses);
        var kubernetesUrl = GivenThereIsAFakeKubernetesProvider(); // stable, services will not be removed from the list
        var configuration = GivenKubeConfiguration(kubernetesUrl, route, discoveryType.IfEmpty(nameof(Kube)));
        GivenThereIsAConfiguration(configuration);
        int ocPort = GivenOcelotIsRunning(WithKubernetesAndRoundRobin);
        HighlyLoadOnKubeProviderAndRoundRobinBalancer(discoveryType, totalRequests, zeroGeneration, k8sCount);
        if (discoveryType != nameof(PollKube))
        {
            ThenAllServicesCalledRealisticAmountOfTimes(bottom, top);
            ThenServiceCountersShouldMatchLeasingCounters(_roundRobinAnalyzer, ports, totalRequests);
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
        int failPerThreads = (totalRequests / k8sGeneration) - 1, // k8sGeneration means number of offline services
            k8sCount = totalRequests;
        var ports = PortFinder.GetPorts(totalServices);
        (string[] downstreamUrls, string[] downstreamResponses) = GivenTheServicesAreRegisteredWithKubernetes(ports);
        var route = GivenRouteWithServiceName(nameof(RoundRobinAnalyzer)); // !!!
        GivenMultipleServiceInstancesAreRunning(downstreamUrls, downstreamResponses);
        var kubernetesUrl = GivenThereIsAFakeKubernetesProvider(_endpoints, false, k8sGeneration, failPerThreads); // false means unstable, k8sGeneration services will be removed from the list
        var configuration = GivenKubeConfiguration(kubernetesUrl, route, discoveryType.IfEmpty(nameof(Kube)));
        GivenThereIsAConfiguration(configuration);
        int ocPort = GivenOcelotIsRunning(WithKubernetesAndRoundRobin);
        HighlyLoadOnKubeProviderAndRoundRobinBalancer(discoveryType, totalRequests, discoveryType == nameof(WatchKube) ? 0 : k8sGeneration, k8sCount);
        ThenAllServicesCalledOptimisticAmountOfTimes(_roundRobinAnalyzer); // with unstable checkings
        ThenServiceCountersShouldMatchLeasingCounters(_roundRobinAnalyzer, ports, totalRequests);
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
        string serviceName = ServiceName(), downstreamResponse = serviceName;
        GivenServiceInstanceIsRunning(downstreamUrl, downstreamResponse);
        var kubernetesUrl = GivenThereIsAFakeKubernetesProvider(serviceName);
        var configuration = GivenKubeConfiguration(kubernetesUrl, route, discoveryType, "txpc696iUhbVoudg164r93CxDTrKRVWG");
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
        string serviceName = ServiceName(), downstreamResponse = serviceName;
        var updatedDownstreamResponse = "updated_content" + serviceName;
        GivenServiceInstanceIsRunning(downstreamUrl, downstreamResponse);
        GivenServiceInstanceIsRunning(updateDownstreamUrl, updatedDownstreamResponse);
        var kubernetesUrl = GivenThereIsAFakeKubernetesProvider(events);
        var configuration = GivenKubeConfiguration(kubernetesUrl, route, nameof(WatchKube));
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
        var ports = PortFinder.GetPorts(5);
        (string[] downstreamUrls, string[] downstreamResponses) = GivenTheServicesAreRegisteredWithKubernetes(ports);
        var route = GivenRouteWithServiceName(nameof(RoundRobin)); // !!!
        GivenMultipleServiceInstancesAreRunning(downstreamUrls, downstreamResponses);
        var kubernetesUrl = GivenThereIsAFakeKubernetesProvider();

        var configuration = GivenKubeConfiguration(kubernetesUrl, route, discoveryType.IfEmpty(nameof(Kube)));
        configuration.GlobalConfiguration.LoadBalancerOptions = new(nameof(RoundRobin));
        configuration.GlobalConfiguration.DownstreamScheme = Uri.UriSchemeHttp;
        configuration.Routes = []; // dynamic routing
        configuration.DynamicRoutes = []; // no dynamic routes, for ALL dynamic routes
        GivenThereIsAConfiguration(configuration);
        int ocPort = GivenOcelotIsRunning(WithKubernetesAndFakeKubeServiceCreator);

        if (discoveryType == nameof(WatchKube)) GivenWatchReceivedEvent();
        var upstreamPath = Path();
        await Task.WhenAll(
            WhenIGetUrlOnTheApiGatewayConcurrently(upstreamPath, 50));
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

    private FileRoute GivenRouteWithServiceName(
        string loadBalancerType = nameof(LeastConnection), [CallerMemberName] string serviceName = null)
        => new()
        {
            DownstreamPathTemplate = "/",
            DownstreamScheme = null, // the scheme should not be defined in service discovery scenarios by default, only ServiceName
            UpstreamPathTemplate = "/",
            UpstreamHttpMethod = [HttpMethods.Get],
            ServiceName = serviceName, // !!!
            ServiceNamespace = ServiceNamespace(),
            LoadBalancerOptions = new() { Type = loadBalancerType },
        };

    private FileConfiguration GivenKubeConfiguration(string kubernetesUrl, FileRoute route, string type, string token = null)
    {
        var k8s = new Uri(kubernetesUrl);
        var configuration = GivenConfiguration(route);
        configuration.GlobalConfiguration.ServiceDiscoveryProvider = new()
        {
            Scheme = k8s.Scheme,
            Host = k8s.Host,
            Port = k8s.Port,
            Type = type,
            PollingInterval = 5 * MaxKubernetesDelay, // 3ms is very fast polling, make sense for PollKube provider only
            Namespace = ServiceNamespace(),
            Token = token ?? ServiceNamespace(),
        };
        return configuration;
    }
    
    private string GivenThereIsAFakeKubernetesProvider(ResourceEventV1<EndpointsV1>[] events,
        [CallerMemberName] string serviceName = null)
    {
        _k8sCounter = 0;
        var @namespace = ServiceNamespace();
        var kubernetesUrl = SetupKubernetes();
        handler.GivenThereIsAServiceRunningOn(kubernetesUrl, (c) => GivenHandleWatchRequest(c, events, @namespace, serviceName));
        return kubernetesUrl;
    }
    
    private void WithKubernetesAndRoundRobin(IServiceCollection services) => AddKubernetes(services)
        .AddCustomLoadBalancer<RoundRobinAnalyzer>(GetRoundRobinAnalyzer)
        .Services.RemoveAll<IKubeServiceCreator>().AddSingleton<IKubeServiceCreator, FakeKubeServiceCreator>();

    private RoundRobinAnalyzer _roundRobinAnalyzer;
    private RoundRobinAnalyzer GetRoundRobinAnalyzer(DownstreamRoute route, IServiceDiscoveryProvider provider)
    {
        lock (K8sCounterLocker)
        {
            return _roundRobinAnalyzer ??= new RoundRobinAnalyzerCreator().Create(route, provider)?.Data as RoundRobinAnalyzer; //??= new RoundRobinAnalyzer(provider.GetAsync, staticRoute.ServiceName);
        }
    }

    public override string ServiceName([CallerMemberName] string serviceName = null) => serviceName ?? GetType().Name;
    public override string ServiceNamespace() // => GetType().Namespace;
        => nameof(ServiceDiscoveryTests); // should NOT contain dot '.' in name
}
