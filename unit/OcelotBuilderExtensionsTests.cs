using KubeClient;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ocelot.DependencyInjection;
using Ocelot.ServiceDiscovery;
using System.Reflection;

namespace Ocelot.Discovery.KubeClient.UnitTests;

public class OcelotBuilderExtensionsTests : UnitTest
{
    private readonly IServiceCollection _services;
    private readonly IConfiguration _configRoot;
    private IOcelotBuilder _ocelotBuilder;

    public OcelotBuilderExtensionsTests()
    {
        _configRoot = new ConfigurationRoot(new List<IConfigurationProvider>());
        _services = new ServiceCollection();
        _services.AddSingleton(GetHostingEnvironment());
        _services.AddSingleton(_configRoot);
    }

    private static IWebHostEnvironment GetHostingEnvironment()
    {
        var environment = new Mock<IWebHostEnvironment>();
        environment.Setup(e => e.ApplicationName)
            .Returns(typeof(OcelotBuilderExtensionsTests).GetTypeInfo().Assembly.GetName().Name);
        return environment.Object;
    }

    [Fact]
    [Trait("Feat", "345")] // https://github.com/ThreeMammals/Ocelot/issues/345
    [Trait("PR", "772")] // https://github.com/ThreeMammals/Ocelot/pull/772
    public void AddKubernetes_NoExceptions_ShouldSetUpKubernetes()
    {
        // Arrange
        _ocelotBuilder = _services.AddOcelot(_configRoot);

        // Act
        var actual = _ocelotBuilder.AddKubernetes();

        // Assert
        Assert.Same(_ocelotBuilder, actual);
    }

    [Fact]
    [Trait("Bug", "977")] // https://github.com/ThreeMammals/Ocelot/issues/977
    [Trait("PR", "2180")] // https://github.com/ThreeMammals/Ocelot/pull/2180
    public void AddKubernetes_DefaultServices_HappyPath()
    {
        // Arrange, Act
        _ocelotBuilder = _services.AddOcelot(_configRoot).AddKubernetes();

        // Assert
        AssertServices();
    }

    [Fact]
    [Trait("Feat", "2256")] // https://github.com/ThreeMammals/Ocelot/discussions/2256
    [Trait("PR", "2257")] // https://github.com/ThreeMammals/Ocelot/pull/2257
    [Trait("Commit", "4b6b96a")] // https://github.com/ThreeMammals/Ocelot/commit/4b6b96af6061ff2ea817c5408074baa1ab387735
    [Trait("Release", "24.0.0")] // https://github.com/ThreeMammals/Ocelot/releases/tag/24.0.0
    public void AddKubernetes_NoAction_HappyPath()
    {
        // Arrange
        Action<KubeClientOptions> noAction = null;
        _ocelotBuilder = _services.AddOcelot(_configRoot);

        // Act
        _ocelotBuilder.AddKubernetes(noAction);

        // Assert
        AssertServices();
        AssertService<IConfigureOptions<KubeClientOptions>>(); // not IOptions<KubeClientOptions>
    }

    private void AssertServices()
    {
        AssertService<IKubeApiClient>(); // 2180 scenario
        AssertService<ServiceDiscoveryFinderDelegate>();
        AssertService<IKubeServiceBuilder>();
        AssertService<IKubeServiceCreator>();
    }

    private void AssertService<T>(ServiceLifetime lifetime = ServiceLifetime.Singleton)
        where T : class
    {
        var descriptor = _services.SingleOrDefault(Of<T>);
        Assert.NotNull(descriptor);
        Assert.Equal(lifetime, descriptor.Lifetime);
    }

    private static bool Of<T>(ServiceDescriptor descriptor)
        where T : class
        => descriptor.ServiceType.Equals(typeof(T));
}
