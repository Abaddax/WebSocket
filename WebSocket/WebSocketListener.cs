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

        /// <summary>
        /// Start listening for clients
        /// </summary>
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
        /// <summary>
        /// Stop listening for clients
        /// <para>Does not close existing client connections</para>
        /// </summary>
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

        /// <summary>
        /// Adds a WebSocket-Service to the listener
        /// </summary>
        /// <param name="path">Path the WebSocket can request</param>
        /// <param name="NewClientHandler">Gets called when a new WebSocket-Connection got established for this service</param>
        /// <returns>
        /// <para>0: OK</para>
        /// <para>-1: already a service on this path</para>
        /// </returns>
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
        /// <summary>
        /// Remopves a WebSocket-Service
        /// </summary>
        /// <returns>
        /// <para>0: OK</para>
        /// <para>-1: no service found for the path</para>
        /// </returns>
        public int RemoveService(string path)
        {
            if (disposedValue)
                throw new ObjectDisposedException(this.GetType().FullName);

            path = WebSocketHelper.PadClientPath(path);
            return availabeServices.TryRemove(path, out _) ? 0 : -1;
        }

        public bool Listening { get { return listening; } }
        public string[] Services
        {
            get
            {
                List<string> ret = new List<string>();
                foreach (var entry in availabeServices)
                {
                    ret.Add(entry.Key);
                }
                return ret.ToArray();
            }
        }
        public EndPoint LocalEndpoint
        {
            get
            {
                IPEndPoint ep = (IPEndPoint)listener?.LocalEndpoint;
                if (ep == null)
                    return null;
                return new IPEndPoint(ep.Address, ep.Port);
            }
        }
       

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // Verwalteten Zustand (verwaltete Objekte) bereinigen
                    Stop();
                    availabeServices.Clear();
                    listening = false;
                }
                // Nicht verwaltete Ressourcen (nicht verwaltete Objekte) freigeben und Finalizer überschreiben
                // Große Felder auf NULL setzen                
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
