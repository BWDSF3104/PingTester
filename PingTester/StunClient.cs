using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace PingTester
{
    /// <summary>
    /// RFC 5389 に基づく STUN Binding Request/Response の送受信クラス。
    /// </summary>
    public static class StunClient
    {
        // Google 公開 STUN サーバ（メイン／フォールバック）
        // UdpPingServer から再利用するため internal に変更
        internal static readonly (string host, int port)[] StunServers =
        {
            ("stun.l.google.com",      19302),
            ("stun1.l.google.com",     19302),
            ("stun.cloudflare.com",    3478),
        };

        private const uint MagicCookie = 0x2112A442;

        /// <summary>
        /// STUN で外部 IP:ポート を取得する。
        /// 全サーバ失敗時は null を返す。
        /// </summary>
        /// <param name="localPort">バインドするローカル UDP ポート</param>
        /// <param name="timeoutMs">1サーバあたりのタイムアウト(ms)</param>
        [Obsolete("UdpPingServer.GetExternalEndPointAsync() を使用してください。サーバソケット共有により NAT マッピングが一致します。")]
        public static async Task<IPEndPoint> GetExternalEndPointAsync(
            int localPort, int timeoutMs = 3000)
        {
            foreach (var (host, port) in StunServers)
            {
                try
                {
                    IPEndPoint result = await QueryStunServerAsync(host, port, localPort, timeoutMs);
                    if (result != null) return result;
                }
                catch
                {
                    // 次のサーバへ
                }
            }
            return null;
        }

        private static async Task<IPEndPoint> QueryStunServerAsync(
            string host, int stunPort, int localPort, int timeoutMs)
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host);
            if (addresses.Length == 0) return null;

            IPEndPoint stunEP = new IPEndPoint(addresses[0], stunPort);
            byte[] transactionId = new byte[12];
            new Random().NextBytes(transactionId);

            byte[] request = BuildBindingRequest(transactionId);

            // [2026-04-18 修正] ポート 0 ではなく引数の localPort を実際に使用する
            //                   ポート 0 では UdpPingServer と異なる NAT マッピングが生成され
            //                   相手から届いたパケットをサーバが受信できなかった
            using (UdpClient udp = new UdpClient(localPort))
            {
                udp.Client.ReceiveTimeout = timeoutMs;
                await udp.SendAsync(request, request.Length, stunEP);

                Task<UdpReceiveResult> recvTask = udp.ReceiveAsync();
                if (await Task.WhenAny(recvTask, Task.Delay(timeoutMs)) != recvTask)
                    return null;

                return ParseBindingResponse(recvTask.Result.Buffer, transactionId);
            }
        }

        // STUN Binding Request パケット生成（RFC 5389）
        // UdpPingServer から再利用するため internal に変更
        internal static byte[] BuildBindingRequest(byte[] transactionId)
        {
            byte[] msg = new byte[20];

            // Message Type: Binding Request (0x0001)
            msg[0] = 0x00; msg[1] = 0x01;
            // Message Length: 0
            msg[2] = 0x00; msg[3] = 0x00;
            // Magic Cookie: 0x2112A442
            msg[4] = 0x21; msg[5] = 0x12; msg[6] = 0xA4; msg[7] = 0x42;
            // Transaction ID (12 bytes)
            Buffer.BlockCopy(transactionId, 0, msg, 8, 12);

            return msg;
        }

        // STUN Binding Response パース。XOR-MAPPED-ADDRESS(0x0020) 優先、次に MAPPED-ADDRESS(0x0001)
        // UdpPingServer から再利用するため internal に変更
        internal static IPEndPoint ParseBindingResponse(byte[] data, byte[] transactionId)
        {
            if (data.Length < 20) return null;

            // Magic Cookie 確認
            if (data[4] != 0x21 || data[5] != 0x12 || data[6] != 0xA4 || data[7] != 0x42)
                return null;

            int msgLen = (data[2] << 8) | data[3];
            int offset = 20;
            IPEndPoint mappedAddress = null;
            IPEndPoint xorMappedAddress = null;

            while (offset + 4 <= 20 + msgLen && offset + 4 <= data.Length)
            {
                int attrType = (data[offset] << 8) | data[offset + 1];
                int attrLength = (data[offset + 2] << 8) | data[offset + 3];
                offset += 4;

                if (offset + attrLength > data.Length) break;

                if (attrType == 0x0001 && attrLength >= 8) // MAPPED-ADDRESS
                {
                    mappedAddress = ParseMappedAddress(data, offset);
                }
                else if (attrType == 0x0020 && attrLength >= 8) // XOR-MAPPED-ADDRESS
                {
                    xorMappedAddress = ParseXorMappedAddress(data, offset);
                }

                // 4バイト境界へ切り上げ
                offset += (attrLength + 3) & ~3;
            }

            return xorMappedAddress ?? mappedAddress;
        }

        private static IPEndPoint ParseMappedAddress(byte[] data, int offset)
        {
            // Family: data[offset+1] (0x01=IPv4)
            if (data[offset + 1] != 0x01) return null;

            int port = (data[offset + 2] << 8) | data[offset + 3];
            IPAddress ip = new IPAddress(new[]
            {
                data[offset + 4], data[offset + 5],
                data[offset + 6], data[offset + 7]
            });
            return new IPEndPoint(ip, port);
        }

        private static IPEndPoint ParseXorMappedAddress(byte[] data, int offset)
        {
            if (data[offset + 1] != 0x01) return null;

            // ポートは Magic Cookie 上位16bit と XOR
            int port = ((data[offset + 2] << 8) | data[offset + 3]) ^ 0x2112;

            // IP は Magic Cookie と XOR
            byte[] ipBytes =
            {
                (byte)(data[offset + 4] ^ 0x21),
                (byte)(data[offset + 5] ^ 0x12),
                (byte)(data[offset + 6] ^ 0xA4),
                (byte)(data[offset + 7] ^ 0x42)
            };
            return new IPEndPoint(new IPAddress(ipBytes), port);
        }
    }
}