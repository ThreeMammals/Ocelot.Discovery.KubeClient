using KubeClient.Models;
using Ocelot.Values;

namespace Ocelot.Discovery.KubeClient;

public interface IKubeServiceBuilder
{
    IEnumerable<Service> BuildServices(KubeRegistryConfiguration configuration, EndpointsV1 endpoint);
}
