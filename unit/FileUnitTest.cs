namespace Ocelot.Discovery.KubeClient.UnitTests;

public class FileUnitTest : FileUnit
{
    public override CancellationToken CancelMe => TestContext.Current.CancellationToken;
}
