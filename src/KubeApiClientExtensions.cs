namespace Ocelot.Discovery.KubeClient;

public static class KubeApiClientExtensions
{
    public static IEndPointClient EndpointsV1(this IKubeApiClient client)
        => client.ResourceClient<IEndPointClient>(x => new EndPointClientV1(x));
}
