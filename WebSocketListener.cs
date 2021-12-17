using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace WebSocket
{
    internal struct WebSocketProtocolInfo
    {
        public Type ProtocolType;
        public object Protocol;
        public Delegate Event;
    }

    public class WebSocketListener : IDisposable
    {
        public delegate void OnWebSocketConnected<TProtocol>(WebSocket<TProtocol> Client) where TProtocol : WebSocketProtocol, new();

        private bool disposedValue = false;

        private readonly TcpListener listener;
        private bool listening = false;

        private Thread listenThread = null;
        private CancellationTokenSource abortToken = null;
        private bool threadRunning = false; //listenThread W only

        private readonly ConcurrentDictionary<string, WebSocketProtocolInfo> availabeServices = new();


        #region Private Static Helpers
        /*static private int memcmp(byte[] a1, int offset_a1, byte[] b1, int offset_b1, int count)
        {
            if (a1 == null || b1 == null || count < 0 || offset_a1 < 0 || offset_b1 < 0)
            {
                throw new ArgumentOutOfRangeException();
            }
            for (int i = 0; i < count; i++)
            {
                if (a1.Length - (i + offset_a1) < 0 || b1.Length - (i + offset_b1) < 0)
                    throw new Exception();
                if (a1[i + offset_a1] < b1[i + offset_b1])
                    return -1;
                if (a1[i + offset_a1] > b1[i + offset_b1])
                    return 1;
            }
            return 0;
        }
        static private string PadClientPath(string path)
        {
            if (path == null)
                path = "/";
            if (path[0] != '/')
                path = '/' + path;
            if (path[path.Length - 1] != '/')
                path = path + '/';
            return path;
        }
        static private byte[] ReadHTTPMessage(NetworkStream stream, int timeout, CancellationToken? cancellationToken)
        {
            byte[] terminator = { 13, 10, 13, 10 };
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
                        //Thread.Sleep(1);
                    }
                }
                catch (System.IO.IOException) { return Array.Empty<byte>(); }
                catch (ObjectDisposedException) { return Array.Empty<byte>(); }

                if (bytes.Count > 3)
                {
                    //Looking for HTTPheader-EndPoint
                    if (memcmp(bytes.ToArray(), bytes.Count - 4, terminator, 0, 4) == 0)
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
        
        static private int HandleWebSocketRequest(string httpMessage, string path, string protocolName, out byte[] response, out string clientPath)
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
                //Checking for OCPP-Websocket-Parameters
                if (Regex.IsMatch(httpMessage, "Connection: (.*Upgrade)") &&
                    Regex.IsMatch(httpMessage, "Upgrade: (.*websocket)") &&
                    Regex.IsMatch(httpMessage, "Sec-WebSocket-Protocol: (.*" + protocolName + ")"))
                {
                    //Valid OCPP-Websocket Request
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
        */
        
        #endregion
        
        #region Private Functions
        private int ListenThreadFunc()
        {
            if (listener == null || !listener.Server.IsBound || !listener.Pending())
                return -1;

            TcpClient client = listener.AcceptTcpClient();

            byte[] request = WebSocketHelper.ReadHTTPMessage(client.GetStream(), 5000, abortToken?.Token);
            byte[] response = null;

            string clientPath;
            if (request != null && request.Length > 0)
            {
                Console.WriteLine("Checking for WebsocketRequest\n\nRequest:\n{0}", Encoding.ASCII.GetString(request));
                foreach (var entry in availabeServices)
                {
                    string path = entry.Key;
                    WebSocketProtocolInfo protocolInfo = entry.Value;
                    WebSocketProtocol protocol = (WebSocketProtocol)protocolInfo.Protocol;

                    int ret = WebSocketHelper.HandleWebSocketClientRequest(Encoding.UTF8.GetString(request), path, protocol.ProtocolName, out response, out clientPath);
                    if (ret == 0)
                    {
                        Console.WriteLine("Response:\n" + Encoding.ASCII.GetString(response));

                        client.GetStream().Write(response);

                        var clientProtocol = Activator.CreateInstance(entry.Value.ProtocolType);
                        Type websocketType = typeof(WebSocket<>).MakeGenericType(entry.Value.ProtocolType);

                        //var webSocket = Activator.CreateInstance(websocketType, clientProtocol, client, clientPath);
                        var webSocket = Activator.CreateInstance(websocketType, BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { clientProtocol, client, clientPath }, null);

                        var eventHandler = entry.Value.Event.Method;
                        eventHandler.Invoke(entry.Value.Event.Target, new object[] { webSocket });
                        return 0;
                    }
                }
                if (response == null)
                    response = Encoding.UTF8.GetBytes("HTTP/1.1 400 Bad Request\r\n\r\n");
                client.GetStream().Write(response);
                Console.WriteLine("Client denied: Unknown Websocket");
            }
            else
            {
                Console.WriteLine("Client denied");
            }
            client.Close();
            client.Dispose();
            return 0;
        }
        private void StartThread()
        {
            if (listenThread != null || !listening || threadRunning)
                return;

            abortToken?.Dispose(); //just in case
            abortToken = new CancellationTokenSource();
            listenThread = new Thread(task =>
            {
                threadRunning = true;
                while (!(abortToken?.IsCancellationRequested ?? true))//(!abortThread)
                {
                    while (listening && !(abortToken?.IsCancellationRequested ?? true))
                    {
                        int ret = ListenThreadFunc();
                        if (ret < 0)
                            abortToken?.Token.WaitHandle.WaitOne(100);
                    }
                    abortToken?.Token.WaitHandle.WaitOne(100);
                }
                listening = false;
                threadRunning = false;
                return;
            })
            { Name = "WebSocketListener-ListenThread" };
            //abortThread = false;
            listenThread.Start();
        }
        private void StopThread()
        {
            abortToken?.Cancel();
            if (threadRunning)
                Thread.Sleep(100);
            if (threadRunning)
                throw new Exception("WebSocketListener - ListenThread still running");
            abortToken?.Dispose();
            abortToken = null;
            listenThread = null;
        }
        #endregion

        public WebSocketListener(IPAddress address, int port)
        {
            listener = new TcpListener(address, port);
        }

        public void Start()
        {
            if (disposedValue)
                throw new ObjectDisposedException(this.GetType().FullName);

            if (!listening)
            {
                listener.Start();
                Console.WriteLine("Listening for clients");
                listening = true;
                if (!threadRunning)
                    StartThread();
            }
        }
        public void Stop()
        {
            if (disposedValue)
                throw new ObjectDisposedException(this.GetType().FullName);

            if (listening)
            {
                Console.WriteLine("Stop Listening for clients");
                listener.Stop();
                listening = false;
                if (threadRunning)
                    StopThread();
            }
        }

        public int AddService<TProtocol>(string path, OnWebSocketConnected<TProtocol> NewClientHandler) where TProtocol : WebSocketProtocol, new()
        {
            if (disposedValue)
                throw new ObjectDisposedException(this.GetType().FullName);

            path = WebSocketHelper.PadClientPath(path);

            return availabeServices.TryAdd(path, new WebSocketProtocolInfo()
            {
                ProtocolType = typeof(TProtocol),
                Protocol = new TProtocol(),
                Event = NewClientHandler
            }) ? 0 : -1;
        }
        public int RemoveService(string path)
        {
            if (disposedValue)
                throw new ObjectDisposedException(this.GetType().FullName);

            path = WebSocketHelper.PadClientPath(path);
            return availabeServices.TryRemove(path, out _) ? 0 : -1;
        }

        public bool Listening { get { return listening; } }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: Verwalteten Zustand (verwaltete Objekte) bereinigen
                    Stop();
                    availabeServices.Clear();
                    listening = false;
                }
                // TODO: Nicht verwaltete Ressourcen (nicht verwaltete Objekte) freigeben und Finalizer überschreiben
                // TODO: Große Felder auf NULL setzen                
                abortToken?.Cancel();
                listener.Stop();
                listener.Server?.Dispose();
                listening = false;
                StopThread();
                abortToken?.Dispose();

                disposedValue = true;
            }
        }
        ~WebSocketListener()
        {
            Dispose(false);
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

}
