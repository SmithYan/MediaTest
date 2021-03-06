﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Net;
using Media.Rtp;
using Media.Rtcp;
using Media.Rtsp.Server.Streams;

namespace Media.Rtsp
{
    /// <summary>
    /// Implementation of Rtsp / RFC2326 server 
    /// http://tools.ietf.org/html/rfc2326
    /// Suppports Reliable(Rtsp / Tcp or Rtsp / Http) and Unreliable(Rtsp / Udp) connections
    /// </summary>
    public class RtspServer
    {
        public const int DefaultPort = 554;

        public const int DefaultReceiveTimeout = 1000;

        public const int DefaultSendTimeout = 1000;

        #region Nested Types

        /// <summary>
        /// Encapsulated exceptions thrown from a RtspServer
        /// </summary>
        public class RtspServerException : Exception
        {
            public RtspServerException(string message) : base(message) { }

            public RtspServerException(string message, Exception inner  ) : base(message, inner) { }
        }
        
        #endregion

        #region Fields

        DateTime? m_Started;

        /// <summary>
        /// The port the RtspServer is listening on, defaults to 554
        /// </summary>
        int m_ServerPort = 554,
            //Counters for bytes sent and recieved
            m_Recieved, m_Sent;

        /// <summary>
        /// The socket used for recieving RtspRequests
        /// </summary>
        Socket m_TcpServerSocket, m_UdpServerSocket;

        int m_MaximumClients = 1024;

        /// <summary>
        /// The version of the Rtsp protocol in use by the server
        /// </summary>
        double m_Version = 1.0;

        /// <summary>
        /// The HttpListner used for handling Rtsp over Http
        /// Todo, use Socket on Designated Port
        /// </summary>
        HttpListener m_HttpListner;

        /// <summary>
        /// The endpoint the server is listening on
        /// </summary>
        EndPoint m_ServerEndPoint;

        /// <summary>
        /// The dictionary containing all streams the server is aggregrating
        /// </summary>
        Dictionary<Guid, RtpSource> m_Streams = new Dictionary<Guid, RtpSource>();

        /// <summary>
        /// The dictionary containing all the clients the server has sessions assocaited with
        /// </summary>
        Dictionary<Guid, ClientSession> m_Clients = new Dictionary<Guid, ClientSession>();

        /// <summary>
        /// The thread allocated to handle socket communication
        /// </summary>
        Thread m_ServerThread;

        /// <summary>
        /// Indicates to the ServerThread a stop has been requested
        /// </summary>
        bool m_StopRequested;

        /// <summary>
        /// Used to signal the server to recieve new clients
        /// </summary>
        AutoResetEvent allDone = new AutoResetEvent(false);

        //Handles the Restarting of streams which needs to be and disconnects clients which are inactive.
        internal Timer m_Maintainer;

        #endregion

        #region Propeties

        /// <summary>
        /// The Version of the RtspServer (used in responses)
        /// </summary>
        public double Version { get { return m_Version; } protected set { if (value < m_Version) throw new ArgumentOutOfRangeException(); m_Version = value; } }

        /// <summary>
        /// Indicates if requests require a User Agent
        /// </summary>
        public bool RequireUserAgent { get; set; }

        /// <summary>
        /// Indicates if setup requests require a Range Header
        /// </summary>
        public bool RequireRangeHeader { get; set; }

        /// <summary>
        /// The name of the server (used in responses)
        /// </summary>
        public string ServerName { get; set; }

        /// <summary>
        /// The amount of time before the RtpServer will remove a session if no Rtsp activity has occured.
        /// </summary>
        public int ClientRtspInactivityTimeoutSeconds { get; set; }

        /// <summary>
        /// Gets or sets the ReceiveTimeout of the TcpSocket used by the RtspServer
        /// </summary>
        public int ReceiveTimeout { get { return m_TcpServerSocket.ReceiveTimeout; } set { m_TcpServerSocket.ReceiveTimeout = value; } }

        /// <summary>
        /// Gets or sets the SendTimeout of the TcpSocket used by the RtspServer
        /// </summary>
        public int SendTimeout { get { return m_TcpServerSocket.SendTimeout; } set { m_TcpServerSocket.SendTimeout = value; } }

        /// <summary>
        /// The amount of time before the RtpServer will remove a session if no Rtp activity has occured.
        /// </summary>
        //public int ClientRtpInactivityTimeoutSeconds { get; set; }

        //For controlling Port ranges, Provide events so Upnp support can be plugged in? PortClosed/PortOpened(ProtocolType, startPort, endPort?)
        public int? MinimumUdpPort { get; set; } 
        int? MaximumUdpPort { get; set; }

        /// <summary>
        /// The maximum amount of connected clients
        /// </summary>
        public int MaximumClients { get { return m_MaximumClients; } set { if (value <= 0) throw new ArgumentOutOfRangeException(); m_MaximumClients = value; } }

        /// <summary>
        /// The amount of time the server has been running
        /// </summary>
        public TimeSpan Uptime { get { if (m_Started.HasValue) return DateTime.UtcNow - m_Started.Value; return TimeSpan.Zero; } }

        /// <summary>
        /// Indicates if the RtspServer is listening for requests on the ServerPort
        /// </summary>
        public bool Listening { get { return m_ServerThread != null && m_ServerThread.ThreadState.HasFlag(ThreadState.Running); } }

        /// <summary>
        /// The port in which the RtspServer is listening for requests
        /// </summary>
        public int ServerPort { get { return m_ServerPort; } }

        /// <summary>
        /// The local endpoint for this RtspServer (The endpoint on which requests are recieved)
        /// </summary>
        public IPEndPoint LocalEndPoint { get { return m_TcpServerSocket.LocalEndPoint as IPEndPoint; } }

        /// <summary>
        /// Accesses a contained stream by id of the stream
        /// </summary>
        /// <param name="streamId">The unique identifer</param>
        /// <returns>The RtspClient assocaited with the given id if found, otherwise null</returns>
        public RtpSource this[Guid streamId] { get { return GetStream(streamId); } }

        /// <summary>
        /// The streams contained in the server
        /// </summary>
        public IEnumerable<RtpSource> Streams { get { lock (m_Streams) return m_Streams.Values.ToArray(); } }

        /// <summary>
        /// The amount of streams the server is prepared to listen to
        /// </summary>
        public int TotalStreamCount { get { return m_Streams.Count; } }

        /// <summary>
        /// The amount of active streams the server is listening to
        /// </summary>
        public int ActiveStreamCount
        {
            get
            {                
                if (TotalStreamCount == 0) return 0;
                return Streams.Where(s => s.State == SourceStream.StreamState.Stopped && s.Ready == true).Count();
            }
        }

        /// <summary>
        /// The total amount of bytes the RtspServer recieved from remote RtspRequests
        /// </summary>
        public int TotalRtspBytesRecieved { get { return m_Recieved; } }

        /// <summary>
        /// The total amount of bytes the RtspServer sent in response to remote RtspRequests
        /// </summary>
        public int TotalRtspBytesSent { get { return m_Sent; } }

        /// <summary>
        /// The amount of bytes recieved from all contained streams in the RtspServer (Might want to log the counters seperately so the totals are not lost with the streams or just not provide the property)
        /// </summary>
        public long TotalStreamBytesRecieved
        {
            get
            {
                return Streams.Sum(s => s.RtpClient != null ? s.RtpClient.TotalRtpBytesReceieved : 0);
            }
        }

        /// <summary>
        /// The amount of bytes sent to all contained streams in the RtspServer (Might want to log the counters seperately so the totals are not lost with the streams or just not provide the property)
        /// </summary>
        public long TotalStreamBytesSent
        {
            get
            {
                return Streams.Sum(s => s.RtpClient != null ?s.RtpClient.TotalRtpBytesSent : 0);
            }
        }

        public int ConnectedClients { get { return m_Clients.Count; } }

        public RtspServerLogger Logger { get; set; }

        public bool HttpEnabled { get { return m_HttpPort != -1; } }

        public bool UdpEnabled { get { return m_UdpPort != -1; } }

        #endregion

        #region Constructor

        public RtspServer(int listenPort = DefaultPort)
        {
            ClientRtspInactivityTimeoutSeconds = 60;
            ServerName = "ASTI Media Server";
            m_ServerPort = listenPort;
        }

        #endregion

        #region Methods

        int m_HttpPort = -1;
        public void EnableHttp(int port = 80) 
        {
            if (m_HttpListner == null)
            {
                try
                {
                    m_HttpListner = new HttpListener();
                    m_HttpPort = port;
                    m_HttpListner.Prefixes.Add("http://*:" + port + "/");
                    m_HttpListner.Start();
                    m_HttpListner.BeginGetContext(new AsyncCallback(ProcessHttpRtspRequest), null);
                }
                catch (Exception ex)
                {
                    throw new RtspServerException("Error Enabling Http on Port '" + port + "' : " + ex.Message, ex);
                }
            }
        }

        int m_UdpPort = -1;
        public void EnableUdp(int port = 555, bool ipV6 = false) 
        {
            if (m_UdpServerSocket != null)
            {
                try
                {
                    m_UdpPort = port;
                    if (ipV6)
                    {
                        m_UdpServerSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                        m_UdpServerSocket.Bind(new IPEndPoint(Utility.GetFirstIPAddress(AddressFamily.InterNetworkV6), port));
                    }
                    else
                    {
                        m_UdpServerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        m_UdpServerSocket.Bind(new IPEndPoint(Utility.GetFirstV4IPAddress(), port));
                    }
                    //Begin the initial recieve
                    {
                        ClientSession temp = new ClientSession(this, null);
                        temp.m_RtspSocket = m_UdpServerSocket;
                        m_UdpServerSocket.BeginReceive(temp.m_Buffer, 0, temp.m_Buffer.Length, SocketFlags.None, new AsyncCallback(ProcessReceive), temp);
                    }
                }
                catch(Exception ex)
                {
                    throw new RtspServerException("Error Enabling Udp on Port '" + port + "' : " + ex.Message, ex);
                }
            }
        }

        public void DisableHttp()
        {
            if (m_HttpListner != null)
            {
                m_HttpListner.Stop();
                m_HttpListner.Close();
                m_HttpListner = null;
            }
        }

        public void DisableUdp()
        {
            if (m_UdpServerSocket != null)
            {
                m_UdpServerSocket.Shutdown(SocketShutdown.Both);
                m_UdpServerSocket.Dispose();
                m_UdpServerSocket = null;
            }
        }

        #region Session Collection

        internal void AddSession(ClientSession session)
        {
            lock (m_Clients)
            {
                m_Clients.Add(session.Id, session);
            }
        }

        internal bool RemoveSession(ClientSession session)
        {
            lock (m_Clients)
            {
                return m_Clients.Remove(session.Id);
            }
        }

        internal bool ContainsSession(ClientSession session)
        {
            return m_Clients.ContainsKey(session.Id);
        }

        internal ClientSession GetSession(Guid id)
        {
            ClientSession result;
            m_Clients.TryGetValue(id, out result);
            return result;
        }

        internal ClientSession FindSessionByRtspSessionId(string rtspSessionId)
        {
            if (string.IsNullOrWhiteSpace(rtspSessionId)) return null;
            rtspSessionId = rtspSessionId.Trim();
            return m_Clients.Values.Where(c => c.SessionId != null && c.SessionId.Equals(rtspSessionId)).FirstOrDefault();
        }               

        #endregion

        #region Stream Collection

        /// <summary>
        /// Adds a stream to the server. If the server is already started then the stream will also be started
        /// </summary>
        /// <param name="location">The uri of the stream</param>
        public void AddStream(RtpSource stream)
        {
            if (ContainsStream(stream.Id)) throw new RtspServerException("Cannot add the given stream because it is already contained in the RtspServer");
            else
            {
                lock (m_Streams)
                {
                    //Remember to have clients indicate PlayFromStart if they want all sessions to start at 0
                    m_Streams.Add(stream.Id, stream);
                }

                //If we are listening start the stram
                if (Listening) stream.Start();
            }
        }

        /// <summary>
        /// Indicates if the RtspServer contains the given streamId
        /// </summary>
        /// <param name="streamId">The id of the stream</param>
        /// <returns>True if the stream is contained, otherwise false</returns>
        public bool ContainsStream(Guid streamId)
        {
            return m_Streams.ContainsKey(streamId);
        }

        /// <summary>
        /// Stops and Removes a stream from the server
        /// </summary>
        /// <param name="streamId">The id of the stream</param>
        /// <param name="stop">True if the stream should be stopped when removed</param>
        /// <returns>True if removed, otherwise false</returns>
        public bool RemoveStream(Guid streamId, bool stop = true)
        {
            try
            {
                if (m_Streams.ContainsKey(streamId))
                {
                    RtpSource source = this[streamId];
                    if (stop) source.Stop();
                    lock (m_Streams)
                    {
                        return m_Streams.Remove(streamId);
                    }
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public RtpSource GetStream(Guid streamId)
        {
            RtpSource result;
            m_Streams.TryGetValue(streamId, out result);
            return result;
        }

        /// <summary>
        /// </summary>
        /// <param name="mediaLocation"></param>
        /// <returns></returns>
        internal RtpSource FindStreamByLocation(Uri mediaLocation)
        {
            RtpSource found = null;

            string streamBase = null, streamName = null;

            foreach (string segmentPart in mediaLocation.Segments)
            {
                string segment = segmentPart.Replace("/", string.Empty);

                if (segment.ToLowerInvariant() == "live")
                {
                    //Live play
                    streamBase = segment;
                    continue;
                }
                else if (segment.ToLowerInvariant() == "archive"){  

                    //Archive
                    streamBase = segment; 
                    continue; 
                }

                //If we have the base then the next part is our streamName
                if (streamBase != null) 
                {
                    //Convert to lower case
                    streamName = segment.ToLowerInvariant();
                    //Done
                    break;
                }
            }

            //If either the streamBase or the streamName is null or Whitespace then return null (no stream)
            if (string.IsNullOrWhiteSpace(streamBase) || string.IsNullOrWhiteSpace(streamName)) return null;

            //handle live streams
            if (streamBase == "live")
            {
                foreach (RtpSource stream in Streams)
                {

                    //If the name matches the streamName or stream Id then we found it
                    if (stream.Name.ToLowerInvariant() == streamName || stream.Id.ToString() == streamName)
                    {
                        found = stream;
                        break;
                    }

                    //Try aliases of streams
                    if (found == null)
                    {
                        foreach (string alias in stream.m_Aliases)
                        {
                            if (alias.ToLowerInvariant() == streamName)
                            {
                                found = stream;
                                break;
                            }
                        }
                    }
                }
            }
            else
            {
                //Need facilites for creating a RtspStream from an archive file
                //Should have a static constructor RtspArchivedStream.FromMediaLocation(Url location)
                //Needs the ci who requests this media to attached the archives stream to... 
            }

            return found;
        }

        #endregion

        #region Server Logic

        /// <summary>
        /// Finds and removes inactive clients.
        /// Determined by the time of the sessions last RecieversReport or the last RtspRequestRecieved (get parameter must be sent to keep from timing out)
        /// </summary>
        internal void DisconnectAndRemoveInactiveSessions(object state = null) { DisconnectAndRemoveInactiveSessions(); }
        internal void DisconnectAndRemoveInactiveSessions()
        {
            //Find inactive clients and remove..
            IEnumerable<ClientSession> clients;
            lock (m_Clients)
            {
                 clients = m_Clients.Values.ToArray();
            }
            
            //Iterate and find inactive sessions
            foreach (ClientSession session in clients)
            {
                //If the inactivity timeout is not disabled
                if (ClientRtspInactivityTimeoutSeconds != -1 && (DateTime.UtcNow - session.m_LastRtspRequestRecieved).TotalSeconds > ClientRtspInactivityTimeoutSeconds)
                {
                    if(session.m_RtpClient != null) session.m_RtpClient.SendGoodbyes();
                    RemoveSession(session);
                }
            }
        }

        /// <summary>
        /// Restarted streams which should be Listening but are not
        /// </summary>
        internal void RestartFaultedStreams(object state = null) { RestartFaultedStreams(); }
        internal void RestartFaultedStreams()
        {
            foreach (RtpSource stream in Streams.Where(s => s.State == RtspSourceStream.StreamState.Started && s.Ready == false))
            {
                try
                {
                    //Ensure Stopped
                    stream.Stop();

                    //try to start it again
                    stream.Start();
                }
                catch { }
            }
        }

        /// <summary>
        /// Starts the RtspServer and listens for requests.
        /// Starts all streams contained in the server
        /// </summary>
        public virtual void Start()
        {
            //If we already have a thread return
            if (m_ServerThread != null) return;

            m_StopRequested = false;

            //Start streaming from m_Streams
            StartStreams();

            //Start listening for requests....

            ///Create the server EndPoint
            m_ServerEndPoint = new IPEndPoint(IPAddress.Any, m_ServerPort);

            //Create the server Socket (Should allow InterNetworkV6)
            m_TcpServerSocket = new Socket(IPAddress.Any.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            //Bind the server Socket to the server EndPoint
            m_TcpServerSocket.Bind(m_ServerEndPoint);

            //Set the backlog
            m_TcpServerSocket.Listen(MaximumClients);

            //Set the recieve timeout
            m_TcpServerSocket.ReceiveTimeout = DefaultReceiveTimeout;

            m_TcpServerSocket.SendTimeout = DefaultSendTimeout;

            //Create a thread to handle client connections
            m_ServerThread = new Thread(new ThreadStart(RecieveLoop));
            m_ServerThread.Name = "RtspServer@" + m_ServerPort;
            m_ServerThread.Start();

            //Should allow all this frequencies to be controlled with a property
            m_Maintainer = new Timer(new TimerCallback(MaintainServer), null, 30000, 30000);

            m_Started = DateTime.UtcNow;

            if (m_UdpPort != -1) EnableUdp(m_UdpPort);
            if (m_HttpPort != -1) EnableHttp(m_HttpPort);

        }

        /// <summary>
        /// Removes Inactive Sessions and Restarts Faulted Streams
        /// </summary>
        /// <param name="state">Reserved</param>
        internal virtual void MaintainServer(object state = null)
        {
            try
            {
                DisconnectAndRemoveInactiveSessions(state);
                RestartFaultedStreams(state);
            }
            catch { }
        }

        /// <summary>
        /// Stops recieving RtspRequests and stops streaming all contained streams
        /// </summary>
        public virtual void Stop()
        {
            //If there is not a server thread return
            if (m_ServerThread == null) return;

            //Stop listening for requests
            m_StopRequested = true;

            //Abort the thread
            m_ServerThread.Abort();

            //Free the member so we can start again
            m_ServerThread = null;

            if (m_Maintainer != null)
            {
                m_Maintainer.Dispose();
                m_Maintainer = null;
            }

            //Stop listening on client streams
            StopStreams();

            //Remove all clients
            foreach (ClientSession cs in m_Clients.Values.ToList())
            {
                cs.Disconnect();
                RemoveSession(cs);
            }

            //Dispose the socket
            m_TcpServerSocket.Dispose();

            //Stop other listeners
            DisableHttp();
            DisableUdp();

            //Erase statistics
            m_Started = null;
        }

        /// <summary>
        /// Starts all streams contained in the video server
        /// </summary>
        internal virtual void StartStreams()
        {
            foreach (RtpSource stream in Streams)
            {
                try
                {
                    stream.Start();
                }
                catch
                {
                    continue;
                }
            }
        }

        /// <summary>
        /// Stops all contained streams from streaming
        /// </summary>
        internal virtual void StopStreams()
        {
            foreach (RtpSource stream in Streams)
            {
                try
                {
                    stream.Stop();
                }
                catch
                {
                    continue;
                }
            }
        }        

        /// <summary>
        /// The loop where Rtsp Requests are recieved
        /// </summary>
        internal virtual void RecieveLoop()
        {
            int timeOut = 0;

            while (!m_StopRequested)
            {
                //If we can accept
                if (m_Clients.Count < m_MaximumClients)
                {
                    //Get the timeout from the socket
                    timeOut = m_TcpServerSocket.ReceiveTimeout;

                    //If the timeout is infinite only wait for the default
                    if (timeOut <= 0) timeOut = DefaultReceiveTimeout;

                    //Start acceping
                    m_TcpServerSocket.BeginAccept(new AsyncCallback(ProcessAccept), m_TcpServerSocket);

                    //Wait half using the event
                    while (!allDone.WaitOne(timeOut / 2))
                    {                        
                        //Wait the other half
                        if (allDone.WaitOne(timeOut / 2)) break;
                    }
                }
            }
        }

        #endregion

        #region Socket Methods

        /// <summary>
        /// Handles the accept of rtsp client sockets into the server
        /// </summary>
        /// <param name="ar">The asynch result</param>
        internal void ProcessAccept(IAsyncResult ar)
        {
            try
            {
                //Reset the event so another client can join
                allDone.Set();

                Socket svr = (Socket)ar.AsyncState;

                Socket clientSocket = svr.EndAccept(ar);

                //Make a temporary client (Could move semantics about begin recieve to ClientSession)
                ClientSession ci = new ClientSession(this, clientSocket);

                //Start receiving into the client buffer
                clientSocket.BeginReceive(ci.m_Buffer, 0, ci.m_Buffer.Length, SocketFlags.None, new AsyncCallback(ProcessReceive), ci);
#if DEBUG
                System.Diagnostics.Debug.WriteLine("Accepted connection from: {0}, Assigned Id = {1}", clientSocket.RemoteEndPoint, ci.Id);
#endif
            }
#if DEBUG
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Accept failed with: {0}", ex);
            }
#else
            catch { }
#endif            
        }

        /// <summary>
        /// Handles the recieving of sockets data from a rtspClient
        /// </summary>
        /// <param name="ar">The asynch result</param>
        internal void ProcessReceive(IAsyncResult ar)
        {
            //Get the client information
            ClientSession session = (ClientSession)ar.AsyncState;
            if (session == null) return;

            int received = 0;
            RtspRequest request = null;

            try
            {
                //If we are Tcp we can just end the recieve
                if (session.m_RtspSocket.ProtocolType == ProtocolType.Tcp)                
                {
                    received = session.m_RtspSocket.EndReceive(ar);
                }
                else //Udp
                {
                    //If this is the inital receive
                    if (m_UdpServerSocket.Handle == session.m_RtspSocket.Handle)
                    {
                        //End it
                        received = m_UdpServerSocket.EndReceive(ar);

                        //Start recieving on the Udp Socket again
                        {
                            ClientSession temp = new ClientSession(this, m_UdpServerSocket);
                            m_UdpServerSocket.BeginReceive(temp.m_Buffer, 0, temp.m_Buffer.Length, SocketFlags.None, new AsyncCallback(ProcessReceive), temp);
                        }

                        //Determine who sent the request (Should use overload of Receive with Ep?)
                        IPEndPoint remote = (IPEndPoint)m_UdpServerSocket.RemoteEndPoint;
                        
                        //Easier then Creating and configuring the socket.
                        //TODO - (Stop being Lazy and use socket)
                        session.m_Udp = new UdpClient();
                        session.m_Udp.AllowNatTraversal(true);
                        session.m_Udp.ExclusiveAddressUse = false;
                        //session.m_Udp.Ttl = 255;
                        session.m_Udp.Connect(remote);

                        //Ensure the socket is assigned from the client
                        session.m_RtspSocket = session.m_Udp.Client;
                    }
                    else //This is a repeated recieve
                    {
                        IPEndPoint remote = null;
                        session.m_Buffer = session.m_Udp.EndReceive(ar, ref remote);
                        //remote.Address should match 
                        received = session.m_Buffer.Length;
                    }
                }

                //If we received anything
                if (received > 0)
                {
                    //Parse the request to determine if there is actually an existing session before proceeding
                    request = new RtspRequest(session.m_Buffer);

                    //Log it
                    if (Logger != null) Logger.LogRequest(request, session);

                    //If there is a Session Header
                    if (request.ContainsHeader(RtspHeaders.Session))
                    {
                        //Try to find a matching session
                        ClientSession existing = FindSessionByRtspSessionId(request.GetHeader(RtspHeaders.Session));
                        //If there is an existing session with the id
                        if (existing != null)
                        {
                            //If the request EndPoint does not match the session EndPoint the person tried to fake request for Session
                            if (existing.m_RtspSocket.RemoteEndPoint != session.m_RtspSocket.RemoteEndPoint)
                            {
                                ProcessInvalidRtspRequest(session, RtspStatusCode.Unauthorized);
                                return;
                            }
                            //else //Sessions matched and EndPoints matched
                            //{
                            //    //Should be the same anyway
                            //    //session = existing;
                            //}
                        }
                        else
                        {
                            //A Session was given but could not be found :(
                            ProcessInvalidRtspRequest(session, RtspStatusCode.SessionNotFound);
                            return;
                        }
                    }
                    else if (!ContainsSession(session)) //Otherwise if we didn't have a record of the session then add it now
                    {
                        AddSession(session);
                    }

                    //Determine if we support what the client requests in `Required` Header
                    if (request.ContainsHeader(RtspHeaders.Required))
                    {
                        //
                    }

                    //Process the request
                    ProcessRtspRequest(request, session);
                }
                else// We recieved nothing
                {
                    //This happens then Just recieve again
                    session.m_RtspSocket.BeginReceive(session.m_Buffer, 0, session.m_Buffer.Length, SocketFlags.None, new AsyncCallback(ProcessReceive), session);
                }
            }
            catch(Exception ex)
            {
                //Something happened during the session
                if (Logger != null) Logger.LogException(ex, request, session);
                //If there is a session
                if (session != null)
                {
                    //End it
                    ProcessInvalidRtspRequest(session);
                    return;
                }
            }
            finally
            {
                request = null;
                //Count for the server
                m_Recieved += received;

                //Count for the session (Should be done in the session request handler?)
                if (session != null)
                {
                    session.m_Receieved += received;
                }
            }
        }

        /// <summary>
        /// Handles the sending of responses to clients which made requests
        /// </summary>
        /// <param name="ar">The asynch result</param>
        internal void ProcessSend(IAsyncResult ar)
        {
            ClientSession session = (ClientSession)ar.AsyncState;
            if (session == null) return;
            int sent = 0;
            try
            {
                //Rtsp Tcp
                if (session.m_RtspSocket.ProtocolType == ProtocolType.Tcp)
                {
                    sent = session.m_RtspSocket.EndSend(ar);

                    session.m_Sent += sent;

                    m_Sent += sent;

                    //If the client is interleaving
                    if (session.m_RtpClient != null && session.m_RtpClient.m_TransportProtocol == ProtocolType.Tcp)
                    {
                        //The request is in the buffer (Complete this)
                        ProcessRtspRequest(new RtspRequest(session.m_Buffer), session);
                    }
                    else //The client is not interleaving
                    {
                        // We can recieve again
                        session.m_RtspSocket.BeginReceive(session.m_Buffer, 0, session.m_Buffer.Length, SocketFlags.None, new AsyncCallback(ProcessReceive), session);
                    }
                }
                else //Rtsp Udp
                {
                    sent = session.m_Udp.EndSend(ar);

                    session.m_Sent += sent;

                    m_Sent += sent;

                    //Use the Udp Client for the Session (might make just use Sockets eventually)
                    //Complete this after Tcp Interleaving
                    session.m_Udp.BeginReceive(new AsyncCallback(ProcessReceive), session);
                }
            }
#if DEBUG
            catch (Exception ex)
            {

                System.Diagnostics.Debug.WriteLine("Exception in ProcessSend: " + ex.ToString());
            }
#else 
            catch { }                
#endif            
        }

        #endregion

        #region Rtsp Request Handling Methods

        //Todo use sockets and don't require a Http Listener.
        internal void ProcessHttpRtspRequest(IAsyncResult state)
        {
            try
            {
                //Could do this without the HttpListner but I feel that in end it will give more flexibility
                HttpListenerContext context = m_HttpListner.EndGetContext(state);

                //Begin to Recieve another client
                m_HttpListner.BeginGetContext(new AsyncCallback(ProcessHttpRtspRequest), null);

                //If the Accept header is not present then this is not a valid request
                if (context.Request.Headers.Get("Accept") != "application/x-rtsp-tunnelled")
                {
                    //Ignore invalid request or return 500? TransportInvalid?
                    //Give back nothing for now
                    context.Response.Close();
                    return;
                }

                #region Comments and source reference

                //http://comments.gmane.org/gmane.comp.multimedia.live555.devel/5896
                //http://cgit.freedesktop.org/gstreamer/gst-plugins-base/tree/gst-libs/gst/rtsp/gstrtspconnection.c?id=88110ea67e7d5240a7262dbb9c4e5d8db565cccf
                //http://www.live555.com/liveMedia/doxygen/html/RTSPClient_8cpp-source.html
                //https://developer.apple.com/quicktime/icefloe/dispatch028.html
                //Can't find anything in the RFC except one example
                //MAY ALSO NEED ICE AND STUN?

                #endregion

                int len = int.Parse(context.Request.Headers.Get("Content-Length"), System.Globalization.CultureInfo.InvariantCulture);
                byte[] buffer = new byte[len];

                //Get RtspRequest from Body and base64 decode as request
                int rec = context.Request.InputStream.Read(buffer, 0, len);


                RtspRequest request = null;

                try
                {
                    request = new RtspRequest(System.Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(buffer, 0, len)));
                }
                catch
                {
                    //invalid request
                }

                ClientSession ci;

                // Attempt to find existing session
                if (request != null && request.ContainsHeader(RtspHeaders.Session))
                {
                    ci = FindSessionByRtspSessionId(request[RtspHeaders.Session]);
                }
                else // Create a new session
                {
                    ci = new ClientSession(this, null);
                    ci.m_Http = context;
                }

                //If we have a client
                if (request != null && ci != null)
                {

                    //Process request
                    ProcessRtspRequest(request, ci);
                }

                //Process the Response as the server deson't respond for Http
                RtspResponse response = ci != null && ci.m_LastResponse != null ? ci.m_LastResponse : new RtspResponse()
                {
                    CSeq =  request != null ? request.CSeq : 1,
                    StatusCode = RtspStatusCode.SessionNotFound
                };

                context.Response.ContentType = "application/x-rtsp-tunnelled";
                context.Response.AddHeader("Pragma", "no-cache");
                context.Response.AddHeader("Cache-Control", "no-cache");

                buffer = response.ToBytes();

                buffer = response.Encoding.GetBytes(Convert.ToBase64String(buffer));

                context.Response.AddHeader("Content-Length", buffer.Length.ToString());

                context.Response.StatusCode = 200;

                context.Response.OutputStream.Write(buffer, 0, buffer.Length);

                context.Response.OutputStream.Close();

                context.Response.Close();

                //If there was a session
                if (ci != null)
                {
                    //Update coutners
                }
            }
#if DEBUG
            catch(Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Exception in ProcessHttpRtspRequest: " + ex.Message);
            }
#else
            catch { }
#endif
        }

        /// <summary>
        /// Processes a RtspRequest based on the contents
        /// </summary>
        /// <param name="request">The rtsp Request</param>
        /// <param name="session">The client information</param>
        internal void ProcessRtspRequest(RtspRequest request, ClientSession session)
        {
            //Ensure we have a session and request
            if (request == null || session == null)
            {
                //We can't identify the request or session
                return;
            }

            //All requests need the CSeq
            if (!request.ContainsHeader(RtspHeaders.CSeq))
            {
                ProcessInvalidRtspRequest(session);
                return;
            }
            else if (session.LastRequest != null && request.CSeq == session.LastRequest.CSeq) //Check for a duplicate request
            {
                //TODO Fix me 
                //Do nothing just to allow vlc to continue for now
                return;
            }

            //Synchronize the server and client since this is not a duplicate
            session.LastRequest = request;

            //If there is a body and no content-length
            if (string.IsNullOrWhiteSpace(request.Body) && !request.ContainsHeader(RtspHeaders.ContentLength))
            {
                ProcessInvalidRtspRequest(session);
                return;
            }            

            //Optional Checks

            //UserAgent
            if (RequireUserAgent && !request.ContainsHeader(RtspHeaders.UserAgent))
            {
                ProcessInvalidRtspRequest(session);
                return;
            }

            //Version
            if (request.Version > Version)
            {
                ProcessInvalidRtspRequest(session, RtspStatusCode.VersionNotSupported);
                return;
            }

            //Determine the handler for the request and process it
            switch (request.Method)
            {
                case RtspMethod.OPTIONS:
                    {
                        ProcessRtspOptions(request, session);
                        //Check for pipline?
                        break;
                    }
                case RtspMethod.DESCRIBE:
                    {
                        ProcessRtspDescribe(request, session);
                        //Check for pipline?
                        break;
                    }
                case RtspMethod.SETUP:
                    {
                        ProcessRtspSetup(request, session);
                        //Check for pipline?
                        break;
                    }
                case RtspMethod.PLAY:
                    {
                        ProcessRtspPlay(request, session);
                        break;
                    }
                case RtspMethod.RECORD:
                    {
                        //Not yet implimented
                        goto default;
                    }
                case RtspMethod.PAUSE:
                    {
                        ProcessRtspPause(request, session);
                        break;
                    }
                case RtspMethod.TEARDOWN:
                    {
                        ProcessRtspTeardown(request, session);
                        break;
                    }
                case RtspMethod.GET_PARAMETER:
                    {
                        ProcessGetParameter(request, session);
                        break;
                    }
                case RtspMethod.UNKNOWN:
                default:                
                    {
                        ProcessInvalidRtspRequest(session, RtspStatusCode.MethodNotAllowed);
                        break;
                    }
            }

        }

        /// <summary>
        /// Sends a Rtsp Response on the given client session
        /// </summary>
        /// <param name="response">The RtspResponse to send</param> If this was byte[] then it could handle http
        /// <param name="ci">The session to send the response on</param>
        internal void ProcessSendRtspResponse(RtspResponse response, ClientSession ci)
        {
            if (!response.ContainsHeader(RtspHeaders.Server))
            {
                response.SetHeader(RtspHeaders.Server, ServerName);
            }
            
            /* Add Supported Header
            Supported: play.basic, con.persistent
                       (basic play, TCP is supported)
            setup.playing means that setup and teardown can be used in the play state.
            Should also check the Require: header because this means the client is looking for a feature
            */

            try
            {
                //If we have a session
                if (ci != null)
                {
                    ci.m_LastResponse = response;
                    if (ci.m_Http != null)
                    {
                        //Don't http handle
                        return;
                    }

                    byte[] buffer = response.ToBytes();

                    //Begin to Send the response over the RtspSocket
                    if (ci.m_RtspSocket.ProtocolType == ProtocolType.Tcp)
                        ci.m_RtspSocket.BeginSend(buffer, 0, buffer.Length, SocketFlags.None, new AsyncCallback(ProcessSend), ci);
                    else
                        ci.m_Udp.BeginSend(buffer, buffer.Length, (IPEndPoint)ci.m_Udp.Client.RemoteEndPoint, new AsyncCallback(ProcessSend), ci);
                }
            }
            catch (SocketException)
            {
                //Most likely a tear down
            }
            catch
            {
                throw;
            }
            finally
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine(response.m_FirstLine);
#endif
                if (Logger != null)
                {
                    Logger.LogResponse(response, ci);
                }
            }
        }

        /// <summary>
        /// Sends a Rtsp Response on the given client session
        /// </summary>
        /// <param name="ci">The client session to send the response on</param>
        /// <param name="code">The status code of the response if other than BadRequest</param>
        //Should allow a header to be put into the response or a KeyValuePair<string,string> headers
        internal void ProcessInvalidRtspRequest(ClientSession session, RtspStatusCode code = RtspStatusCode.BadRequest)
        {
            //Create and Send the response
            ProcessInvalidRtspRequest(session != null ? session.CreateRtspResponse(null, code) : new RtspResponse() { StatusCode = code }, session);
        }

        internal void ProcessInvalidRtspRequest(RtspResponse response, ClientSession session) { ProcessSendRtspResponse(response, session); }

        /// <summary>
        /// Sends a Rtsp LocationNotFound Response
        /// </summary>
        /// <param name="ci">The session to send the response on</param>
        internal void ProcessLocationNotFoundRtspRequest(ClientSession ci)
        {
            ProcessInvalidRtspRequest(ci, RtspStatusCode.NotFound);
        }

        internal void ProcessAuthorizationRequired(SourceStream source, ClientSession session)
        {

            RtspResponse response = new RtspResponse();
            response.CSeq = session.LastRequest.CSeq;

            RtspStatusCode statusCode;

            //If the last request did not have an authorization header
            if (session.LastRequest != null && !session.LastRequest.ContainsHeader(RtspHeaders.Authorization))
            {
                /* -- http://tools.ietf.org/html/rfc2617
                 
    qop
     Indicates what "quality of protection" the client has applied to
     the message. If present, its value MUST be one of the alternatives
     the server indicated it supports in the WWW-Authenticate header.
     These values affect the computation of the request-digest. Note
     that this is a single token, not a quoted list of alternatives as
     in WWW- Authenticate.  This directive is optional in order to
     preserve backward compatibility with a minimal implementation of
     RFC 2617 [6], but SHOULD be used if the server indicated that qop
     is supported by providing a qop directive in the WWW-Authenticate
     header field.

   cnonce
     This MUST be specified if a qop directive is sent (see above), and
     MUST NOT be specified if the server did not send a qop directive in
     the WWW-Authenticate header field.  The cnonce-value is an opaque
     quoted string value provided by the client and used by both client
     and server to avoid chosen plaintext attacks, to provide mutual
     authentication, and to provide some message integrity protection.
     See the descriptions below of the calculation of the response-
     digest and request-digest values.     
                 
                 */

                //Could retrieve values from last Request if needed..
                //string realm = "//", nOnceCount = "00000001";

                //Should store the nonce and cnonce values on the session
                statusCode = RtspStatusCode.Unauthorized;

                string authenticateHeader = null;

                if (source.RemoteAuthenticationScheme == AuthenticationSchemes.Digest)
                {
                    //Might need to store values qop nc, cnonce and nonce in session storage for later retrival
                    authenticateHeader = string.Format(System.Globalization.CultureInfo.InvariantCulture, "Digest username={0},realm={1},nonce={2},cnonce={3}", source.RemoteCredential.UserName, source.RemoteCredential.Domain ?? "//", ((long)(Utility.Random.Next(int.MaxValue) << 32 | (Utility.Random.Next(int.MaxValue)))).ToString("X"), Utility.Random.Next(int.MaxValue).ToString("X"));                    
                }
                else if (source.RemoteAuthenticationScheme == AuthenticationSchemes.Basic)
                {
                    authenticateHeader = "Basic realm=" + source.RemoteCredential.Domain ?? "//";                    
                }

                if(!string.IsNullOrWhiteSpace(authenticateHeader))
                { 
                    response.SetHeader(RtspHeaders.WWWAuthenticate, authenticateHeader);
                }
            }
            else //Authorization header was present but data was incorrect
            {

                //should check to ensure wrong type was not used e.g. basic in place of digest...

                if (source.RemoteAuthenticationScheme == AuthenticationSchemes.Digest)
                {
                    //Increment NonceCount
                }

                //Increment session attempts?

                statusCode = RtspStatusCode.Forbidden;
            }
            
            //Set the status code
            response.StatusCode = statusCode;

            //Send the response
            ProcessInvalidRtspRequest(response, session);
        }

        /// <summary>
        /// Provides the Options this server supports
        /// </summary>
        /// <param name="request"></param>
        /// <param name="session"></param>
        internal void ProcessRtspOptions(RtspRequest request, ClientSession session)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine("OPTIONS " + request.Location);
#endif

            RtpSource found = FindStreamByLocation(request.Location);

            //No stream with name
            if (found == null)
            {
                ProcessLocationNotFoundRtspRequest(session);
                return;
            }

            RtspResponse resp = session.CreateRtspResponse(request);
            
            //resp.SetHeader(RtspHeaders.Public, "OPTIONS, DESCRIBE, SETUP, PLAY, TEARDOWN, GET_PARAMETER"); //Causes VLC to try options again and again
            resp.SetHeader(RtspHeaders.Public, " DESCRIBE, SETUP, PLAY, PAUSE, TEARDOWN, GET_PARAMETER"/*, OPTIONS"*/); //Options is really not needed anyway            

            //Should allow server to have certain options removed from this result

            ProcessSendRtspResponse(resp, session);
        }

        /// <summary>
        /// Decribes the requested stream
        /// </summary>
        /// <param name="request"></param>
        /// <param name="session"></param>
        internal void ProcessRtspDescribe(RtspRequest request, ClientSession session)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine("DESCRIBE " + request.Location);
#endif

            string acceptHeader = request[RtspHeaders.Accept];

            if (string.IsNullOrWhiteSpace(acceptHeader) || acceptHeader.Trim() != "application/sdp")
            {
                ProcessInvalidRtspRequest(session);
                return;
            }

            RtpSource found = FindStreamByLocation(request.Location);

            if (found == null)
            {
                ProcessLocationNotFoundRtspRequest(session);
                return;
            }

            if (!AuthenticateRequest(request, found))
            {
                ProcessAuthorizationRequired(found, session);
                return;
            }

            if (!found.Ready)
            {
                ProcessInvalidRtspRequest(session, RtspStatusCode.MethodNotAllowed);
                return;
            }

            //Chould check to see if ci has an existing session desciprtion

            RtspResponse resp = session.CreateRtspResponse(request);

            if (request.Location.ToString().ToLowerInvariant().Contains("live"))
            {
                resp.SetHeader(RtspHeaders.ContentBase, "rtsp://" + ((IPEndPoint)session.m_RtspSocket.LocalEndPoint).Address.ToString() + "/live/" + found.Id +'/');
            }
            else
            {
                resp.SetHeader(RtspHeaders.ContentBase, request.Location.ToString());
            }

            resp.SetHeader(RtspHeaders.ContentType, "application/sdp");
            

            //Should only do this if the source Transport Profile is Rtp or it requires a SDP...

            //Create the SDP from the found media
            session.CreateSessionDescription(found);
            resp.Body = session.SessionDescription.ToString();

            ProcessSendRtspResponse(resp, session);
        }

        /// <summary>
        /// Sets the given session up
        /// </summary>
        /// <param name="request"></param>
        /// <param name="session"></param>
        internal void ProcessRtspSetup(RtspRequest request, ClientSession session)
        {

#if DEBUG
            System.Diagnostics.Debug.WriteLine("SETUP " + request.Location);
#endif

            RtpSource found = FindStreamByLocation(request.Location);
            if (found == null)
            {                
                ProcessLocationNotFoundRtspRequest(session);
                return;
            }            
            else if (!found.Ready)
            {
                //Stream is not yet ready
                ProcessInvalidRtspRequest(session, RtspStatusCode.PreconditionFailed);
                return;
            }

            //Determine if we have the track
            string track = request.Location.Segments.Last();

            Sdp.MediaDescription mediaDescription = null;

            //Find the MediaDescription
            foreach (Sdp.MediaDescription md in found.SessionDescription.MediaDescriptions)
            {
                Sdp.SessionDescriptionLine attributeLine = md.Lines.Where(l => l.Type == 'a' && l.Parts.Any(p => p.Contains("control"))).FirstOrDefault();
                if (attributeLine != null) 
                {
                    string actualTrack = attributeLine.Parts.Where(p => p.Contains("control")).First().Replace("control:", string.Empty);
                    if(actualTrack == track || actualTrack.Contains(track))
                    {
                        mediaDescription = md;
                        break;
                    }
                }
            }

            //Cannot setup media
            if (mediaDescription == null)
            {
                ProcessLocationNotFoundRtspRequest(session);
                return;
            }

            //Add the state information for the source
            RtpClient.TransportContext sourceTransportChannel = null;

            //Either change the construct to RtpSourceStream on Server or make Interleaves available or not required
            sourceTransportChannel = found.RtpClient.TransportContexts.Where(c => c.MediaDescription.MediaType == mediaDescription.MediaType && c.MediaDescription.MediaFormat == mediaDescription.MediaFormat).First();

            //If the source has no transportChannel for that format(unlikely) or the source has not recieved a packet yet
            if (sourceTransportChannel == null)
            {
                //Stream is not yet ready
                ProcessInvalidRtspRequest(session, RtspStatusCode.PreconditionFailed);
                return;
            }
          
            //Add the sourceInterleave
            session.SourceContexts.Add(sourceTransportChannel);

            if (!AuthenticateRequest(request, found))
            {
                ProcessAuthorizationRequired(found, session);
                return;
            }

            //Get the transport header
            string transportHeader = request[RtspHeaders.Transport];

            //If that is not present we cannot determine what transport the client wants
            if (string.IsNullOrWhiteSpace(transportHeader) || !(transportHeader.Contains("RTP")))
            {
                ProcessInvalidRtspRequest(session);
                return;
            }            

            //comes from transportHeader client_port= (We just send it back)
            string clientPortDirective = null; 

            string[] parts = transportHeader.Split(';');

            //ProtocolType requestedProtcolType = ProtocolType.Udp;

            string[] channels = null, clientPorts = null;

            //Loop the parts (Exchange for split and then query)
            for (int i = 0, e = parts.Length; i < e; ++i)
            {
                string part = parts[i].Trim();
                if (part.StartsWith("interleaved="))
                {
                    channels = part.Replace("interleaved=", string.Empty).Split('-');                    
                }
                else if (part.StartsWith("client_port="))
                {
                    clientPortDirective = part.Replace("client_port=", string.Empty);
                    clientPorts = clientPortDirective.Split('-');
                }
            }            

            //We also have to send one back
            string returnTransportHeader = null;

            //If there was no client port w and no channels cannot setup the media
            if (clientPortDirective == null && channels == null)
            {
                ProcessInvalidRtspRequest(session, RtspStatusCode.BadRequest);
                return;
            }

            //If there are Bandwidth lines with RR:0 and RS:0
            IEnumerable<Media.Sdp.SessionDescriptionLine> rtcpLines = mediaDescription.Lines.Where(l => l.Type == 'b' && l.Parts.Count > 1 && (l.Parts[0] == "RR" || l.Parts[0] == "RS") && l.Parts[1] == "0");

            //Some providers disable Rtcp for one reason or another, it is strongly not recommended
            bool rtcpDisabled = false;

            //If there are two lines which match the criteria then disable Rtcp
            //Rtcp is disabled, RtcpEnabled is the logic inverse of this (!rtcpDisabled)
            rtcpDisabled = rtcpLines != null && rtcpLines.Count() == 2;

            //Feedback check

            //Xr Check

            //Ssrc could be generated here for the transportChannel created for this setup to be more like everyone else...
            uint ssrc = (uint)(DateTime.UtcNow.Ticks ^ session.m_RtspSocket.Handle.ToInt64());

            //We need to make an transportChannel
            RtpClient.TransportContext currentContext = null;

            //Determine if the client reqeuested Udp or Tcp or we are forcing Tcp for the found stream
            if (clientPorts != null && clientPorts.Length > 1 && found.m_ForceTCP == false)
            {

                int rtpPort = int.Parse(clientPorts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture), rtcpPort = int.Parse(clientPorts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);

                //The client requests Udp
                if(session.m_RtpClient == null)
                {
                    //Create a sender
                    session.m_RtpClient = RtpClient.Sender(((IPEndPoint)session.m_RtspSocket.LocalEndPoint).Address);

                    //Starts worker thread... 
                    session.m_RtpClient.Connect();
                }

                //Find an open port to send on (might want to reserve this port with a socket)
                int openPort = Utility.FindOpenPort(ProtocolType.Udp, MinimumUdpPort ?? 10000, true);

                if (openPort == -1) throw new RtspServerException("Could not find open Udp Port");
                else if (MaximumUdpPort.HasValue && openPort > MaximumUdpPort)
                {
                    //Handle port out of range
                }                

                //Add the transportChannel
                if (session.m_RtpClient.TransportContexts.Count == 0)
                {
                    //Use default data and control channel
                    currentContext = new RtpClient.TransportContext(0, 1, ssrc, mediaDescription, !rtcpDisabled);
                }                    
                else
                {
                    //Have to calculate next data and control channel
                    RtpClient.TransportContext lastContext = session.m_RtpClient.TransportContexts.Last();
                    currentContext = new RtpClient.TransportContext((byte)(lastContext.DataChannel + 2), (byte)(lastContext.ControlChannel + 2), ssrc, mediaDescription, !rtcpDisabled);
                }
                
                //Initialize the Udp sockets
                currentContext.InitializeSockets(((IPEndPoint)session.m_RtspSocket.LocalEndPoint).Address, ((IPEndPoint)session.m_RtspSocket.RemoteEndPoint).Address, openPort, openPort + 1, rtpPort, rtcpPort);                

                //Add the transportChannel
                session.m_RtpClient.AddTransportContext(currentContext);

                //Create the return Trasnport header
                returnTransportHeader = "RTP/AVP/UDP;unicast;client_port=" + clientPortDirective + ";server_port=" + currentContext.ClientRtpPort + "-" + currentContext.ClientRtcpPort + ";source=" + ((IPEndPoint)session.m_RtspSocket.LocalEndPoint).Address + ";ssrc=" + currentContext.LocalSynchronizationSourceIdentifier.ToString("X"); 
                
            }
            else if (clientPorts != null && clientPorts.Length > 1 && found.m_ForceTCP)//Requested Udp and Tcp was forced
            {
                //Let them know only Tcp is supported
                ProcessInvalidRtspRequest(session, RtspStatusCode.UnsupportedTransport);
                return;
            }
            else /// Rtsp / Tcp (Interleaved)
            {

                byte rtpChannel = 0, rtcpChannel = 1;

                try
                {
                    //get the requested channels
                    rtpChannel = (byte)int.Parse(channels[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    rtcpChannel = (byte)int.Parse(channels[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                }
                catch
                {
                    //invalid channel
                    ProcessInvalidRtspRequest(session, RtspStatusCode.BadRequest);
                    return;
                }

                //The client requests Tcp
                if (session.m_RtpClient == null)
                {
                    //Create a new RtpClient
                    session.m_RtpClient = RtpClient.Interleaved(session.m_RtspSocket);

                    //Create a new Interleave
                    currentContext = new RtpClient.TransportContext(rtpChannel, rtcpChannel, ssrc, mediaDescription, session.m_RtspSocket, !rtcpDisabled);

                    //Add the transportChannel the client requested
                    session.m_RtpClient.AddTransportContext(currentContext);

                    //Initialize the Interleaved Socket
                    currentContext.InitializeSockets(session.m_RtspSocket);
                }
                else if (session.m_RtpClient != null && session.m_RtpClient.m_TransportProtocol != ProtocolType.Tcp)//switching From Udp to Tcp
                {
                    //Has Udp source from before switch must clear
                    session.SourceContexts.Clear();

                    //Re-add the source
                    session.SourceContexts.Add(sourceTransportChannel);

                    //Switch the client to Tcp manually
                    session.m_RtpClient.m_SocketOwner = false;
                    session.m_RtpClient.m_TransportProtocol = ProtocolType.Tcp;

                    //Clear the existing transportChannels
                    session.m_RtpClient.TransportContexts.Clear();

                    //Get rid of existing packets
                    lock (session.m_RtpClient.m_OutgoingRtpPackets) session.m_RtpClient.m_OutgoingRtpPackets.Clear();
                    lock (session.m_RtpClient.m_OutgoingRtcpPackets) session.m_RtpClient.m_OutgoingRtcpPackets.Clear();    

                    //Add the transportChannel the client requested
                    currentContext = new RtpClient.TransportContext(rtpChannel, rtcpChannel, 0, mediaDescription, session.m_RtspSocket, !rtcpDisabled);

                    //Add the transportChannel the client requested
                    session.m_RtpClient.AddTransportContext(currentContext);

                    //Initialize the Interleaved Socket
                    currentContext.InitializeSockets(session.m_RtspSocket);
                }
                else //Is Tcp not Switching
                {
                    //Add the transportChannel
                    if (session.m_RtpClient.TransportContexts.Count == 0)
                    {
                        //Use default data and control channel
                        currentContext = new RtpClient.TransportContext(0, 1, ssrc, mediaDescription, !rtcpDisabled);
                    }
                    else
                    {
                        //Have to calculate next data and control channel
                        RtpClient.TransportContext lastContext = session.m_RtpClient.TransportContexts.Last();
                        currentContext = new RtpClient.TransportContext((byte)(lastContext.DataChannel + 2), (byte)(lastContext.ControlChannel + 2), ssrc, mediaDescription);
                    }
                    
                    //Initialize the current TransportChannel with the interleaved Socket
                    currentContext.InitializeSockets(session.m_RtspSocket);
                }

                returnTransportHeader = "RTP/AVP/TCP;unicast;interleaved=" + currentContext.DataChannel + '-' + currentContext.ControlChannel +";ssrc=" + currentContext.LocalSynchronizationSourceIdentifier.ToString("X");
            }

            //Update the values
            currentContext.NtpTimestamp = sourceTransportChannel.NtpTimestamp;
            currentContext.RtpTimestamp = sourceTransportChannel.RtpTimestamp;

            //Create the response
            RtspResponse resp = session.CreateRtspResponse(request);
            resp.AppendOrSetHeader(RtspHeaders.Session, "timeout=" + ClientRtspInactivityTimeoutSeconds);
            resp.SetHeader(RtspHeaders.Transport, returnTransportHeader);

            //Send the response
            ProcessSendRtspResponse(resp, session);

            //Identifies the transportChannel with a senders report
            //session.SendSendersReport(currentInterleave);

#if DEBUG
            System.Diagnostics.Debug.WriteLine(resp.GetHeader(RtspHeaders.Session));
            System.Diagnostics.Debug.WriteLine(resp.GetHeader(RtspHeaders.Transport));
#endif
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="request"></param>
        /// <param name="session"></param>
        internal void ProcessRtspPlay(RtspRequest request, ClientSession session)
        {

#if DEBUG
            System.Diagnostics.Debug.WriteLine("PLAY " + request.Location);
#endif

            RtpSource found = FindStreamByLocation(request.Location);

            if (found == null)
            {
                ProcessLocationNotFoundRtspRequest(session);
                return;
            }

            if (!AuthenticateRequest(request, found))
            {
                ProcessAuthorizationRequired(found, session);
                return;
            }                
            else if (!found.Ready)
            {
                //Stream is not yet ready
                ProcessInvalidRtspRequest(session, RtspStatusCode.PreconditionFailed);
                return;
            }
        
            //Get the Range header
            string rangeString = request[RtspHeaders.Range];
            TimeSpan? startRange = null, endRange = null;

            //If that is not present we cannot determine where the client wants to start playing from
            if (string.IsNullOrWhiteSpace(rangeString))
            {
                if (RequireRangeHeader)
                {
                    ProcessInvalidRtspRequest(session);
                    return;
                }
            }
            else
            {
                //Parse Range Header
                string[] times = rangeString.Trim().Split('=');
                if (times.Length > 1)
                {
                    //Determine Format
                    if (times[0] == "npt")//ntp=1.060-20
                    {
                        times = times[1].Split(RtspClient.TimeSplit, StringSplitOptions.RemoveEmptyEntries);
                        if (times[0].ToLowerInvariant() == "now") { }
                        else if (times.Length == 1)
                        {
                            if (times[0].Contains(':'))
                            {
                                startRange = TimeSpan.Parse(times[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                            }
                            else
                            {
                                startRange = TimeSpan.FromSeconds(double.Parse(times[0].Trim(), System.Globalization.CultureInfo.InvariantCulture));
                            }
                        }
                        else if (times.Length == 2)
                        {
                            //Both might not be in the same format? Check spec
                            if (times[0].Contains(':'))
                            {
                                startRange = TimeSpan.Parse(times[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                                endRange = TimeSpan.Parse(times[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                            }
                            else
                            {
                                startRange = TimeSpan.FromSeconds(double.Parse(times[0].Trim(), System.Globalization.CultureInfo.InvariantCulture));
                                endRange = TimeSpan.FromSeconds(double.Parse(times[1].Trim(), System.Globalization.CultureInfo.InvariantCulture));
                            }
                        }
                        else ProcessInvalidRtspRequest(session);
                    }
                    else if (times[0] == "smpte")//smpte=0:10:20-;time=19970123T153600Z
                    {
                        //Get the times into the times array skipping the time from the server (order may be first so I explicitly did not use Substring overload with count)
                        times = times[1].Split(RtspClient.TimeSplit, StringSplitOptions.RemoveEmptyEntries).Where(s => !s.StartsWith("time=")).ToArray();
                        if (times[0].ToLowerInvariant() == "now") { }
                        else if (times.Length == 1)
                        {
                            startRange = TimeSpan.Parse(times[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                        }
                        else if (times.Length == 2)
                        {
                            startRange = TimeSpan.Parse(times[0].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                            endRange = TimeSpan.Parse(times[1].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                        }
                        else ProcessInvalidRtspRequest(session);
                    }
                    else if (times[0] == "clock")//clock=19961108T142300Z-19961108T143520Z
                    {
                        //Get the times into times array
                        times = times[1].Split(RtspClient.TimeSplit, StringSplitOptions.RemoveEmptyEntries);
                        //Check for live
                        if (times[0].ToLowerInvariant() == "now") { }
                        //Check for start time only
                        else if (times.Length == 1)
                        {
                            DateTime now = DateTime.UtcNow, startDate;
                            ///Parse and determine the start time
                            if (DateTime.TryParse(times[0].Trim(), out startDate))
                            {
                                //Time in the past
                                if (now > startDate) startRange = now - startDate;
                                //Future?
                                else startRange = startDate - now;
                            }
                        }
                        else if (times.Length == 2)
                        {
                            DateTime now = DateTime.UtcNow, startDate, endDate;
                            ///Parse and determine the start time
                            if (DateTime.TryParse(times[0].Trim(), out startDate))
                            {
                                //Time in the past
                                if (now > startDate) startRange = now - startDate;
                                //Future?
                                else startRange = startDate - now;
                            }

                            ///Parse and determine the end time
                            if (DateTime.TryParse(times[1].Trim(), out endDate))
                            {
                                //Time in the past
                                if (now > endDate) endRange = now - endDate;
                                //Future?
                                else endRange = startDate - now;
                            }
                        }
                        else ProcessInvalidRtspRequest(session);
                    }
                }
            }

            //Todo 
            //Validate Range and store in ClientSession and start playing from range

            //Create a response
            RtspResponse response = session.CreateRtspResponse(request);

            //Add the range header
            response.SetHeader(RtspHeaders.Range, RtspHeaders.RangeHeader(startRange, endRange));
           
            //Create the Rtp-Info RtpHeader as required by RFC2326
            session.SourceContexts.ForEach( c=> {
                string actualTrack = string.Empty;

                Sdp.SessionDescriptionLine attributeLine = c.MediaDescription.Lines.Where(l => l.Type == 'a' && l.Parts.Any(p => p.Contains("control"))).First();
                if (attributeLine != null)
                    actualTrack = '/' + attributeLine.Parts.Where(p => p.Contains("control")).FirstOrDefault().Replace("control:", string.Empty);

                response.AppendOrSetHeader(RtspHeaders.RtpInfo, "url=rtsp://" + ((IPEndPoint)(session.m_RtspSocket.LocalEndPoint)).Address + "/live/" + found.Id + actualTrack + ";seq=" + c.SequenceNumber + ";rtptime=" + c.RtpTimestamp);
            });

            //Send the response
            ProcessSendRtspResponse(response, session);

            //Send a SendersReport to identify the media being sent before anything else
            session.m_RtpClient.SendSendersReports();

            //Attach the client to the source, Here they may only want one track so there is no need to attach events for all
            //Todo - Provide overload for MediaDescription
            session.Attach(found);
#if DEBUG
            System.Diagnostics.Debug.WriteLine(response.GetHeader(RtspHeaders.Session));
            System.Diagnostics.Debug.WriteLine(response.GetHeader(RtspHeaders.RtpInfo));
            System.Diagnostics.Debug.WriteLine(response.GetHeader(RtspHeaders.Range));
#endif
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="request"></param>
        /// <param name="session"></param>
        internal void ProcessRtspPause(RtspRequest request, ClientSession session)
        {

#if DEBUG
            System.Diagnostics.Debug.WriteLine("PAUSE " + request.Location);
#endif

            RtpSource found = FindStreamByLocation(request.Location);
            if (found == null)
            {
                ProcessLocationNotFoundRtspRequest(session);
                return;
            }

            if (!AuthenticateRequest(request, found))
            {
                ProcessAuthorizationRequired(found, session);
                return;
            }

            //Should just signal so packets are not lost per RFC e.g. packets should remain in buffer and begin where next play time says
            //Right now we just stop sending which is also valid enough to work for now (most players handle this differently anyway)
            session.Detach(found);

            //Might need to add some headers
            ProcessSendRtspResponse(session.CreateRtspResponse(request), session);
        }

        /// <summary>
        /// Ends the client session
        /// </summary>
        /// <param name="request">The Teardown request</param>
        /// <param name="session">The session which recieved the request</param>
        internal void ProcessRtspTeardown(RtspRequest request, ClientSession session)
        {

#if DEBUG
            System.Diagnostics.Debug.WriteLine("TEARDOWN " + request.Location);
#endif
            try
            {
                RtpSource found = FindStreamByLocation(request.Location);

                if (found == null)
                {
                    ProcessLocationNotFoundRtspRequest(session);
                    return;
                }

                if (!AuthenticateRequest(request, found))
                {
                    ProcessAuthorizationRequired(found, session);
                    return;
                }

                //Only a single track
                if (request.Location.ToString().Contains("track"))
                {

                    //Determine if we have the track
                    string track = request.Location.Segments.Last();

                    Sdp.MediaDescription mediaDescription = null;

                    RtpClient.TransportContext sourceContext = null;

                    session.SourceContexts.ForEach(c =>
                    {
                        if (mediaDescription != null || sourceContext != null) return;
                        Sdp.SessionDescriptionLine attributeLine = c.MediaDescription.Lines.Where(l => l.Type == 'a' && l.Parts.Any(p => p.Contains("control"))).FirstOrDefault();
                        if (attributeLine != null)
                        {
                            string actualTrack = attributeLine.Parts.Where(p => p.Contains("control")).FirstOrDefault().Replace("control:", string.Empty);
                            if (actualTrack == track)
                            {
                                mediaDescription = c.MediaDescription;
                                sourceContext = c;
                                return;
                            }
                        }
                    });

                    //Cannot teardown media because we can't find the track they are asking to tear down
                    if (mediaDescription == null || !session.SourceContexts.Contains(sourceContext))
                    {
                        ProcessLocationNotFoundRtspRequest(session);
                        return;
                    }

                    //Remove related transportChannels from found Client in session
                    session.SourceContexts.Remove(sourceContext);

                    //Todo
                    //session.Detach(mediaDescription);

                }
                else //Tear down all streams
                {
                    if (request.Location.ToString().ToLowerInvariant().Contains("archive"))
                    {
                        //Disconnect for archive
                    }
                    else
                    {
                        session.Detach(found);
                        session.m_RtpClient.Disconnect();
                    }

                    //Remove related transportChannels from found Client in session
                    found.RtpClient.TransportContexts.ForEach(c => session.SourceContexts.Remove(c));
                }

                //Send the response
                ProcessSendRtspResponse(session.CreateRtspResponse(request), session);

                //Clients session will timeout eventually, don't remove it now incase they setup a new stream or have other streams playing
            }
            catch
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine("Exception in Teardown");
#endif
            }            
        }

        /// <summary>
        /// Handles the GET_PARAMETER RtspRequest
        /// </summary>
        /// <param name="request">The GET_PARAMETER RtspRequest to handle</param>
        /// <param name="ci">The RtspSession from which the request was receieved</param>
        internal void ProcessGetParameter(RtspRequest request, ClientSession session)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine("GET_PARAMETER " + request.Location);
#endif

            //We should process the body and return the parameters
            ProcessSendRtspResponse(session.CreateRtspResponse(request), session);
        }

        /// <summary>
        /// Handles the SET_PARAMETER RtspRequest
        /// </summary>
        /// <param name="request">The GET_PARAMETER RtspRequest to handle</param>
        /// <param name="ci">The RtspSession from which the request was receieved</param>
        internal void ProcessSetParameter(RtspRequest request, ClientSession session)
        {
#if DEBUG
            System.Diagnostics.Debug.WriteLine("SET_PARAMETER " + request.Location);
#endif
            //Could be used for PTZ or other stuff
            //Should have a way to determine to forward send parameters... public bool ForwardSetParameter { get; set; }
            //Should have a way to call SendSetParamter on the RtspSession.Listener
            ProcessSendRtspResponse(session.CreateRtspResponse(request), session);
        }

        /// <summary>
        /// Authenticates a RtspRequest against a RtspStream
        /// </summary>
        /// <param name="request">The RtspRequest to authenticate</param>
        /// <param name="source">The RtspStream to authenticate against</param>
        /// <returns>True if authroized, otherwise false</returns>
        internal bool AuthenticateRequest(RtspRequest request, RtpSource source)
        {
            if (request == null) throw new ArgumentNullException("request");
            if (source == null) throw new ArgumentNullException("source");

            //If the source has no authentication scheme or no credential then there is nothing to determine
            if (source.SourceAuthenticationScheme == AuthenticationSchemes.None || source.RemoteCredential == null) return true;
            
            //If the request does not have the authorization header then there is nothing else to determine
            if (!request.ContainsHeader(RtspHeaders.Authorization)) return false;

            //Get the Authroization Header
            string header = request[RtspHeaders.Authorization].ToLower();

            //Wouldn't have to have a RemoteAuthenticationScheme if we stored the Nonce and CNonce on the session... then allowed either or here based on the header

            //If the SourceAuthenticationScheme is Basic and the header contains the BASIC indication then validiate using BASIC authentication
            if (source.RemoteAuthenticationScheme == AuthenticationSchemes.Basic && header.Contains("basic"))
            {
                //Remove the parts
                header = header.Replace("basic", string.Empty).Trim();
                
                //Get the decoded value
                header = request.Encoding.GetString(Convert.FromBase64String(header));
                
                //Get the parts
                string[] parts = header.Split(':');
                
                //If enough return the determination by comparison as the result
                return parts.Length > 1 && (parts[0].Equals(source.RemoteCredential.UserName) && parts[2].Equals(source.RemoteCredential.Password));
            }
            else if (source.RemoteAuthenticationScheme == AuthenticationSchemes.Digest && header.Contains("digest"))
            {
                //http://tools.ietf.org/html/rfc2617
                //Digest RFC2617
                /* Example header -
                 * 
                 Authorization: Digest username="Mufasa",
                     realm="testrealm@host.com",
                     nonce="dcd98b7102dd2f0e8b11d0f600bfb0c093",
                     uri="/dir/index.html",
                     qop=auth,
                     nc=00000001,
                     cnonce="0a4f113b",
                     response="6629fae49393a05397450978507c4ef1",
                     opaque="5ccc069c403ebaf9f0171e9517f40e41"
                 * 
                 * 
                 * Example Convo
                 * 
                 * ANNOUNCE rtsp://216.224.181.197/bstream.sdp RTSP/1.0
    CSeq: 1
    Content-Type: application/sdp
    User-Agent: C.U.
    Authorization: Digest username="gidon", realm="null", nonce="null", uri="/bstream.sdp", response="239fcac559661c17436e427e75f3d6a0"
    Content-Length: 313

    v=0
    s=CameraStream
    m=video 5006 RTP/AVP 96
    b=RR:0
    a=rtpmap:96 H264/90000
    a=fmtp:96 packetization-mode=1;profile-level-id=42000c;sprop-parameter-sets=Z0IADJZUCg+I,aM44gA==;
    a=control:trackID=0
    m=audio 5004 RTP/AVP 96
    b=AS:128
    b=RR:0
    a=rtpmap:96 AMR/8000
    a=fmtp:96 octet-align=1;
    a=control:trackID=1


    RTSP/1.0 401 Unauthorized
    Server: DSS/6.0.3 (Build/526.3; Platform/Linux; Release/Darwin Streaming Server; State/Development; )
    Cseq: 1
    WWW-Authenticate: Digest realm="Streaming Server", nonce="e5c0b7aff71820962027d73f55fe48c8"


    ANNOUNCE rtsp://216.224.181.197/bstream.sdp RTSP/1.0
    CSeq: 2
    Content-Type: application/sdp
    User-Agent: C.U.
    Authorization: Digest username="gidon", realm="Streaming Server", nonce="e5c0b7aff71820962027d73f55fe48c8", uri="/bstream.sdp", response="6e3aa3be3f5c04a324491fe9ab341918"
    Content-Length: 313

    v=0
    s=CameraStream
    m=video 5006 RTP/AVP 96
    b=RR:0
    a=rtpmap:96 H264/90000
    a=fmtp:96 packetization-mode=1;profile-level-id=42000c;sprop-parameter-sets=Z0IADJZUCg+I,aM44gA==;
    a=control:trackID=0
    m=audio 5004 RTP/AVP 96
    b=AS:128
    b=RR:0
    a=rtpmap:96 AMR/8000
    a=fmtp:96 octet-align=1;
    a=control:trackID=1


    RTSP/1.0 200 OK
    Server: DSS/6.0.3 (Build/526.3; Platform/Linux; Release/Darwin Streaming Server; State/Development; )
    Cseq: 2
                 * 
                 * 
                 */

                string[] parts = header.Split(',');

                string username, realm, nonce, nc, cnonce, uri, qop, opaque, response;

                username = parts.Where(p => p.StartsWith("username")).FirstOrDefault();

                realm = parts.Where(p => p.StartsWith("realm")).FirstOrDefault();

                nc = parts.Where(p => p.StartsWith("nc")).FirstOrDefault();

                nonce = parts.Where(p => p.StartsWith("nonce")).FirstOrDefault();

                if (nonce == null) nonce = string.Empty;

                cnonce = parts.Where(p => p.StartsWith("cnonce")).FirstOrDefault();

                if (cnonce == null) cnonce = string.Empty;

                uri = parts.Where(p => p.StartsWith("uri")).FirstOrDefault();

                qop = parts.Where(p => p.StartsWith("qop")).FirstOrDefault();

                if (qop == null) qop = string.Empty;

                opaque = parts.Where(p => p.StartsWith("opaque")).FirstOrDefault();

                if (opaque == null) opaque = string.Empty;

                response = parts.Where(p => p.StartsWith("response")).FirstOrDefault();

                if (string.IsNullOrEmpty(username) || username != source.RemoteCredential.UserName || string.IsNullOrWhiteSpace(realm) || string.IsNullOrWhiteSpace(uri) || string.IsNullOrWhiteSpace(response)) return false;                

                //http://en.wikipedia.org/wiki/Digest_access_authentication
                //The MD5 hash of the combined username, authentication realm and password is calculated. The result is referred to as HA1.
                byte[] HA1 = Utility.MD5HashAlgorithm.ComputeHash(request.Encoding.GetBytes(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1}:{2}", source.RemoteCredential.UserName , realm.Replace("realm=", string.Empty), source.RemoteCredential.Password)));
                
                //The MD5 hash of the combined method and digest URI is calculated, e.g. of "GET" and "/dir/index.html". The result is referred to as HA2.
                byte[] HA2 = Utility.MD5HashAlgorithm.ComputeHash(request.Encoding.GetBytes(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1}", request.Method, uri.Replace("uri=", string.Empty))));

                //No QOP No NC
                //See http://en.wikipedia.org/wiki/Digest_access_authentication
                //http://tools.ietf.org/html/rfc2617

                //The MD5 hash of the combined HA1 result, server nonce (nonce), request counter (nc), client nonce (cnonce), quality of protection code (qop) and HA2 result is calculated. The result is the "response" value provided by the client.
                byte[] ResponseHash = Utility.MD5HashAlgorithm.ComputeHash(request.Encoding.GetBytes(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}:{1}:{2}:{3}:{4}:{5}", Convert.ToString(HA1).Replace("-", string.Empty), nonce.Replace("nonce=", string.Empty), nc.Replace("nc=", string.Empty), cnonce.Replace("cnonce=", string.Empty), qop.Replace("qop=", string.Empty), Convert.ToString(HA2).Replace("-", string.Empty))));

                //return the result of a mutal hash creation via comparison
                return ResponseHash.SequenceEqual(Utility.HexStringToBytes(response.Replace("response=", string.Empty)));
                //return Comparer<byte[]>.Default.Compare(ResponseHash, Utility.HexStringToBytes(response.Replace("response=", string.Empty))) == 0;
            }
            //else if (source.RemoteAuthenticationScheme == AuthenticationSchemes.IntegratedWindowsAuthentication && (header.Contains("ntlm") || header.Contains("integrated")))
            //{
            //    //Check windows creds
            //    throw new NotImplementedException();
            //}

            //Did not authenticate
            return false;
        }

        #endregion

        #endregion        
    }
}
