using Deskswap.Core;

namespace Deskswap.Core.Tests;

public class ProductInfoTests
{
    [Fact]
    public void Name_is_Deskswap()
    {
        Assert.Equal("Deskswap", ProductInfo.Name);
    }
}
