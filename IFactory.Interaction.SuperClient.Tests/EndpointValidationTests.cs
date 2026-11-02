using Xunit;
namespace IFactory.Interaction.SuperClient.Tests;

public class EndpointValidationTests
{
    [Theory]
    [InlineData("tcp://localhost:0/?Role=server")]
    [InlineData("udp://localhost:0/?RemoteEndPoint=127.0.0.1:9")]
    [InlineData("udp://127.0.0.1:0/?RemoteEndPoint=bad")]
    public async Task BindingConfigurationErrorsHaveAnArgumentExceptionWithUriContext(string uri)
    {
        await using var client = new SimplifiedClient(uri, new FixedSizePipelineFilter(1));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => client.Initialization);
        Assert.Contains(uri, error.Message); Assert.Equal("uri", error.ParamName);
    }
}
