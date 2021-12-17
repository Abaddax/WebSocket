namespace WebSocket
{
    public abstract class WebSocketProtocol
    {
        /// <summary>
        /// Protocol Name used in HTTP-WebSocket Handshake
        /// </summary>
        public abstract string ProtocolName { get; }

        #region Protocol protected
        protected void Send(byte[] data, Opcode code)
        {
            sendFunc?.Invoke(data, code);
        }
        protected abstract void HandleReceive(byte[] data, Opcode code);
        #endregion

        #region WebSocket/Protocol internal
        /// <summary>
        /// <para>Internal Delegate</para>
        /// <para>Delagate for RegisterWebsocketSendFunction</para>
        /// </summary>
        internal delegate void OnSendFunction(byte[] data, Opcode code);

        private OnSendFunction sendFunc;
        /// <summary>
        /// <para>Internal Function. DO NOT CALL explicitly</para>
        /// <para>Registers the private WebSocket-SendFunction for use by the protocol</para>
        /// <para>Gets called by the WebSocket-Contructor</para>
        /// <para>Protocol->func->Sends Data over Websocket</para>
        /// </summary>
        internal void RegisterWebSocketSendFunction(OnSendFunction func) //Protocol->WebSocket; Fires Send Method
        {
            sendFunc = func;
        }
        /// <para>Internal Function. DO NOT CALL explicitly</para>
        /// <para>Gets called from the WebSocket whenever a message got send by the remote-client</para>
        /// <para>WebSocket->Protocol->Computes the Message</para>
        /// </summary>
        internal void InvokeHandleReceive(byte[] data, Opcode code) //WebSocket->Protocol; Fires HandleReceive
        {
            HandleReceive(data, code);
        }
        #endregion
    }
}
