// 32feet.NET - Personal Area Networking for .NET
//
// InTheHand.Net.ObexListenerContext
// 
// Copyright (c) 2003-2021 In The Hand Ltd, All rights reserved.
// This source code is licensed under the MIT License

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using InTheHand.Net;
using InTheHand.Net.Sockets;
using InTheHand.Net.Bluetooth;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using InTheHand.Net.Obex;

namespace InTheHand.Net
{
    /// <summary>
    /// Provides access to the request and response objects used by the <see cref="T:InTheHand.Net.ObexListener"/> class.
    /// </summary>
    public class ObexListenerContext2
    {
        byte[] buffer;

        private ObexListenerRequest _request;
        private readonly WebHeaderCollection _headers = new WebHeaderCollection();
        private readonly MemoryStream _bodyStream = new MemoryStream();
        private readonly EndPoint _localEndPoint;
        private readonly EndPoint _remoteEndPoint;
        ushort _remoteMaxPacket = 0;

        private Socket _socket;

        public event EventHandler ReceiveStarted;
        public event EventHandler<(string Name, long Length)> ReceivedHeaders;
        public event EventHandler<long> ReceiveProgress;
        public event EventHandler ReceiveCompleted;
        public event EventHandler<ObexListenerRequest2> RequestCompleted;

        private const string NAME_HEADER = "NAME";
        private const string LENGTH_HEADER = "LENGTH";


        internal ObexListenerContext2(Socket s)
        {
            buffer = new byte[0x2000];

            this._localEndPoint = s.LocalEndPoint;
            this._remoteEndPoint = s.RemoteEndPoint;

            _socket = s;
        }

        public async Task Receive(bool skipFirst = false, bool noKillSocket = false)
        {
            bool moretoreceive = true;
            bool putCompleted = false;
            bool fileComplete = false;
            bool disconnecting = false;

            ReceiveStarted?.Invoke(this, EventArgs.Empty);

            while (moretoreceive)
            {
                //receive the request and store the data for the request object
                int received = 0;

                if (skipFirst)
                {
                    skipFirst = false;
                    received = 3;
                }
                else
                {
                    try
                    {
                        while (received < 3)
                        {
                            int readLen = _socket.Receive(buffer, received, 3 - received, SocketFlags.None);
                            if (readLen == 0)
                            {
                                moretoreceive = false;
                                if (received == 0)
                                {

                                    break; // Waiting for first byte of packet -- OK to close then.

                                }
                                else
                                {
                                    throw new EndOfStreamException("Connection lost.");
                                }
                            }

                            received += readLen;
                        }
                        //Debug.WriteLine(s.GetHashCode().ToString("X8") + ": RecvH", "ObexListener");
                    }
                    catch (SocketException se)
                    {
                        //Console.Write(se.Message);
                        HandleConnectionError(se);
                    }
                }

                if (received == 3)
                {
                    ObexMethod method = (ObexMethod)buffer[0];
                    //get length (excluding the 3 byte header)
                    short len = (short)(IPAddress.NetworkToHostOrder(BitConverter.ToInt16(buffer, 1)) - 3);
                    if (len > 0)
                    {
                        int iPos = 0;

                        while (iPos < len)
                        {
                            int wanted = len - iPos;
                            Debug.Assert(wanted > 0, "NOT wanted > 0, is: " + wanted);
                            int receivedBytes = _socket.Receive(this.buffer, iPos + 3, wanted, SocketFlags.None);
                            if (receivedBytes == 0)
                            {
                                moretoreceive = false;
                                throw new EndOfStreamException("Connection lost.");
                            }
                            iPos += receivedBytes;
                            ReceiveProgress?.Invoke(this, _bodyStream.Length);
                        }
                    }

                    byte[] responsePacket; // Don't init, then the compiler will check that it's set below.

                    //Debug.WriteLine(s.GetHashCode().ToString("X8") + ": Method: " + method, "ObexListener");
                    responsePacket = HandleAndMakeResponse(ref moretoreceive, ref putCompleted, method, out fileComplete, out disconnecting);

                    if (fileComplete)
                    {
                        moretoreceive = false;
                    }

                    if (_headers.AllKeys.Any(x => x == NAME_HEADER) && _headers.AllKeys.Any(x => x == LENGTH_HEADER))
                    {

                        try
                        {
                            var name = ObexHeaderHelper.GetLast(_headers[NAME_HEADER]);
                            var lengthString = ObexHeaderHelper.GetLast(_headers[LENGTH_HEADER]);
                            var length = long.Parse(lengthString);

                            ReceivedHeaders?.Invoke(this, (name, length));
                        }
                        catch (Exception ex)
                        {

                        }

                    }

                    try
                    {
                        System.Diagnostics.Debug.Assert(responsePacket != null, "Must always respond to the peer.");
                        if (responsePacket != null)
                        {
                            _socket.Send(responsePacket);
                        }

                    }
                    catch (Exception se)
                    {
                        //Console.WriteLine(se.Message);
                        HandleConnectionError(se);
                    }
                }
                else
                {
                    moretoreceive = false;
                }
            }//while

            Debug.WriteLine(_socket.GetHashCode().ToString("X8") + ": Completed", "ObexListener");



            //var receiveABitMore = _socket.Receive(this.buffer, received, 1, SocketFlags.None);

            if (!disconnecting)
            {
                var request = new ObexListenerRequest2(_bodyStream.ToArray(), _headers, _localEndPoint, _remoteEndPoint);
                RequestCompleted?.Invoke(this, request);

                Console.WriteLine("Trying for another file.");
                Reset();
                // Another file?
                await Receive(noKillSocket: true);
            }
            else
            {
                putCompleted = true;
                ReceiveCompleted?.Invoke(this, EventArgs.Empty);

            }

            if (!noKillSocket)
            {
                _socket.Close();
                _socket = null;
            }


            if (!putCompleted)
            {
                // Should not return the request.
                //throw new ProtocolViolationException("No PutFinal received.");
            }
        }

        private void Reset()
        {
            _bodyStream.SetLength(0);
        }

        private byte[] HandleAndMakeResponse(ref bool moretoreceive, ref bool putCompleted, ObexMethod method, out bool fileComplete, out bool disconnecting)
        {
            fileComplete = false;
            disconnecting = false;
            byte[] responsePacket;
            switch (method)
            {
                case ObexMethod.Connect:
                    ObexParser.ParseHeaders(buffer, true, ref _remoteMaxPacket, _bodyStream, _headers);
                    responsePacket = new byte[7] { 0xA0, 0x00, 0x07, 0x10, 0x00, 0x20, 0x00 };
                    break;
                case ObexMethod.Put:
                    if (putCompleted)
                    { // Don't allow another PUT to append to the previous content!
                        goto case ObexMethod.PutFinal;
                    }
                    ObexParser.ParseHeaders(buffer, false, ref _remoteMaxPacket, _bodyStream, _headers);
                    responsePacket = new byte[3] { (byte)(ObexStatusCode.Continue | ObexStatusCode.Final), 0x00, 0x03 };
                    break;
                case ObexMethod.PutFinal:
                    if (putCompleted)
                    { // Don't allow another PUT to append to the previous content!
                        ObexStatusCode code = ObexStatusCode.Forbidden; // Any better one?
                        responsePacket = new byte[3] { (byte)(code | ObexStatusCode.Final), 0x00, 0x03 };
                        moretoreceive = false;
                        break;
                    }
                    ObexParser.ParseHeaders(buffer, false, ref _remoteMaxPacket, _bodyStream, _headers);
                    responsePacket = new byte[3] { (byte)(ObexStatusCode.OK | ObexStatusCode.Final), 0x00, 0x03 };
                    // Shouldn't return an object if the sender didn't send it all.
                    //putCompleted = true; // (Need to just assume that it does contains EndOfBody)
                    fileComplete = true;
                    break;
                case ObexMethod.Disconnect:
                    ObexParser.ParseHeaders(buffer, false, ref _remoteMaxPacket, _bodyStream, _headers);
                    responsePacket = new byte[3] { (byte)(ObexStatusCode.OK | ObexStatusCode.Final), 0x00, 0x03 };
                    moretoreceive = false;
                    disconnecting = true;
                    break;
                default:
                    //Console.WriteLine(method.ToString() + " " + received.ToString());
                    responsePacket = new byte[3] { (byte)ObexStatusCode.NotImplemented, 0x00, 0x03 };
                    break;
            }
            return responsePacket;
        }

        // Do we want to throw an exception when error occurs?
        private void HandleConnectionError(Exception ex)
        {
            throw ex;
        }

        /// <summary>
        /// Gets the <see cref="T:InTheHand.Net.ObexListenerRequest"/> that represents a client's request for a resource
        /// </summary>
        public ObexListenerRequest Request
        {
            get
            {
                return _request;
            }
        }

        /*
         * For a future version
        /// <summary>
        /// Gets the <see cref="T:InTheHand.Net.ObexListener.ObexListenerResponse"/> object that will be sent to the client in response to the client's request.
        /// </summary>
        public ObexListenerResponse Response
        {
            get
            {
                return new ObexListenerResponse();
            }
        }*/
    }
}