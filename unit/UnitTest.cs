namespace Ocelot.Discovery.KubeClient.UnitTests;

public class UnitTest : Unit
{
    public override CancellationToken CancelMe => TestContext.Current.CancellationToken;
}
