using KubeClient.Models;
using Ocelot.Values;

namespace Ocelot.Discovery.KubeClient;

public interface IKubeServiceCreator
{
    IEnumerable<Service> Create(KubeRegistryConfiguration configuration, EndpointsV1 endpoint, EndpointSubsetV1 subset);
    IEnumerable<Service> CreateInstance(KubeRegistryConfiguration configuration, EndpointsV1 endpoint, EndpointSubsetV1 subset, EndpointAddressV1 address);
}
