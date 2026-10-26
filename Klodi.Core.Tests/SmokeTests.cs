namespace Klodi.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void CoreAssemblyLoads()
    {
        Assert.Equal("Klodi.Core", typeof(Klodi.Core.KlodiInfo).Assembly.GetName().Name);
    }
}
