using Newtonsoft.Json;
using _KubeClient_ = KubeClient;

namespace Ocelot.Discovery.KubeClient.Acceptance;

public class Steps : AcceptanceSteps
{
    public static JsonSerializerSettings JsonSerializerSettings
        => _KubeClient_.ResourceClients.KubeResourceClient.SerializerSettings;

}
