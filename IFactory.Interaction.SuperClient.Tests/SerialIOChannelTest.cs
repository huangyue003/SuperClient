using System.IO.Ports;
using Xunit;

namespace IFactory.Interaction.SuperClient.Tests;

public class SerialIOChannelTest
{
    [Fact]
    public void ParsesSerialOptionsAndDefaults()
    {
        var endpoint = new SerialIOEndPoint("sio://COM2/?BaudRate=9600&Parity=Odd&StopBits=2&DataBits=7");
        Assert.Equal("com2", endpoint.PortName);
        Assert.Equal(9600, endpoint.BaudRate);
        Assert.Equal(Parity.Odd, endpoint.Parity);
        Assert.Equal(StopBits.Two, endpoint.StopBits);
        Assert.Equal(7, endpoint.Databits);
        Assert.Equal(StopBits.One, new SerialIOEndPoint("sio://COM2/?BaudRate=9600").StopBits);
    }
    [Theory]
    [InlineData("BaudRate=0")]
    [InlineData("BaudRate=9600&DataBits=264")]
    [InlineData("BaudRate=9600&Parity=99")]
    [InlineData("BaudRate=9600&StopBits=0")]
    [InlineData("BaudRate=9600&broken")]
    [InlineData("BaudRate=9600&baudrate=4800")]
    public void RejectsInvalidSerialOptions(string query)
        => Assert.ThrowsAny<Exception>(() => new SerialIOEndPoint("sio://COM2/?" + query));
}
