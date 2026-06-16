using KubeClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;

namespace Ocelot.Discovery.KubeClient.UnitTests;

public sealed class KubeApiClientFactoryTests : KubeApiClientFactoryTestsBase
{
    [Theory]
    [Trait("Bug", "2299")]
    [InlineData(null)]
    [InlineData("")]
    public void ServiceAccountPath_NoValue_FallbackedToDefValue(string serviceAccountPath)
    {
        // Arrange
        var s = new FakeKubeApiClientFactory(serviceAccountPath);

        // Act
        var actual = s.ActualServiceAccountPath;

        // Assert
        Assert.NotNull(actual);
        Assert.NotEmpty(actual);
        Assert.Equal(KubeClientConstants.DefaultServiceAccountPath, actual);
    }

    [Collection(nameof(SequentialTests))]
    public class Sequential : KubeApiClientFactoryTestsBase
    {
        [Fact]
        [Trait("Bug", "2299")]
        public async Task Get_UsePodServiceAccount_ShouldCreateFromPodServiceAccount()
        {
            // Arrange
            var serviceAccountPath = Path.Combine(AppContext.BaseDirectory, TestID);
            var stub = new FakeKubeApiClientFactory(logger.Object, options.Object, serviceAccountPath);
            var expectedHost = IPAddress.Loopback.ToString();
            Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", expectedHost);
            int expectedPort = PortFinder.GetRandomPort();
            Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_PORT", expectedPort.ToString());

            folders.Add(serviceAccountPath);
            if (!Directory.Exists(serviceAccountPath))
            {
                Directory.CreateDirectory(serviceAccountPath);
            }

            var path = Path.Combine(serviceAccountPath, "namespace");
            await File.WriteAllTextAsync(path, nameof(Get_UsePodServiceAccount_ShouldCreateFromPodServiceAccount), TestContext.Current.CancellationToken);
            files.Add(path);

            path = Path.Combine(serviceAccountPath, "token");
            await File.WriteAllTextAsync(path, TestID, TestContext.Current.CancellationToken);
            files.Add(path);

            path = Path.Combine(serviceAccountPath, "ca.crt");
            await FakeKubeApiClientFactory.CreateCertificate(path);
            files.Add(path);

            var log = new Mock<ILogger>();
            logger.Setup(x => x.CreateLogger(It.IsAny<string>())).Returns(log.Object);

            try
            {
                // Act
                const bool UsePodServiceAccount = true;
                var actual = stub.Get(UsePodServiceAccount); // !

                // Assert
                Assert.NotNull(actual);
                Assert.IsType<KubeApiClient>(actual);
                Assert.NotNull(actual.ApiEndPoint);
                Assert.Equal(expectedHost, actual.ApiEndPoint.Host);
                Assert.Equal(expectedPort, actual.ApiEndPoint.Port);
                Assert.Equal(nameof(Get_UsePodServiceAccount_ShouldCreateFromPodServiceAccount), actual.DefaultNamespace);
                Assert.NotNull(actual.LoggerFactory);
                Assert.Equal(logger.Object, actual.LoggerFactory);
            }
            finally
            {
                Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_PORT", null);
            }
        }
    }
}

public class KubeApiClientFactoryTestsBase : FileUnitTest
{
    protected readonly Mock<ILoggerFactory> logger = new();
    protected readonly Mock<IOptions<KubeClientOptions>> options = new();
    protected KubeApiClientFactory sut;

    public KubeApiClientFactoryTestsBase()
    {
        sut = new(logger.Object, options.Object);
    }
}
