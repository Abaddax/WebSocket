using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WebSocket
{
    internal static class WebSocketHelper
    {
        /// <returns>
        /// <para>-1: b bigger</para>
        /// <para>0: equal</para>
        /// <para>1: a bigger</para>
        /// </returns>
        private static int memcmp(byte[] a, int offset_a, byte[] b, int offset_b, int count)
        {
            if (a == null || b == null || count < 0 || offset_a < 0 || offset_b < 0)
                throw new ArgumentOutOfRangeException();
            if (offset_a + count > a.Length || offset_b + count > b.Length)
                throw new ArgumentException();
            for (int i = 0; i < count; i++)
            {
                if (a[i + offset_a] < b[i + offset_b])
                    return -1;
                if (a[i + offset_a] > b[i + offset_b])
                    return 1;
            }
            return 0;
        }
        /// <summary>
        /// Padding the path
        /// </summary>
        /// <returns>Padded path</returns>
        public static string PadClientPath(string path)
        {
            if (path == null || path.Length == 0)
                path = "/";
            if (path[0] != '/')
                path = '/' + path;
            if (path[path.Length - 1] != '/')
                path = path + '/';
            return path;
        }
        /// <summary>
        /// Parsing the WebSocket URL to endPoint and Path
        /// </summary>
        /// <param name="url">
        /// <para>ws://1.2.3.4:5678</para>
        /// <para>wss://1.2.3.4:5678</para>
        /// </param>
        /// <returns>
        /// <para>0: OK</para>
        /// <para>-1: Error</para>
        /// </returns>
        public static int ParseWebSocketURL(string url, out IPEndPoint endPoint, out string path)
        {
            endPoint = null;
            path = null;
            if (url == null)
                return -1;
            Uri uri = new Uri(url);
            bool secure = false;
            if (uri.Scheme == "ws" || (secure = uri.Scheme == "wss"))
            {
                endPoint = new IPEndPoint(
                    Dns.GetHostAddresses(uri.DnsSafeHost).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork),
                    uri.Port);
                path = PadClientPath(uri.AbsolutePath);
                return 0;
            }
            return -1;
        }

        /// <summary>
        /// Reading a HTTP-Message from stream
        /// </summary>
        /// <returns>HTTP-Message
        /// <para>length>0: Message</para>
        /// <para>length=0: Timout or Cancel</para>
        /// </returns>
        public static byte[] ReadHTTPMessage(NetworkStream stream, int timeout, CancellationToken? cancellationToken)
        {
            byte[] terminator = { 13, 10, 13, 10 };//\r\n\r\n
            List<byte> bytes = new List<byte>();
            DateTime begin = DateTime.Now;
            bool done = false;
            //Reading TCP-Data
            while (!done)
            {
                try
                {
                    if (stream.DataAvailable)
                    {
                        int ret = stream.ReadByte();
                        if (ret >= 0 && ret < 256)
                            bytes.Add((byte)ret);
                    }
                    else
                    {
                        cancellationToken?.WaitHandle.WaitOne(100);
                    }
                }
                catch (System.IO.IOException) { return Array.Empty<byte>(); }
                catch (ObjectDisposedException) { return Array.Empty<byte>(); }

                if (bytes.Count > 3)
                {
                    //Looking for HTTPheader-EndPoint
                    if (memcmp(bytes.ToArray(), bytes.Count - 4, terminator, 0, 4) == 0)
                    //if (bytes.ToArray()[(bytes.Count - 4)..bytes.Count].Equals(terminator))
                    {
                        done = true;
                    }
                }
                //Timeout
                if ((cancellationToken?.IsCancellationRequested ?? true) || (timeout > 0 && (DateTime.Now - begin).TotalMilliseconds > timeout))
                {
                    return Array.Empty<byte>();
                }
            }
            return bytes.ToArray();
        }
        /// <summary>
        /// Handler for WebSocket Requests
        /// </summary>
        /// <returns>
        /// <para>0:OK -> Response contains WebSocket-Handshake message</para>
        /// <para>-1:Error -> Response contains HTTP-Error Message</para>
        /// </returns>
        public static int HandleWebSocketClientRequest(string httpMessage, string path, string protocolName, out byte[] response, out string clientPath)
        {
            clientPath = null;
            const string eol = "\r\n";

            //No Get-Request
            if (!Regex.IsMatch(httpMessage, "^GET"))
            {
                response = Encoding.UTF8.GetBytes("HTTP/1.1 400 Bad Request" + eol + eol);
                return -1;
            }
            //Wrong HTTP Version
            if (!Regex.IsMatch(httpMessage, "^.*HTTP/1.1"))
            {
                response = Encoding.UTF8.GetBytes("HTTP/1.1 505 HTTP Version Not Supported" + eol + eol);
                return -1;
            }
            if (path == null || Regex.IsMatch(httpMessage, "^GET(.*" + path + ")"))
            {
                //HTTP-Request
                //Checking for Websocket-Parameters
                if (Regex.IsMatch(httpMessage, "Connection: (.*Upgrade)") &&
                    Regex.IsMatch(httpMessage, "Upgrade: (.*websocket)") &&
                    Regex.IsMatch(httpMessage, "Sec-WebSocket-Protocol: (.*" + protocolName + ")"))
                {
                    //Valid Websocket Request
                    string key = Regex.Match(httpMessage, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
                    string hash;
                    using (System.Security.Cryptography.SHA1 sha1 = System.Security.Cryptography.SHA1.Create())
                    {
                        hash = Convert.ToBase64String(sha1.ComputeHash(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                    }
                    response = Encoding.UTF8.GetBytes(
                        "HTTP/1.1 101 Switching Protocols" + eol +
                        "Upgrade: websocket" + eol +
                        "Connection: Upgrade" + eol +
                        "Sec-WebSocket-Accept: " + hash + eol +
                        "Sec-WebSocket-Protocol: " + protocolName + eol + eol);

                    clientPath = WebSocketHelper.PadClientPath(Regex.Match(httpMessage, "(?<=^GET )(.*)(?= HTTP)").Value);
                    return 0;
                }
            }
            response = Encoding.UTF8.GetBytes("HTTP/1.1 404 Not Found" + eol + eol);
            return -1;
        }
        /// <summary>
        /// Creating a WebSocket-Request Message
        /// </summary>
        /// <returns>
        /// <para>0:OK</para>
        /// <para>-1:Error</para>
        /// </returns>
        public static int CreateWebSocketRequest(string host, string path, string protocolName, out string key, out byte[] request)
        {
            request = null;
            key = null;
            const string eol = "\r\n";

            if (host == null || protocolName == null)
                return -1;

            StringBuilder sb = new StringBuilder();
            sb.Append("GET ");
            sb.Append(PadClientPath(path));
            sb.Append(" HTTP/1.1");
            sb.Append(eol);
            sb.Append("Host: ");
            sb.Append(host);
            sb.Append(eol);
            sb.Append("Connection: Upgrade");
            sb.Append(eol);
            sb.Append("Upgrade: websocket");
            sb.Append(eol);
            sb.Append("Sec-WebSocket-Version: 13");
            sb.Append(eol);
            sb.Append("Sec-WebSocket-Protocol: ");
            sb.Append(protocolName);
            sb.Append(eol);
            sb.Append("Sec-WebSocket-Key: ");
            Random rand = new Random();
            byte[] keybytes = new byte[16];
            rand.NextBytes(keybytes);
            key = Convert.ToBase64String(keybytes);
            sb.Append(key);
            sb.Append(eol);
            sb.Append(eol);
            request = Encoding.UTF8.GetBytes(sb.ToString());
            return 0;
        }
        /// <summary>
        /// Handles The WebSocket-Response from a server
        /// </summary>
        /// <returns>
        /// <para>0:OK</para>
        /// <para>-1: Error or request denied</para>
        /// </returns>
        public static int HandleWebSocketServerResponse(string httpMessage, string protocolName, string key)
        {
            if (httpMessage == null)
                return -1;

            if (!Regex.IsMatch(httpMessage, "^(HTTP/1.1 101 Switching Protocols)"))
            {
                return -1;
            }
            if (!Regex.IsMatch(httpMessage, "Upgrade:.websocket") ||
               !Regex.IsMatch(httpMessage, "Connection:.Upgrade"))
            {
                return -1;
            }
            if (!Regex.IsMatch(httpMessage, "Sec-WebSocket-Protocol:." + protocolName))
            {
                return -1;
            }

            //Valid Websocket Respose
            string keyToCheck = Regex.Match(httpMessage, "Sec-WebSocket-Accept: (.*)").Groups[1].Value.Trim();
            string hash;
            using (System.Security.Cryptography.SHA1 sha1 = System.Security.Cryptography.SHA1.Create())
            {
                hash = Convert.ToBase64String(sha1.ComputeHash(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            }
            if (hash != keyToCheck)
                return -1;

            return 0;
        }
    }
}
