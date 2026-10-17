using System.Net;
using System.Text;
using Xunit;

namespace Finder.Tests
{
    public class DiscoveryProtocolTests
    {
        // ---------------------------------------------------------------
        //  BuildRequest
        // ---------------------------------------------------------------

        [Fact]
        public void BuildRequest_ProducesExpectedBytes()
        {
            byte[] request = DiscoveryProtocol.BuildRequest();

            Assert.Equal(new byte[] { 0xFF, 0x04, 0x02, 0xFB }, request);
        }

        [Fact]
        public void BuildRequest_ChecksumMakesByteSumZero()
        {
            byte[] request = DiscoveryProtocol.BuildRequest();

            int sum = request.Sum(b => (int)b);

            Assert.Equal(0, sum & 0xFF);
        }

        // ---------------------------------------------------------------
        //  GetBroadcastAddress
        // ---------------------------------------------------------------

        [Theory]
        [InlineData("192.168.1.10", "255.255.255.0", "192.168.1.255")]
        [InlineData("192.168.1.130", "255.255.255.128", "192.168.1.255")]
        [InlineData("10.0.5.3", "255.255.0.0", "10.0.255.255")]
        [InlineData("172.16.4.7", "255.255.255.252", "172.16.4.7")]
        public void GetBroadcastAddress_ComputesSubnetBroadcast(string address, string mask, string expected)
        {
            IPAddress? result = DiscoveryProtocol.GetBroadcastAddress(
                IPAddress.Parse(address), IPAddress.Parse(mask));

            Assert.Equal(IPAddress.Parse(expected), result);
        }

        [Fact]
        public void GetBroadcastAddress_NullMask_ReturnsNull()
        {
            Assert.Null(DiscoveryProtocol.GetBroadcastAddress(IPAddress.Parse("192.168.1.10"), null));
        }

        [Fact]
        public void GetBroadcastAddress_NullAddress_ReturnsNull()
        {
            Assert.Null(DiscoveryProtocol.GetBroadcastAddress(null, IPAddress.Parse("255.255.255.0")));
        }

        [Fact]
        public void GetBroadcastAddress_NonIPv4_ReturnsNull()
        {
            Assert.Null(DiscoveryProtocol.GetBroadcastAddress(
                IPAddress.IPv6Loopback, IPAddress.IPv6Loopback));
        }

        // ---------------------------------------------------------------
        //  GetBroadcastTargets
        // ---------------------------------------------------------------

        [Fact]
        public void GetBroadcastTargets_NullSubnetBroadcast_ReturnsLimitedBroadcastOnly()
        {
            var targets = DiscoveryProtocol.GetBroadcastTargets(null);

            Assert.Single(targets);
            Assert.Equal(IPAddress.Broadcast, targets[0].Address);
            Assert.Equal(DiscoveryProtocol.DiscoveryPort, targets[0].Port);
        }

        [Fact]
        public void GetBroadcastTargets_SubnetBroadcast_ReturnsBothTargets()
        {
            var targets = DiscoveryProtocol.GetBroadcastTargets(IPAddress.Parse("192.168.1.255"));

            Assert.Equal(2, targets.Count);
            Assert.Contains(targets, t => t.Address.Equals(IPAddress.Broadcast));
            Assert.Contains(targets, t => t.Address.Equals(IPAddress.Parse("192.168.1.255")));
        }

        [Fact]
        public void GetBroadcastTargets_LimitedBroadcastGiven_IsNotDuplicated()
        {
            var targets = DiscoveryProtocol.GetBroadcastTargets(IPAddress.Broadcast);

            Assert.Single(targets);
        }

        [Fact]
        public void GetBroadcastTargets_AnyAddressGiven_IsIgnored()
        {
            var targets = DiscoveryProtocol.GetBroadcastTargets(IPAddress.Any);

            Assert.Single(targets);
        }

        // ---------------------------------------------------------------
        //  TryParseResponse
        // ---------------------------------------------------------------

        private static readonly IPAddress Remote = IPAddress.Parse("192.168.1.42");

        private static string NoMac(IPAddress ip) => "N/A";

        /// <summary>
        /// Builds a reply packet of the given total length (checksum included)
        /// with a valid trailing checksum. <paramref name="fill"/> writes the payload.
        /// </summary>
        private static byte[] BuildPacket(int length, Action<byte[]> fill)
        {
            var buffer = new byte[length];
            buffer[0] = DiscoveryProtocol.TagStatus;
            buffer[1] = (byte)length;
            buffer[2] = DiscoveryProtocol.CmdDiscoverTarget;
            fill(buffer);

            int sum = 0;
            for (int i = 0; i < length - 1; i++)
                sum += buffer[i];
            buffer[length - 1] = (byte)((-sum) & 0xFF);
            return buffer;
        }

        private static byte[] FullPacket(string title)
        {
            return BuildPacket(84, b =>
            {
                b[3] = 0x01;                                  // board type
                b[4] = 0x02;                                  // board ID
                Array.Copy(new byte[] { 10, 0, 0, 5 }, 0, b, 5, 4);   // client IP
                Array.Copy(new byte[] { 0x00, 0x0A, 0x35, 0x11, 0x22, 0x33 }, 0, b, 9, 6); // MAC
                Array.Copy(new byte[] { 1, 2, 3, 4 }, 0, b, 15, 4);   // version
                byte[] titleBytes = Encoding.UTF8.GetBytes(title);
                Array.Copy(titleBytes, 0, b, DiscoveryProtocol.TitleOffset,
                           Math.Min(titleBytes.Length, DiscoveryProtocol.TitleSize));
            });
        }

        [Fact]
        public void TryParseResponse_FullPacket_ParsesAllFields()
        {
            byte[] packet = FullPacket("FM SDR");

            bool ok = DiscoveryProtocol.TryParseResponse(packet, packet.Length, Remote, NoMac, out var board);

            Assert.True(ok);
            Assert.NotNull(board);
            Assert.Equal("192.168.1.42", board!.Ip);
            Assert.Equal("10.0.0.5", board.ClientIp);
            Assert.Equal("00:0A:35:11:22:33", board.Mac);
            Assert.Equal("1.2.3.4", board.Version);
            Assert.Equal("FM SDR", board.App);
        }

        [Fact]
        public void TryParseResponse_TitleStopsAtFirstZeroByte()
        {
            var title = new byte[DiscoveryProtocol.TitleSize];
            Array.Copy(Encoding.UTF8.GetBytes("Zynq Board"), 0, title, 0, 10);
            title[10] = 0;
            Array.Copy(Encoding.UTF8.GetBytes("garbage"), 0, title, 11, 7);

            byte[] packet = BuildPacket(84, b => Array.Copy(title, 0, b, DiscoveryProtocol.TitleOffset, DiscoveryProtocol.TitleSize));

            DiscoveryProtocol.TryParseResponse(packet, packet.Length, Remote, NoMac, out var board);

            Assert.Equal("Zynq Board", board!.App);
        }

        [Fact]
        public void TryParseResponse_TitleIsUtf8Decoded()
        {
            byte[] packet = FullPacket("Прибор");

            DiscoveryProtocol.TryParseResponse(packet, packet.Length, Remote, NoMac, out var board);

            Assert.Equal("Прибор", board!.App);
        }

        [Fact]
        public void TryParseResponse_ShortPacket_UsesMacFallback()
        {
            // Length 10: client IP present, MAC absent -> fallback is used.
            byte[] packet = BuildPacket(10, b => Array.Copy(new byte[] { 10, 0, 0, 5 }, 0, b, 5, 4));

            DiscoveryProtocol.TryParseResponse(packet, packet.Length, Remote, _ => "AA:BB:CC:DD:EE:FF", out var board);

            Assert.Equal("10.0.0.5", board!.ClientIp);
            Assert.Equal("AA:BB:CC:DD:EE:FF", board.Mac);
            Assert.Equal(string.Empty, board.Version);
            Assert.Equal(string.Empty, board.App);
        }

        [Fact]
        public void TryParseResponse_BadChecksum_IsRejected()
        {
            byte[] packet = FullPacket("FM SDR");
            packet[83] ^= 0xFF;   // corrupt the checksum

            bool ok = DiscoveryProtocol.TryParseResponse(packet, packet.Length, Remote, NoMac, out var board);

            Assert.False(ok);
            Assert.Null(board);
        }

        [Fact]
        public void TryParseResponse_WrongTag_IsRejected()
        {
            byte[] packet = FullPacket("FM SDR");
            packet[0] = 0x00;

            Assert.False(DiscoveryProtocol.TryParseResponse(packet, packet.Length, Remote, NoMac, out _));
        }

        [Fact]
        public void TryParseResponse_WrongCommand_IsRejected()
        {
            byte[] packet = FullPacket("FM SDR");
            packet[2] = 0x01;

            Assert.False(DiscoveryProtocol.TryParseResponse(packet, packet.Length, Remote, NoMac, out _));
        }

        [Fact]
        public void TryParseResponse_TooShort_IsRejected()
        {
            byte[] packet = new byte[] { DiscoveryProtocol.TagStatus, 0x02 };

            Assert.False(DiscoveryProtocol.TryParseResponse(packet, packet.Length, Remote, NoMac, out _));
        }

        [Fact]
        public void TryParseResponse_LengthBeyondReceived_IsRejected()
        {
            byte[] packet = FullPacket("FM SDR");

            // The header claims 84 bytes but only 20 were received.
            Assert.False(DiscoveryProtocol.TryParseResponse(packet, 20, Remote, NoMac, out _));
        }

        [Fact]
        public void TryParseResponse_LengthBelowMinimum_IsRejected()
        {
            byte[] packet = new byte[] { DiscoveryProtocol.TagStatus, 0x03, DiscoveryProtocol.CmdDiscoverTarget, 0x00 };

            Assert.False(DiscoveryProtocol.TryParseResponse(packet, packet.Length, Remote, NoMac, out _));
        }
    }
}
