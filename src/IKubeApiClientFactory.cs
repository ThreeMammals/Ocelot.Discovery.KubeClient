namespace Ocelot.Discovery.KubeClient;

public interface IKubeApiClientFactory
{
    KubeApiClient Get(bool usePodServiceAccount);
}
