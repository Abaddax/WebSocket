using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;


namespace WebSocket
{
    public enum Opcode
    {
        continuation = 0x00,
        text = 0x01,
        binary = 0x02,
        close = 0x08,
        ping = 0x09,
        pong = 0x0A
    }
    internal struct WebSocketFrame
    {
        public bool FIN;
        public bool RSV1;
        public bool RSV2;
        public bool RSV3;
        public Opcode OPCODE;
        public bool MASK;
        public byte[] MASK_KEY;
        public int Payload_Length;
        public byte[] Payload;
    }
    public class WebSocket<TProtocol> : IDisposable where TProtocol : WebSocketProtocol, new()
    {
        private bool disposedValue = false;

        private readonly TProtocol protocol;
        private TcpClient client;
        private NetworkStream stream = null;
        private string path = null;
        private string url = null;

        private readonly object sendLock = new();
        private readonly object readLock = new();

        private bool connected = false;

        private Thread listenThread = null;
        private CancellationTokenSource abortToken = null;
        private bool threadRunning = false; //listenThread W only


        #region Private Functions

        #region Receive Functions
        /// <returns>
        /// <para>0: OK</para>
        /// <para>-1: ReadError</para>
        /// <para>-2: Connection Error</para>
        /// </returns>
        private int ReadWebSocketFrame(out WebSocketFrame Message)
        {
            List<byte> header = new List<byte>();

            Message = new WebSocketFrame() { FIN = false, RSV1 = false, RSV2 = false, RSV3 = false, OPCODE = (Opcode)0, MASK = false, MASK_KEY = null, Payload_Length = 0, Payload = null };
            int timeoutCounter = 0;
            bool done = false;
            bool completed_length = false;
            bool completed_header = false;

            //lock Should be irrelavent
            lock (readLock)
            {
                while (!done && !(abortToken?.IsCancellationRequested ?? true))
                {
                    //Reading WebsocketHeader
                    try
                    {
                        if (stream.DataAvailable)
                        {
                            int ret = stream.ReadByte();
                            if (ret >= 0 && ret < 256)
                            {
                                timeoutCounter = 0;
                                header.Add((byte)ret);
                            }
                        }
                        else
                        {
                            abortToken?.Token.WaitHandle.WaitOne(10);
                            timeoutCounter++;
                            if (timeoutCounter == 500)//5Sec
                            {
                                //Send Ping
                                int ret = SendPayload(Encoding.UTF8.GetBytes("PING"), Opcode.ping, true);
                                if (ret < 0)
                                    return -2; //SendError
                            }
                            if(timeoutCounter>1000)//10Sec
                            {
                                return -2; //Connection Timeout
                            }
                        }
                    }
                    catch (System.IO.IOException) { return -2; }
                    catch (ObjectDisposedException) { return -2; }

                    //Process 1st Byte
                    if (!completed_length && header.Count == 1)
                    {
                        Message.FIN = (header[0] & 0b10000000) != 0;
                        Message.RSV1 = (header[0] & 0b01000000) != 0;
                        Message.RSV2 = (header[0] & 0b00100000) != 0;
                        Message.RSV3 = (header[0] & 0b00010000) != 0;
                        if (Message.RSV1 || Message.RSV2 || Message.RSV3)
                            done = true;
                        Message.OPCODE = (Opcode)(header[0] & 0b00001111);
                    }
                    //Process 2nd Byte
                    if (!completed_length && header.Count == 2)
                    {
                        Message.MASK = (header[1] & 0b10000000) != 0;
                        Message.Payload_Length = header[1] & 0b01111111;
                        if (Message.Payload_Length < 126)
                        {
                            completed_length = true;
                            header.Clear();
                        }
                    }
                    //Length==126 -> 16 Bit Length
                    if (!completed_length && header.Count == 4 && Message.Payload_Length == 126)
                    {
                        Message.Payload_Length = BitConverter.ToUInt16(new byte[] { header[3], header[2] }, 0);
                        completed_length = true;
                        header.Clear();
                    }
                    //Length==127 -> 64 Bit Length
                    if (!completed_length && header.Count == 10 && Message.Payload_Length == 127)
                    {
                        Message.Payload_Length = (int)BitConverter.ToUInt64(new byte[] { header[9], header[8], header[7], header[6], header[5], header[4], header[3], header[2] }, 0);
                        completed_length = true;
                        header.Clear();
                    }
                    //MASK==TRUE -> 4 Bytes MASK_KEY
                    if (completed_length && !completed_header)
                    {
                        if (Message.MASK)
                        {
                            if (header.Count == 4)
                            {
                                Message.MASK_KEY = new byte[4];
                                Message.MASK_KEY[0] = header[0];
                                Message.MASK_KEY[1] = header[1];
                                Message.MASK_KEY[2] = header[2];
                                Message.MASK_KEY[3] = header[3];
                                completed_header = true;
                                header.Clear();
                            }
                        }
                        else
                        {
                            completed_header = true;
                        }
                    }
                    //Reading Payload
                    if (completed_header)
                    {
                        Message.Payload = new byte[Message.Payload_Length];

                        int read = 0;
                        try
                        {
                            while (read < Message.Payload_Length && !(abortToken?.IsCancellationRequested ?? true))
                            {
                                if (stream.DataAvailable)
                                {
                                    int ret = stream.Read(Message.Payload, read, Message.Payload.Length - read);
                                    if (ret > 0)
                                    {
                                        read += ret;
                                    }
                                }
                                else
                                {
                                    abortToken?.Token.WaitHandle.WaitOne(10);
                                }
                            }
                        }
                        catch (System.IO.IOException) { return -2; }
                        catch (ObjectDisposedException) { return -2; }

                        done = true;
                        return 0;
                    }
                }
            }
            return -1;
        }
        /// <returns>
        /// <para>0: OK</para>
        /// <para>-1: Error/Connection Closed</para>
        /// </returns>
        private int ReadPayload(out byte[] Payload, out Opcode Code)
        {
            List<WebSocketFrame> frames = new List<WebSocketFrame>();

            Payload = null;
            Code = Opcode.continuation;

            bool error = false;
            bool endOfFrame = false;
            int ret = -1;
            while (!error && !(abortToken?.IsCancellationRequested ?? true))
            {
                WebSocketFrame frame;
                ret = ReadWebSocketFrame(out frame);
                if (ret == 0)
                {
                    //Unmasking if needed
                    if (frame.MASK == true)
                    {
                        for (uint i = 0; i < frame.Payload_Length; i++)
                        {
                            frame.Payload[i] = (byte)(frame.Payload[i] ^ frame.MASK_KEY[i % 4]);
                        }
                    }

                    switch (frame.OPCODE)
                    {
                        case Opcode.close:
                            {
                                //CloseFrames must me Masked
                                if (frame.MASK && frame.FIN)
                                {
                                    OnCloseReceived(frame);//Close request form client -> closing connection
                                    return -1;
                                }
                                else
                                    error = true;
                                continue;
                            }
                        case Opcode.ping:
                            {
                                if (frame.FIN)
                                    SendPayload(frame.Payload, Opcode.pong, false);
                                else
                                    error = true;
                                continue;
                            }
                        case Opcode.pong:
                            {
                                if (!frame.FIN)
                                    error = true;
                                continue;
                            }
                        case Opcode.continuation:
                            {
                                frames.Add(frame);
                                if (frame.FIN) //Continuous Frame Finished
                                    endOfFrame = true;
                                break;
                            }
                        case Opcode.binary:
                        case Opcode.text:
                            {
                                frames.Add(frame);
                                if (frame.FIN) //Single Frame
                                {
                                    endOfFrame = true;
                                    Code = frame.OPCODE;
                                }
                                else //Continuous Frame
                                {
                                    Code = frame.OPCODE;
                                }
                                break;
                            }
                    }
                    //Combining Partial Frames
                    if (endOfFrame && !error)
                    {
                        int length = 0;
                        int offset = 0;
                        for (int i = 0; i < frames.Count; i++)
                        {
                            length += frames[i].Payload_Length;
                        }
                        Payload = new byte[length];
                        for (int i = 0; i < frames.Count; i++)
                        {
                            System.Buffer.BlockCopy(frames[i].Payload, 0, Payload, offset, frames[i].Payload_Length);
                        }
                        return 0;
                    }
                }
                else
                {
                    error = true;
                }
            }
            //OnERROR
            if (ret == -2)
                OnConnectionClosed();//Connection got closed from client
            else
                OnReceiveError(); //Connection error -> server closes connection
            return -1;
        }
        #endregion

        #region Send Functions
        /// <returns>
        /// <para>0: OK</para>
        /// <para>-1: ParseError</para>
        /// <para>-2: Connection Error</para>
        /// </returns>
        private int SendWebSocketFrame(in WebSocketFrame Message)
        {
            if (Message.Payload_Length != 0 && Message.Payload == null)
                return -1;

            if (Message.Payload != null && Message.Payload.Length < Message.Payload_Length)
                return -1;

            int maskbuf = Message.MASK ? 4 : 0;
            int lenbuf = Message.Payload_Length < 126 ? 1 : (Message.Payload_Length == 126 ? 3 : 9);
            byte[] buffer = new byte[1 + maskbuf + lenbuf + Message.Payload_Length];

            if (Message.FIN == false && Message.OPCODE != Opcode.continuation)
                return -1;

            buffer[0] = (byte)((byte)Message.OPCODE | (Message.FIN ? 1 : 0) << 7 | (Message.RSV1 ? 1 : 0) << 6 | (Message.RSV2 ? 1 : 0) << 5 | (Message.RSV3 ? 1 : 0) << 4);

            //PayloadLength
            switch (lenbuf)
            {
                case 1:
                    {
                        byte[] length = BitConverter.GetBytes((byte)Message.Payload_Length);
                        buffer[1] = length[0];
                        break;
                    }
                case 3:
                    {
                        byte[] length = BitConverter.GetBytes((UInt16)Message.Payload_Length);
                        buffer[3] = length[0];
                        buffer[2] = length[1];
                        buffer[1] = 126;
                        break;
                    }
                case 9:
                    {
                        byte[] length = BitConverter.GetBytes((UInt64)Message.Payload_Length);
                        buffer[9] = length[0];
                        buffer[8] = length[1];
                        buffer[7] = length[2];
                        buffer[6] = length[3];
                        buffer[5] = length[4];
                        buffer[4] = length[5];
                        buffer[3] = length[6];
                        buffer[2] = length[7];
                        buffer[1] = 127;
                        break;
                    }
                default: return -1;
            }
            //MaskBit
            if (Message.MASK)
            {
                buffer[1] |= 0b10000000;
                //Setting MASK_KEY
                if (Message.MASK_KEY == null)
                    return -1;
                buffer[1 + lenbuf] = Message.MASK_KEY[0];
                buffer[2 + lenbuf] = Message.MASK_KEY[1];
                buffer[3 + lenbuf] = Message.MASK_KEY[2];
                buffer[4 + lenbuf] = Message.MASK_KEY[3];
            }
            else
            {
                buffer[1] &= 0b01111111;
            }

            if (Message.Payload != null)
                Buffer.BlockCopy(Message.Payload, 0, buffer, 1 + maskbuf + lenbuf, Message.Payload_Length);

            try
            {
                lock (sendLock)
                {
                    stream.Write(buffer, 0, buffer.Length);
                }
            }
            catch (System.IO.IOException) { return -2; }
            catch (ObjectDisposedException) { return -2; }

            return 0;
        }
        /// <returns>
        /// <para>0:  OK</para>
        /// <para>-1: Error</para>
        /// </returns>
        private int SendPayload(in byte[] Payload, in Opcode Code, in bool Masked)
        {
            WebSocketFrame frame = new WebSocketFrame() { FIN = false, RSV1 = false, RSV2 = false, RSV3 = false, OPCODE = Opcode.continuation, MASK = false, MASK_KEY = null, Payload_Length = 0, Payload = null };
            switch (Code)
            {
                case Opcode.continuation:
                    {
                        //Not supported
                        return -1;
                    }
                case Opcode.binary:
                case Opcode.text:
                    {

                        frame.FIN = true;
                        frame.OPCODE = Code;
                        frame.MASK = Masked;
                        if (frame.MASK)
                        {
                            frame.MASK_KEY = new byte[4];
                            Random rand = new Random();
                            rand.NextBytes(frame.MASK_KEY);
                        }
                        if (Payload != null)
                        {
                            frame.Payload_Length = Payload.Length;
                            frame.Payload = new byte[frame.Payload_Length];
                            System.Buffer.BlockCopy(Payload, 0, frame.Payload, 0, Payload.Length);
                            if (frame.MASK)
                            {
                                for (int i = 0; i < frame.Payload.Length; i++)
                                {
                                    frame.Payload[i] = (byte)(frame.Payload[i] ^ frame.MASK_KEY[i % 4]);
                                }
                            }
                        }
                        int ret = SendWebSocketFrame(frame);
                        if (ret < 0)
                            return -1;
                        break;
                    }
                case Opcode.close:
                case Opcode.ping:
                case Opcode.pong:
                    {
                        frame.FIN = true;
                        frame.OPCODE = Code;

                        //Client?
                        if(url==null)
                            frame.MASK = false;
                        else
                            frame.MASK = true;

                        if (frame.MASK)
                        {
                            frame.MASK_KEY = new byte[4];
                            Random rand = new Random();
                            rand.NextBytes(frame.MASK_KEY);
                        }
                        if (Payload != null)
                        {
                            frame.Payload_Length = Payload.Length;
                            frame.Payload = new byte[frame.Payload_Length];
                            System.Buffer.BlockCopy(Payload, 0, frame.Payload, 0, Payload.Length);
                            if (frame.MASK)
                            {
                                for (int i = 0; i < frame.Payload.Length; i++)
                                {
                                    frame.Payload[i] = (byte)(frame.Payload[i] ^ frame.MASK_KEY[i % 4]);
                                }
                            }
                        }

                        int ret = SendWebSocketFrame(frame);
                        if (ret < 0)
                            return -1;
                        break;
                    }
                default: return -1;
            }
            return 0;
        }
        #endregion

        #region EventCaller and EventHandler
        /// <summary>
        /// Calls the Protocol to process received data
        /// </summary>
        private void OnMessageReceived(byte[] message, Opcode code)
        {
            protocol?.InvokeHandleReceive(message, code);
        }
        /// <summary>
        /// Function gets called by Protocol to send data
        /// </summary>
        private void OnProtocolSend(byte[] data, Opcode code)
        {
            SendPayload(data, code, false);
        }
        #endregion

        #region Close-Handler
        /// <summary>
        /// Diconnecting and freeing resources
        /// </summary>
        /// <returns><para>Succsess: true</para><para>failure: false</para></returns>
        private bool TryClose()
        {
            if (connected == false)
                return false;
            connected = false;
            stream.Close();
            stream.Dispose();
            client.Close();
            abortToken?.Cancel();
            return true;
        }
        /// <summary>
        /// Client trying to disconnect
        /// </summary>
        private void OnCloseReceived(in WebSocketFrame closeframe)
        {
            SendPayload(closeframe.Payload, Opcode.close, false);
            if (TryClose())
            {
                OnClosed?.Invoke(this, Encoding.UTF8.GetString(closeframe.Payload));
                Console.WriteLine("Connection Closed (Client Diconnected)");
            }
        }
        /// <summary>
        /// Receive Error -> Server disconnects
        /// </summary>
        private void OnReceiveError()
        {
            SendPayload(new byte[] { 0x03, 0xEB }, Opcode.close, false);
            if (TryClose())
            {
                OnClosed?.Invoke(this, "1003");
                Console.WriteLine("Connection Closed (Receive-Error)");
            }
        }
        /// <summary>
        /// Connection Error (Client disconnected without close) -> Server closes
        /// </summary>
        private void OnConnectionClosed()
        {
            if (TryClose())
            {
                OnClosed?.Invoke(this, "1002");
                Console.WriteLine("Connection Closed (Connection-Error)");
            }

        }
        #endregion

        #region Listening Thread
        private int ListenThreadFunc()
        {
            return -1;
        }
        private void StartThread()
        {
            if (!threadRunning)
            {
                abortToken?.Dispose();
                abortToken = new CancellationTokenSource();
                listenThread = new Thread(task =>
                {
                    threadRunning = true;
                    while (!(abortToken?.IsCancellationRequested ?? true))
                    {
                        byte[] payload;
                        Opcode code;
                        int ret = ReadPayload(out payload, out code); //Closes the Connection itself on error
                        if (ret < 0)
                        {
                            connected = false;
                            break;
                        }
                        else
                        {
                            OnMessageReceived(payload, code);
                        }
                    }
                    //On Error / ThreadStop -> Connection Closed
                    if (connected)
                    {
                        stream.Close();
                        stream.Dispose();
                        client.Close();
                        OnClosed?.Invoke(this, "");
                        Console.WriteLine("Connection Closed");
                        connected = false;
                    }
                    threadRunning = false;
                    return;
                })
                { Name = "WebSocket-ListenThread" };
                listenThread.Start();
            }
        }
        private void StopThread()
        {
            abortToken?.Cancel();
            if (threadRunning)
                Thread.Sleep(1000);
            if (threadRunning)
                throw new Exception("WebSocket-ListenThread still running");
            abortToken?.Dispose();
            abortToken = null;
            listenThread = null;
        }
        #endregion

        #region WebSocket-Connect
        private int WebSocketHandshake(string url, int timeout, out string path)
        {
            path = null;
            if (client.Connected)
                return -1;

            IPEndPoint ep;
            int ret = WebSocketHelper.ParseWebSocketURL(url, out ep, out path);
            if (ret != 0)
                return -1;

            try
            {
                client.Connect(ep);
                byte[] request;
                byte[] response;
                ret = WebSocketHelper.CreateWebSocketRequest(ep.Address.ToString(), path, protocol.ProtocolName, out string key, out request);
                if (ret != 0)
                {
                    client.Close();
                    return -1;
                }
                client.GetStream().Write(request, 0, request.Length);

                Console.ReadKey();
                response = WebSocketHelper.ReadHTTPMessage(client.GetStream(), timeout, new CancellationToken());
                if (response == null)
                {
                    client.Close();
                    return -1;
                }
                ret = WebSocketHelper.HandleWebSocketServerResponse(Encoding.UTF8.GetString(response), protocol.ProtocolName, key);
                if (ret != 0)
                {
                    client.Close();
                    return -1;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                client.Close();
                return -1;
            }
            return 0;
        }
        #endregion
        #endregion


        #region Constructors
        internal WebSocket(TProtocol Protocol, TcpClient Client, string Path)
        {
            if (Protocol == null || Client == null || Path == null)
                throw new ArgumentNullException($"Protocol?: {Protocol == null}\nClient?:\n{Client == null}Path?:{Path == null}\n");
            protocol = Protocol;
            protocol.RegisterWebSocketSendFunction(OnProtocolSend);
            client = Client;
            stream = client.GetStream();
            path = Path;
            connected = client.Connected;
            StartThread();
        }
        public WebSocket()
        {
            client = new TcpClient();
            protocol = new TProtocol();
            connected = client.Connected;
        }
        public WebSocket(string Url)
        {
            client = new TcpClient();
            protocol = new TProtocol();
            connected = client.Connected;
            url = Url;
        }
        #endregion

        #region Connect
        /// <summary>
        /// Connect WebSocket to Url
        /// </summary>
        /// <param name="Url">
        /// <para>ws://1.2.3.4:5678</para>
        /// <para>wss://1.2.3.4:5678</para>
        /// </param>
        /// <returns>
        /// <para>0:OK</para>
        /// <para>-1:Error</para>
        /// </returns>
        public int Connect(string Url)
        {            
            if (Url == null || connected == true || threadRunning == true)
                return -1;

            url = Url;

            int ret = WebSocketHandshake(url, 5000, out path);
            if (ret != 0)
            {
                connected = false;
                client = null;
                client = new TcpClient();
                return -1;
            }
            stream = client.GetStream();
            connected = client.Connected;
            protocol.RegisterWebSocketSendFunction(OnProtocolSend);
            StartThread();
            return 0;
        }
        /// <summary>
        /// Connect WebSocket to constructor-defined Url
        /// </summary>
        /// <returns>
        /// <para>0:OK</para>
        /// <para>-1:Error</para>
        /// </returns>
        public int Connect()
        {
            return Connect(url);
        }

        #endregion

        #region Close
        /// <summary>
        /// Closes WebSocket-Connection
        /// </summary>
        public void Close()
        {
            if (disposedValue)
                throw new ObjectDisposedException(this.GetType().FullName);

            if (connected == true)
                SendPayload(new byte[] { 0x03, 0xE8 }, Opcode.close, false);
            //SendPayload(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(1000)), Opcode.close, false);
            if (TryClose())
            {
                StopThread();
                OnClosed?.Invoke(this, "1000");
                Console.WriteLine("Connection Closed");
            }
        }
        /// <summary>
        /// Close Event-Handler
        /// <para>After the connection got already closed</para>
        /// </summary>
        public event EventHandler<string> OnClosed;
        #endregion



        public TProtocol Protocol { get { if (disposedValue) throw new ObjectDisposedException(this.GetType().FullName); return protocol; } }
        public string Path { get { if (disposedValue) throw new ObjectDisposedException(this.GetType().FullName); return path; } }
        public bool IsConnected { get { return connected; } }
        public EndPoint LocalEndpoint
        {
            get
            {
                IPEndPoint ep = (IPEndPoint)client?.Client?.LocalEndPoint;
                if (ep == null)
                    return null;
                return new IPEndPoint(ep.Address, ep.Port);
            }
        }
        public EndPoint RemoteEndpoint
        {
            get
            {
                IPEndPoint ep = (IPEndPoint)client?.Client?.RemoteEndPoint;;
                if (ep == null)
                    return null;
                return new IPEndPoint(ep.Address, ep.Port);
            }
        }
        

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                abortToken?.Cancel();
                stream?.Dispose();
                client?.Close();
                if (disposing)
                {
                    // Verwalteten Zustand (verwaltete Objekte) bereinigen
                    Close();
                    connected = false;
                    client = null;
                    stream = null;
                    path = null;
                    url = null;
                }
                // Nicht verwaltete Ressourcen (nicht verwaltete Objekte) freigeben und Finalizer überschreiben
                // Große Felder auf NULL setzen
                StopThread();

                disposedValue = true;
            }
        }
        ~WebSocket()
        {
            // Ändern Sie diesen Code nicht. Fügen Sie Bereinigungscode in der Methode "Dispose(bool disposing)" ein.
            Dispose(false);
        }
        public void Dispose()
        {
            // Ändern Sie diesen Code nicht. Fügen Sie Bereinigungscode in der Methode "Dispose(bool disposing)" ein.
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

}
