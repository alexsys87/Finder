using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Finder
{
    /// <summary>
    /// Pure, UI-independent implementation of the OpenWrt board discovery protocol.
    /// Kept free of Windows/WPF dependencies so it can be unit tested on any platform.
    /// </summary>
    public static class DiscoveryProtocol
    {
        public const int DiscoveryPort = 23;
        public const byte TagCmd = 0xFF;            // request marker
        public const byte TagStatus = 0xFE;         // response marker
        public const byte CmdDiscoverTarget = 0x02;

        public const int TitleOffset = 19;
        public const int TitleSize = 64;

        /// <summary>
        /// Builds the discovery request: TAG_CMD, length, command, checksum.
        /// The length field includes the checksum byte; the sum of all packet bytes is 0 (mod 256).
        /// </summary>
        public static byte[] BuildRequest()
        {
            byte[] request = new byte[4];
            request[0] = TagCmd;
            request[1] = 4;
            request[2] = CmdDiscoverTarget;
            unchecked
            {
                request[3] = (byte)(-(request[0] + request[1] + request[2]));
            }
            return request;
        }

        /// <summary>
        /// Computes the subnet's broadcast address from an IPv4 address and mask.
        /// Returns null when the inputs are not valid IPv4 addresses.
        /// </summary>
        public static IPAddress? GetBroadcastAddress(IPAddress? address, IPAddress? mask)
        {
            if (address == null || mask == null)
                return null;

            byte[] addr = address.GetAddressBytes();
            byte[] maskBytes = mask.GetAddressBytes();

            if (addr.Length != 4 || maskBytes.Length != 4)
                return null;

            byte[] broadcast = new byte[4];
            for (int i = 0; i < 4; i++)
                broadcast[i] = (byte)((addr[i] | ~maskBytes[i]) & 0xFF);

            return new IPAddress(broadcast);
        }

        /// <summary>
        /// Addresses the discovery request is broadcast to: the limited broadcast
        /// address always, plus the subnet broadcast when it is a usable one.
        /// </summary>
        public static List<IPEndPoint> GetBroadcastTargets(IPAddress? subnetBroadcast)
        {
            var targets = new List<IPEndPoint>
            {
                new IPEndPoint(IPAddress.Broadcast, DiscoveryPort)
            };

            if (subnetBroadcast != null &&
                !subnetBroadcast.Equals(IPAddress.Broadcast) &&
                !subnetBroadcast.Equals(IPAddress.Any))
            {
                targets.Add(new IPEndPoint(subnetBroadcast, DiscoveryPort));
            }

            return targets;
        }

        /// <summary>
        /// Parses a board's response.
        /// Format: 0 TAG_STATUS, 1 length, 2 CMD, 3 board type, 4 board ID,
        /// 5-8 client IP, 9-14 MAC, 15-18 version, 19-82 app title, followed by the checksum.
        /// The length field includes the checksum byte.
        /// </summary>
        /// <param name="macFallback">
        /// Invoked when the reply carries no MAC address; typically a Windows ARP lookup.
        /// </param>
        /// <returns>true and the parsed board when the packet is a valid discovery reply.</returns>
        public static bool TryParseResponse(
            byte[] buffer,
            int received,
            IPAddress remoteAddress,
            Func<IPAddress, string> macFallback,
            out BoardInfo? board)
        {
            board = null;

            if (received < 4)
                return false;

            if (buffer[0] != TagStatus || buffer[2] != CmdDiscoverTarget)
                return false;

            int length = buffer[1];
            if (length < 4 || length > received)
                return false;

            // The sum of all packet bytes must be 0 (mod 256)
            int sum = 0;
            for (int i = 0; i < length; i++)
                sum += buffer[i];
            if ((sum & 0xFF) != 0)
                return false;

            var parsed = new BoardInfo { Ip = remoteAddress.ToString() };

            if (length > 9)
            {
                byte[] clientIpBytes = new byte[4];
                Array.Copy(buffer, 5, clientIpBytes, 0, 4);
                parsed.ClientIp = new IPAddress(clientIpBytes).ToString();
            }

            if (length > 15)
            {
                byte[] macBytes = new byte[6];
                Array.Copy(buffer, 9, macBytes, 0, 6);
                parsed.Mac = BitConverter.ToString(macBytes).Replace('-', ':');
            }
            else
            {
                parsed.Mac = macFallback(remoteAddress);
            }

            if (length > 19)
                parsed.Version = $"{buffer[15]}.{buffer[16]}.{buffer[17]}.{buffer[18]}";

            if (length > 83)
            {
                // Truncate at the FIRST zero byte: the daemon's buffer may contain
                // garbage past the terminator. Decode as UTF-8, not ASCII.
                int end = TitleOffset;
                while (end < TitleOffset + TitleSize && buffer[end] != 0)
                    end++;

                parsed.App = Encoding.UTF8.GetString(buffer, TitleOffset, end - TitleOffset).Trim();
            }

            board = parsed;
            return true;
        }
    }
}
