using KubeClient;
using Microsoft.Extensions.DependencyInjection;
using Ocelot.Configuration;
using Ocelot.Configuration.Builder;
using Ocelot.Logging;
using Ocelot.Responses;
using Ocelot.ServiceDiscovery;
using Ocelot.ServiceDiscovery.Providers;
using Ocelot.Values;

namespace Ocelot.Discovery.KubeClient.UnitTests;

public class ServiceDiscoveryProviderFactoryTests : UnitTest
{
    private Response<IServiceDiscoveryProvider> _result;
    private ServiceDiscoveryProviderFactory _factory;
    private readonly Mock<IOcelotLoggerFactory> _loggerFactory;
    private readonly Mock<IOcelotLogger> _logger;
    private IServiceProvider _provider;
    private readonly IServiceCollection _collection;

    public ServiceDiscoveryProviderFactoryTests()
    {
        _loggerFactory = new Mock<IOcelotLoggerFactory>();
        _logger = new Mock<IOcelotLogger>();
        _collection = new ServiceCollection();
        _provider = _collection.BuildServiceProvider(true);
        _factory = new ServiceDiscoveryProviderFactory(_loggerFactory.Object, _provider);

        _loggerFactory.Setup(x => x.CreateLogger<ServiceDiscoveryProviderFactory>())
            .Returns(_logger.Object);
    }

    [Fact]
    [Trait("Feat", "17")] // https://github.com/ThreeMammals/Ocelot/issues/17
    [Trait("PR", "28")] // https://github.com/ThreeMammals/Ocelot/pull/28
    [Trait("Commit", "24dbb95")] // https://github.com/ThreeMammals/Ocelot/commit/24dbb958e34da4f040b150b0b95097b93da54d13
    [Trait("Release", "1.2.0")] // https://github.com/ThreeMammals/Ocelot/releases/tag/1.2.0
    public void Should_return_no_service_provider()
    {
        // Arrange
        var serviceConfig = new ServiceProviderConfigurationBuilder()
            .Build();
        var route = new DownstreamRouteBuilder().Build();

        // Act
        WhenIGetTheServiceProvider(serviceConfig, route);

        // Assert
        Assert.IsType<ConfigurationServiceProvider>(_result.Data);
    }

    [Fact]
    [Trait("Bug", "213")] // https://github.com/ThreeMammals/Ocelot/issues/213
    [Trait("PR", "211")] // https://github.com/ThreeMammals/Ocelot/pull/211
    [Trait("Commit", "3ac9b3b")] // https://github.com/ThreeMammals/Ocelot/commit/3ac9b3bd87424c1829e5ee480d38ee29d40b2b75
    [Trait("Release", "3.0.0")] // https://github.com/ThreeMammals/Ocelot/releases/tag/3.0.0
    public async Task Should_return_list_of_configuration_services()
    {
        // Arrange
        var serviceConfig = new ServiceProviderConfigurationBuilder()
            .Build();
        var downstreamAddresses = new List<DownstreamHostAndPort>
        {
            new("asdf.com", 80),
            new("abc.com", 80),
        };
        var route = new DownstreamRouteBuilder().WithDownstreamAddresses(downstreamAddresses).Build();

        // Act
        WhenIGetTheServiceProvider(serviceConfig, route);

        // Assert
        Assert.IsType<ConfigurationServiceProvider>(_result.Data);

        // Assert: Then The Following Services Are Returned
        var result = (ConfigurationServiceProvider)_result.Data;
        var services = await result.GetAsync();
        for (var i = 0; i < services.Count; i++)
        {
            var service = services[i];
            var downstreamAddress = downstreamAddresses[i];

            Assert.Equal(downstreamAddress.Host, service.HostAndPort.DownstreamHost);
            Assert.Equal(downstreamAddress.Port, service.HostAndPort.DownstreamPort);
        }
    }

    [Fact]
    [Trait("Feat", "238")] // https://github.com/ThreeMammals/Ocelot/issues/238
    [Trait("PR", "242")] // https://github.com/ThreeMammals/Ocelot/pull/242
    [Trait("Commit", "454ba3f")] // https://github.com/ThreeMammals/Ocelot/commit/454ba3f9a03d79dfd493d67d6a02c0a2eb1d0c4b
    [Trait("Release", "3.1.8")] // https://github.com/ThreeMammals/Ocelot/releases/tag/3.1.8
    public void Should_return_service_fabric_provider()
    {
        // Arrange
        var route = new DownstreamRouteBuilder()
            .WithServiceName("product")
            .Build();
        var serviceConfig = new ServiceProviderConfigurationBuilder()
            .WithType("ServiceFabric")
            .Build();
        GivenAFakeDelegate();

        // Act
        WhenIGetTheServiceProvider(serviceConfig, route);

        // Assert
        Assert.IsType<ServiceFabricServiceDiscoveryProvider>(_result.Data);
    }

    [Fact]
    [Trait("Bug", "580")] // https://github.com/ThreeMammals/Ocelot/issues/580
    [Trait("PR", "584")] // https://github.com/ThreeMammals/Ocelot/pull/584
    [Trait("Commit", "55277ca")] // https://github.com/ThreeMammals/Ocelot/commit/55277cac4569f28ed98df3fe95f7634f889d88fc
    [Trait("Release", "11.0.0")] // https://github.com/ThreeMammals/Ocelot/releases/tag/11.0.0
    public void Should_return_provider_because_type_matches_reflected_type_from_delegate()
    {
        // Arrange
        var route = new DownstreamRouteBuilder()
            .WithServiceName("product")
            .Build();
        var serviceConfig = new ServiceProviderConfigurationBuilder()
            .WithType(nameof(FakeDiscovery))
            .Build();
        GivenAFakeDelegate();

        // Act
        WhenIGetTheServiceProvider(serviceConfig, route);

        // Assert
        Assert.IsType<FakeDiscovery>(_result.Data);
    }

    [Fact]
    [Trait("Bug", "580")] // https://github.com/ThreeMammals/Ocelot/issues/580
    [Trait("PR", "584")] // https://github.com/ThreeMammals/Ocelot/pull/584
    [Trait("Commit", "55277ca")] // https://github.com/ThreeMammals/Ocelot/commit/55277cac4569f28ed98df3fe95f7634f889d88fc
    [Trait("Release", "11.0.0")] // https://github.com/ThreeMammals/Ocelot/releases/tag/11.0.0
    public void Should_not_return_provider_because_type_doesnt_match_reflected_type_from_delegate()
    {
        // Arrange
        var route = new DownstreamRouteBuilder()
            .WithServiceName("product")
            .Build();
        var serviceConfig = new ServiceProviderConfigurationBuilder()
            .WithType("Wookie")
            .Build();
        GivenAFakeDelegate();

        // Act
        WhenIGetTheServiceProvider(serviceConfig, route);

        // Assert
        Assert.True(_result.IsError);
        Assert.Equal(1, _result.Errors.Count);

        Assert.NotNull(_logInformationMessages);
        Assert.Equal(2, _logInformationMessages.Count);
        _logger.Verify(x => x.LogInformation(It.IsAny<Func<string>>()),
            Times.Exactly(2));

        Assert.NotNull(_logWarningMessages);
        Assert.Equal(1, _logWarningMessages.Count);
        _logger.Verify(x => x.LogWarning(It.IsAny<Func<string>>()),
            Times.Once());
    }

    [Theory]
    [InlineData("Kube", true)]
    [InlineData("kube", true)]
    [InlineData("PollKube", true)]
    [InlineData("pollkube", true)]
    [InlineData("unknown", false)]
    [Trait("Bug", "1954")] // https://github.com/ThreeMammals/Ocelot/pull/1954
    [Trait("Commit", "a1607f5")] // https://github.com/ThreeMammals/Ocelot/commit/a1607f5aa59ba9bf9354f40d16f0fac929743ca0
    [Trait("Release", "23.0.0")] // https://github.com/ThreeMammals/Ocelot/releases/tag/23.0.0
    public void Should_return_Kubernetes_provider_with_type_names_from_docs(string typeName, bool success)
    {
        // Arrange
        var route = new DownstreamRouteBuilder()
            .WithServiceName(TestName())
            .Build();
        var serviceConfig = new ServiceProviderConfigurationBuilder()
            .WithType(typeName)
            .WithPollingInterval(Timeout.Infinite)
            .Build();

        // Arrange: Given Kubernetes Provider
        var k8sClient = new Mock<IKubeApiClient>();
        _collection
            .AddSingleton(KubernetesProviderFactory.Get)
            .AddSingleton(k8sClient.Object)
            .AddSingleton(_loggerFactory.Object);
        _provider = _collection.BuildServiceProvider(true);
        _factory = new ServiceDiscoveryProviderFactory(_loggerFactory.Object, _provider);

        // Act
        WhenIGetTheServiceProvider(serviceConfig, route);

        // Assert
        if (success)
        {
            Assert.IsType<OkResponse<IServiceDiscoveryProvider>>(_result);
        }
        else
        {
            Assert.IsType<ErrorResponse<IServiceDiscoveryProvider>>(_result);
        }
    }

    private void GivenAFakeDelegate()
    {
        static IServiceDiscoveryProvider fake(IServiceProvider provider, ServiceProviderConfiguration config, DownstreamRoute name) => new FakeDiscovery();
        _collection.AddSingleton((ServiceDiscoveryFinderDelegate)fake);
        _provider = _collection.BuildServiceProvider(true);
        _factory = new ServiceDiscoveryProviderFactory(_loggerFactory.Object, _provider);
    }

    private class FakeDiscovery : IServiceDiscoveryProvider
    {
        public Task<List<Service>> GetAsync() => null;
    }

    private readonly List<string> _logInformationMessages = [];
    private readonly List<string> _logWarningMessages = [];

    private void WhenIGetTheServiceProvider(ServiceProviderConfiguration serviceConfig, DownstreamRoute route)
    {
        _logger.Setup(x => x.LogInformation(It.IsAny<Func<string>>()))
            .Callback<Func<string>>(myFunc => _logInformationMessages.Add(myFunc.Invoke()));
        _logger.Setup(x => x.LogWarning(It.IsAny<Func<string>>()))
            .Callback<Func<string>>(myFunc => _logWarningMessages.Add(myFunc.Invoke()));

        _result = _factory.Get(serviceConfig, route);
    }
}
