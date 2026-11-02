using System.Net;
using Alcoler.Remote.Tests.Infrastructure;
using Xunit;

namespace Alcoler.Remote.Tests;

public sealed class NetworkPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.255.255.255", true)]
    [InlineData("::1", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.255.3.7", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("192.168.200.9", true)]
    [InlineData("169.254.10.20", true)]
    [InlineData("fd12:3456:789a::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("febf::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("::ffff:192.168.1.5", true)]
    [InlineData("172.15.255.255", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("11.0.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("fbff::1", false)]
    [InlineData("fec0::1", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void IsPrivateClient_ClassifiesAddresses(string address, bool expected)
    {
        Assert.Equal(expected, NetworkPolicy.IsPrivateClient(IPAddress.Parse(address)));
    }

    [Fact]
    public void IsPrivateClient_NullAddress_IsAllowed()
    {
        Assert.True(NetworkPolicy.IsPrivateClient(null));
    }
}
