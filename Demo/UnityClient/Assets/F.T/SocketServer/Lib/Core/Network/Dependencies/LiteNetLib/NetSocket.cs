#if UNITY_4 || UNITY_5 || UNITY_5_3_OR_NEWER
#define UNITY
#endif
#if NETCORE
using System.Runtime.InteropServices;
#endif

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace LiteNetLib
{
    internal interface INetSocketListener
    {
        void OnMessageReceived(byte[] data, int length, SocketError errorCode, IPEndPoint remoteEndPoint);
    }

    internal sealed class NetSocket
    {
        private Socket _udpSocketv4;
        private Socket _udpSocketv6;
        private Thread _threadv4;
        private Thread _threadv6;
        private volatile bool _running;
        private readonly INetSocketListener _listener;
        private static readonly IPAddress MulticastAddressV6 = IPAddress.Parse("FF02:0:0:0:0:0:0:1");
        private byte[] _receiveBufferv4, _receiveBufferv6; // receive buffer
        internal static readonly bool IPv6Support;

        public int LocalPort { get; private set; }

        public short Ttl
        {
            get { return _udpSocketv4.Ttl; }
            set { _udpSocketv4.Ttl = value; }
        }

        static NetSocket()
        {
#if DISABLE_IPV6
            IPv6Support = false;
#elif UNITY
            IPv6Support = Socket.SupportsIPv6;
#else
            IPv6Support = Socket.OSSupportsIPv6;
#endif
        }

        public NetSocket(INetSocketListener listener)
        {
            _listener = listener;
        }

        private void ReceiveLogicAsync(IAsyncResult ar)
        {
            Socket _udpSocket = (Socket)ar.AsyncState;
            EndPoint bufferEndPoint = new IPEndPoint(_udpSocket.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
            try
            {
                int result = _udpSocket.EndReceiveFrom(ar, ref bufferEndPoint);
                //clone receive array
                byte[] buffer = new byte[result];
                Array.Copy(_receiveBufferv4, buffer, result);
                //call BeginReceiveFrom before _listener.OnMessageReceived 
                _udpSocket.BeginReceiveFrom(_receiveBufferv4, 0, _receiveBufferv4.Length, SocketFlags.None, ref bufferEndPoint, ReceiveLogicAsync, _udpSocket);
                _listener.OnMessageReceived(buffer, result, 0, (IPEndPoint)bufferEndPoint);
            }
            catch (SocketException ex)
            {
                switch (ex.SocketErrorCode)
                {
                    case SocketError.Interrupted:
                        return;
                    case SocketError.ConnectionReset:
                    case SocketError.MessageSize:
                    case SocketError.TimedOut:
                        NetDebug.Write(NetLogLevel.Trace, "[R]Ignored error: {0} - {1}",
                            (int)ex.SocketErrorCode, ex.ToString());
                        break;
                    default:
                        NetDebug.WriteError("[R]Error code: {0} - {1}", (int)ex.SocketErrorCode,
                            ex.ToString());
                        _listener.OnMessageReceived(null, 0, ex.SocketErrorCode, (IPEndPoint)bufferEndPoint);
                        break;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }

        public bool Bind(IPAddress addressIPv4, IPAddress addressIPv6, int port, bool reuseAddress)
        {
            _udpSocketv4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            if (!BindSocket(_udpSocketv4, new IPEndPoint(addressIPv4, port), reuseAddress))
                return false;
            LocalPort = ((IPEndPoint)_udpSocketv4.LocalEndPoint).Port;
            _running = true;

            _receiveBufferv4 = new byte[NetConstants.MaxPacketSize];
            EndPoint pointv4 = new IPEndPoint(_udpSocketv4.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
            _udpSocketv4.BeginReceiveFrom(_receiveBufferv4, 0, _receiveBufferv4.Length, SocketFlags.None, ref pointv4, ReceiveLogicAsync, _udpSocketv4);

            //Check IPv6 support
            if (!IPv6Support)
                return true;

            _udpSocketv6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
            //Use one port for two sockets
            if (BindSocket(_udpSocketv6, new IPEndPoint(addressIPv6, LocalPort), reuseAddress))
            {
                try
                {
#if !UNITY
                    _udpSocketv6.SetSocketOption(
                        SocketOptionLevel.IPv6, 
                        SocketOptionName.AddMembership,
                        new IPv6MulticastOption(MulticastAddressV6));
#endif
                }
                catch (Exception)
                {
                    // Unity3d throws exception - ignored
                }

                _receiveBufferv6 = new byte[NetConstants.MaxPacketSize];
                EndPoint pointv6 = new IPEndPoint(_udpSocketv6.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
                _udpSocketv6.BeginReceiveFrom(_receiveBufferv6, 0, _receiveBufferv6.Length, SocketFlags.None, ref pointv6, ReceiveLogicAsync, _udpSocketv6);
            }

            return true;
        }

        private bool BindSocket(Socket socket, IPEndPoint ep, bool reuseAddress)
        {
            //Setup socket
            socket.ReceiveTimeout = 500;
            socket.SendTimeout = 500;
            socket.ExclusiveAddressUse = !reuseAddress;
            socket.ReceiveBufferSize = NetConstants.SocketBufferSize;
            socket.SendBufferSize = NetConstants.SocketBufferSize;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, reuseAddress);
            if (socket.AddressFamily == AddressFamily.InterNetwork)
            {
                socket.Ttl = NetConstants.SocketTTL;

#if NETCORE
                if(!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
#endif
                try { socket.DontFragment = true; }
                catch (SocketException e)
                {
                    NetDebug.WriteError("[B]DontFragment error: {0}", e.SocketErrorCode);
                }

                try { socket.EnableBroadcast = true; }
                catch (SocketException e)
                {
                    NetDebug.WriteError("[B]Broadcast error: {0}", e.SocketErrorCode);
                }
            }

            //Bind
            try
            {
                socket.Bind(ep);
                NetDebug.Write(NetLogLevel.Trace, "[B]Successfully binded to port: {0}", ((IPEndPoint)socket.LocalEndPoint).Port);
            }
            catch (SocketException bindException)
            {
                switch (bindException.SocketErrorCode)
                {
                    //IPv6 bind fix
                    case SocketError.AddressAlreadyInUse:
                        if (socket.AddressFamily == AddressFamily.InterNetworkV6)
                        {
                            try
                            {
                                socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)27, true);
                                socket.Bind(ep);
                            }
                            catch (SocketException ex)
                            {
                                NetDebug.WriteError("[B]Bind exception: {0}, errorCode: {1}", ex.ToString(), ex.SocketErrorCode);
                                return false;
                            }
                            return true;
                        }
                        break;
                    //hack for iOS (Unity3D)
                    case SocketError.AddressFamilyNotSupported:
                        return true;
                }
                NetDebug.WriteError("[B]Bind exception: {0}, errorCode: {1}", bindException.ToString(), bindException.SocketErrorCode);
                return false;
            }
            return true;
        }

        public bool SendBroadcast(byte[] data, int offset, int size, int port)
        {
            bool success;
            try
            {
                success = _udpSocketv4.SendTo(
                             data,
                             offset,
                             size,
                             SocketFlags.None,
                             new IPEndPoint(IPAddress.Broadcast, port)) > 0;

                if (IPv6Support)
                {
                    success = success || _udpSocketv6.SendTo(
                                 data,
                                 offset,
                                 size,
                                 SocketFlags.None,
                                 new IPEndPoint(MulticastAddressV6, port)) > 0;
                }
            }
            catch (Exception ex)
            {
                NetDebug.WriteError("[S][MCAST]" + ex);
                return false;
            }
            return success;
        }

        public int SendTo(byte[] data, int offset, int size, IPEndPoint remoteEndPoint, ref SocketError errorCode)
        {
            try
            {
                var socket = _udpSocketv4;
                if (remoteEndPoint.AddressFamily == AddressFamily.InterNetworkV6 && IPv6Support)
                    socket = _udpSocketv6;
                int result = socket.SendTo(data, offset, size, SocketFlags.None, remoteEndPoint);
                NetDebug.Write(NetLogLevel.Trace, "[S]Send packet to {0}, result: {1}", remoteEndPoint, result);
                return result;
            }
            catch (SocketException ex)
            {
                switch (ex.SocketErrorCode)
                {
                    case SocketError.NoBufferSpaceAvailable:
                    case SocketError.Interrupted:
                        return 0;
                    case SocketError.MessageSize: //do nothing              
                        break;
                    default:
                        NetDebug.WriteError("[S]" + ex);
                        break;
                }
                errorCode = ex.SocketErrorCode;
                return -1;
            }
            catch (Exception ex)
            {
                NetDebug.WriteError("[S]" + ex);
                return -1;
            }
        }

        public void Close()
        {
            _running = false;
            // first close sockets
            if (_udpSocketv4 != null)
            {
                _udpSocketv4.Close();
                _udpSocketv4 = null;
            }
            if (_udpSocketv6 != null)
            {
                _udpSocketv6.Close();
                _udpSocketv6 = null;
            }
            // then join threads
            if (_threadv4 != null)
            {
                if (_threadv4 != Thread.CurrentThread)
                    _threadv4.Join();
                _threadv4 = null;
            }
            if (_threadv6 != null)
            {
                if (_threadv6 != Thread.CurrentThread)
                    _threadv6.Join();
                _threadv6 = null;
            }
        }
    }
}