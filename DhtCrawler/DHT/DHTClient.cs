﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using DhtCrawler.Common.RateLimit;
using DhtCrawler.Common.Utils;
using DhtCrawler.DHT.Message;
using DhtCrawler.Encode;
using DhtCrawler.Encode.Exception;
using log4net;


namespace DhtCrawler.DHT
{
    public class DhtClient : IDisposable
    {
        private static byte[] GenerateRandomNodeId()
        {
            var random = new Random();
            var ids = new byte[20];
            random.NextBytes(ids);
            return ids;
        }
        //初始节点 
        private static readonly DhtNode[] BootstrapNodes =
        {
            new DhtNode() { Host = Dns.GetHostAddresses("router.bittorrent.com")[0], Port = 6881 },
            new DhtNode() { Host = Dns.GetHostAddresses("dht.transmissionbt.com")[0], Port = 6881 },
            new DhtNode() { Host = Dns.GetHostAddresses("router.utorrent.com")[0], Port = 6881 },
            new DhtNode() { Host = IPAddress.Parse("82.221.103.244"), Port = 6881 },
            new DhtNode() { Host = IPAddress.Parse("23.21.224.150"), Port = 6881 }
        };
        /// <summary>
        /// 默认入队等待时间（超时丢弃）
        /// </summary>
        private static readonly TimeSpan EnqueueWaitTime = TimeSpan.FromSeconds(10);
        private readonly ILog _logger = LogManager.GetLogger(typeof(DhtClient));

        private readonly UdpClient _client;
        private readonly IPEndPoint _endPoint;
        private readonly DhtNode _node;
        private readonly RouteTable _kTable;

        private readonly BlockingCollection<DhtNode> _nodeQueue;
        private readonly BlockingCollection<DhtData> _recvMessageQueue;
        private readonly BlockingCollection<Tuple<DhtMessage, DhtNode>> _sendMessageQueue;
        private readonly BlockingCollection<Tuple<DhtMessage, DhtNode>> _responseMessageQueue;//
        private readonly CancellationTokenSource _cancellationTokenSource;

        private readonly IList<Task> _tasks;
        private readonly IRateLimit _sendRateLimit;
        private readonly IRateLimit _receveRateLimit;
        private readonly int _processThreadNum;
        private volatile bool running = false;
        /// <summary>
        /// 消息处理时等待次数
        /// </summary>
        private readonly int waitSize;
        /// <summary>
        /// 等待时间（毫秒）
        /// </summary>
        private readonly int waitTime;
        private byte[] GetNeighborNodeId(byte[] targetId)
        {
            var selfId = _node.NodeId;
            if (targetId == null)
                targetId = _node.NodeId;
            return targetId.Take(10).Concat(selfId.Skip(10)).ToArray();
        }

        protected virtual AbstractMessageMap MessageMap => DefaultMessageMap.Instance;

        #region 事件

        public event Func<InfoHash, Task> OnFindPeer;

        public event Func<InfoHash, Task> OnAnnouncePeer;

        public event Func<InfoHash, Task> OnReceiveInfoHash;

        #endregion

        public int ReceviceMessageCount => _recvMessageQueue.Count;
        public int SendMessageCount => _sendMessageQueue.Count;
        public int ResponseMessageCount => _responseMessageQueue.Count;
        public int FindNodeCount => _nodeQueue.Count;

        public DhtClient() : this(new DhtConfig())
        {

        }

        public DhtClient(ushort port = 0, int nodeQueueSize = 1024 * 20, int receiveQueueSize = 1024 * 20, int sendQueueSize = 1024 * 20, int sendRate = 100, int receiveRate = 100, int threadNum = 1) : this(new DhtConfig() { Port = port, NodeQueueMaxSize = nodeQueueSize, ReceiveQueueMaxSize = receiveQueueSize, SendQueueMaxSize = sendQueueSize, SendRateLimit = sendRate, ReceiveRateLimit = receiveRate, ProcessThreadNum = threadNum })
        {

        }

        public DhtClient(DhtConfig config)
        {
            _endPoint = new IPEndPoint(IPAddress.Any, config.Port);
            _client = new UdpClient(_endPoint) { Ttl = byte.MaxValue };
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                case PlatformID.Win32S:
                case PlatformID.Win32Windows:
                case PlatformID.WinCE:
                    _client.Client.IOControl(-1744830452, new byte[] { 0, 0, 0, 0 }, null);
                    break;
            }

            _node = new DhtNode() { Host = IPAddress.Any, Port = config.Port, NodeId = GenerateRandomNodeId() };
            _kTable = new RouteTable(config.KTableSize);

            _nodeQueue = new BlockingCollection<DhtNode>(config.NodeQueueMaxSize);
            _recvMessageQueue = new BlockingCollection<DhtData>(config.ReceiveQueueMaxSize);
            _sendMessageQueue = new BlockingCollection<Tuple<DhtMessage, DhtNode>>(config.SendQueueMaxSize);
            _responseMessageQueue = new BlockingCollection<Tuple<DhtMessage, DhtNode>>();

            _sendRateLimit = new TokenBucketLimit(config.SendRateLimit * 1024, 1, TimeUnit.Second);
            _receveRateLimit = new TokenBucketLimit(config.ReceiveRateLimit * 1024, 1, TimeUnit.Second);
            _processThreadNum = config.ProcessThreadNum;
            _cancellationTokenSource = new CancellationTokenSource();

            waitSize = config.ProcessWaitSize;
            waitTime = config.ProcessWaitTime;
            _tasks = new List<Task>();
        }

        #region 处理收到消息

        private void Recevie_Data(IAsyncResult asyncResult)
        {
            var client = (UdpClient)asyncResult.AsyncState;
            try
            {
                var remotePoint = _endPoint;
                var data = client.EndReceive(asyncResult, ref remotePoint);
                while (!_receveRateLimit.Require(data.Length, out var waitTime))
                {
                    Thread.Sleep(waitTime);
                }
                _recvMessageQueue.TryAdd(new DhtData() { Data = data, RemoteEndPoint = remotePoint }, EnqueueWaitTime);
                if (!running)
                    return;
            }
            catch (Exception ex)
            {
                _logger.Error(ex);
            }
            while (true)
            {
                try
                {
                    client.BeginReceive(Recevie_Data, client);
                    break;
                }
                catch (SocketException ex)
                {
                    _logger.Error("begin receive error", ex);
                }
            }
        }


        private async Task ProcessRequestAsync(DhtMessage msg, IPEndPoint remotePoint)
        {
            var response = new DhtMessage
            {
                MessageId = msg.MessageId,
                MesageType = MessageType.Response
            };
            var requestNode = new DhtNode() { NodeId = (byte[])msg.Data["id"], Host = remotePoint.Address, Port = (ushort)remotePoint.Port };
            _kTable.AddOrUpdateNode(requestNode);
            response.Data.Add("id", GetNeighborNodeId(requestNode.NodeId));
            switch (msg.CommandType)
            {
                case CommandType.Find_Node:
                    var targetNodeId = (byte[])msg.Data["target"];
                    response.Data.Add("nodes", _kTable.FindNodes(targetNodeId).SelectMany(n => n.CompactNode()).ToArray());
                    break;
                case CommandType.Get_Peers:
                case CommandType.Announce_Peer:
                    var infoHash = new InfoHash((byte[])msg.Data["info_hash"]);
                    if (OnReceiveInfoHash != null)
                    {
                        await OnReceiveInfoHash(infoHash);
                    }
                    if (msg.CommandType == CommandType.Get_Peers)
                    {
                        var nodes = _kTable.FindNodes(infoHash.Bytes);
                        response.Data.Add("nodes", nodes.SelectMany(n => n.CompactNode()).ToArray());
                        response.Data.Add("token", infoHash.Value.Substring(0, 2));
                        if (!infoHash.IsDown)
                        {
                            foreach (var node in nodes)
                            {
                                GetPeers(node, infoHash.Bytes);
                            }
                        }
                    }
                    else if (!infoHash.IsDown)
                    {
                        if (!msg.Data.Keys.Contains("implied_port") || 0.Equals(msg.Data["implied_port"]))//implied_port !=0 则端口使用port  
                        {
                            remotePoint.Port = Convert.ToInt32(msg.Data["port"]);
                        }
                        infoHash.Peers = new HashSet<IPEndPoint>(1) { remotePoint };
                        if (OnAnnouncePeer != null)
                        {
                            await OnAnnouncePeer(infoHash);
                        }
                    }
                    break;
                case CommandType.Ping:
                    break;
                default:
                    return;
            }
            _responseMessageQueue.TryAdd(new Tuple<DhtMessage, DhtNode>(response, new DhtNode(remotePoint)));
        }

        private async Task ProcessResponseAsync(DhtMessage msg, IPEndPoint remotePoint)
        {
            if (msg.MessageId.Length != 2)
                return;
            var responseNode = new DhtNode() { NodeId = (byte[])msg.Data["id"], Host = remotePoint.Address, Port = (ushort)remotePoint.Port };
            if (!MessageMap.RequireRegisteredInfo(msg, responseNode))
            {
                return;
            }
            _kTable.AddOrUpdateNode(responseNode);
            object nodeInfo;
            ISet<DhtNode> nodes = null;
            switch (msg.CommandType)
            {
                case CommandType.Find_Node:
                    if (!msg.Data.TryGetValue("nodes", out nodeInfo))
                        break;
                    nodes = DhtNode.ParseNode((byte[])nodeInfo);
                    break;
                case CommandType.Get_Peers:
                    var hashByte = msg.Get<byte[]>("info_hash");
                    var infoHash = new InfoHash(hashByte);
                    if (msg.Data.TryGetValue("values", out nodeInfo))
                    {
                        var peerInfo = (IList<object>)nodeInfo;
                        var peers = new HashSet<IPEndPoint>(peerInfo.Count);
                        foreach (var t in peerInfo)
                        {
                            var peer = (byte[])t;
                            peers.Add(DhtNode.ParsePeer(peer, 0));
                        }
                        if (peers.Count > 0)
                        {
                            infoHash.Peers = peers;
                            if (OnFindPeer != null)
                            {
                                await OnFindPeer(infoHash);
                            }
                            return;
                        }
                    }
                    if (msg.Data.TryGetValue("nodes", out nodeInfo))
                    {
                        if (!(nodeInfo is byte[]))
                            return;
                        nodes = DhtNode.ParseNode((byte[])nodeInfo);
                        foreach (var node in nodes)
                        {
                            _kTable.AddNode(node);
                            GetPeers(node, infoHash.Bytes);
                        }
                    }
                    break;
            }
            if (nodes != null)
            {
                foreach (var node in nodes)
                {
                    _nodeQueue.TryAdd(node);
                    _kTable.AddNode(node);
                }
            }
        }

        private async Task ProcessMsgData()
        {
            var size = 0;
            var canWait = waitTime > 0 && waitSize > 0;
            while (running)
            {
                if (!_recvMessageQueue.TryTake(out DhtData dhtData))
                {
                    continue;
                }
                if (canWait && size > waitSize)
                {
                    await Task.Delay(waitTime);
                    size = 0;
                }
                try
                {
                    var dic = (Dictionary<string, object>)BEncoder.Decode(dhtData.Data);
                    var msg = new DhtMessage(dic);
                    switch (msg.MesageType)
                    {
                        case MessageType.Request:
                            await ProcessRequestAsync(msg, dhtData.RemoteEndPoint);
                            break;
                        case MessageType.Response:
                            await ProcessResponseAsync(msg, dhtData.RemoteEndPoint);
                            break;
                    }
                    Interlocked.Increment(ref size);
                }
                catch (Exception ex)
                {
                    _logger.Error($"ErrorData:{BitConverter.ToString(dhtData.Data)}", ex);
                    var response = new DhtMessage
                    {
                        MesageType = MessageType.Exception,
                        MessageId = new byte[] { 0, 0 }
                    };
                    if (ex is DecodeException)
                    {
                        response.Errors.Add(203);
                        response.Errors.Add("Error Protocol");
                    }
                    else
                    {
                        response.Errors.Add(202);
                        response.Errors.Add("Server Error:" + ex.Message);
                    }
                    _sendMessageQueue.TryAdd(new Tuple<DhtMessage, DhtNode>(response, new DhtNode(dhtData.RemoteEndPoint)));
                }
            }
        }

        #endregion

        #region 发送请求

        private void SendMsg(CommandType command, IDictionary<string, object> data, DhtNode node)
        {
            var msg = new DhtMessage
            {
                CommandType = command,
                MesageType = MessageType.Request,
                Data = new SortedDictionary<string, object>(data)
            };
            if (!MessageMap.RegisterMessage(msg, node))
            {
                return;
            }
            msg.Data.Add("id", GetNeighborNodeId(node.NodeId));
            var dhtItem = new Tuple<DhtMessage, DhtNode>(msg, node);
            if (msg.CommandType == CommandType.Get_Peers)
            {
                _sendMessageQueue.TryAdd(dhtItem, EnqueueWaitTime);
            }
            else
            {
                _sendMessageQueue.TryAdd(dhtItem);
            }
        }

        private async Task LoopSendMsg()
        {
            while (running)
            {
                var queue = _responseMessageQueue.Count <= 0 ? _sendMessageQueue : _responseMessageQueue;
                if (!queue.TryTake(out var dhtData))
                {
                    await Task.Delay(1000);
                    continue;
                }
                try
                {
                    var msg = dhtData.Item1;
                    var node = dhtData.Item2;
                    if (queue == _sendMessageQueue)
                    {
                        if (!MessageMap.RegisterMessage(msg, node))
                        {
                            continue;
                        }
                    }
                    var sendBytes = msg.BEncodeBytes();
                    var remotepoint = new IPEndPoint(node.Host, node.Port);
                    while (!_sendRateLimit.Require(sendBytes.Length, out var waitTime))
                    {
                        await Task.Delay(waitTime);
                    }
                    await _client.SendAsync(sendBytes, sendBytes.Length, remotepoint);
                }
                catch (SocketException)
                {
                }
                catch (Exception ex)
                {
                    _logger.Error(ex);
                }
            }
        }

        private async Task LoopFindNodes()
        {
            int limitNode = 1024 * 10;
            var nodeSet = new HashSet<DhtNode>();
            while (running)
            {
                if (_nodeQueue.Count <= 0)
                {
                    foreach (var dhtNode in _kTable)
                    {
                        if (!running)
                            return;
                        if (!_nodeQueue.TryAdd(dhtNode))
                            break;
                    }
                }
                while (running && _nodeQueue.TryTake(out var node) && nodeSet.Count <= limitNode)
                {
                    nodeSet.Add(node);
                }
                using (var nodeEnumerator = BootstrapNodes.Union(nodeSet).GetEnumerator())
                {
                    while (running && nodeEnumerator.MoveNext())
                    {
                        FindNode(nodeEnumerator.Current);
                    }
                }
                nodeSet.Clear();
                if (!running)
                    return;
                if (nodeSet.Count < 10 || (SendMessageCount > 0 && ReceviceMessageCount > 0))
                    await Task.Delay(60 * 1000, _cancellationTokenSource.Token);
            }
        }

        #endregion

        #region dht协议命令
        public void FindNode(DhtNode node)
        {
            var data = new Dictionary<string, object> { { "target", GenerateRandomNodeId() } };
            SendMsg(CommandType.Find_Node, data, node);
        }

        public void Ping(DhtNode node)
        {
            SendMsg(CommandType.Ping, null, node);
        }

        public void GetPeers(DhtNode node, byte[] infoHash)
        {
            var data = new Dictionary<string, object> { { "info_hash", infoHash } };
            SendMsg(CommandType.Get_Peers, data, node);
        }

        public void GetPeers(byte[] infoHash)
        {
            var nodes = _kTable.FindNodes(infoHash);
            if (nodes.IsEmpty() || nodes.Count < 8)
            {
                foreach (var node in BootstrapNodes)
                {
                    nodes.Add(node);
                }
            }
            foreach (var node in nodes)
            {
                GetPeers(node, infoHash);
            }
        }

        public void AnnouncePeer(DhtNode node, byte[] infoHash, ushort port, string token)
        {
            var data = new Dictionary<string, object> { { "info_hash", infoHash }, { "port", port }, { "token", token } };
            SendMsg(CommandType.Announce_Peer, data, node);
        }
        #endregion

        public void Run()
        {
            running = true;
            _client.BeginReceive(Recevie_Data, _client);
            for (int i = 0; i < _processThreadNum; i++)
            {
                var local = i;
                Task.Run(() =>
                {
                    _tasks.Add(ProcessMsgData().ContinueWith(t => { _logger.InfoFormat("ProcessMsg {0} Over", local); }));
                });
            }
            Task.Run(() =>
            {
                _tasks.Add(LoopFindNodes().ContinueWith(t =>
                {
                    _logger.Info("Loop FindNode Over");
                }));
            });
            Task.Run(() =>
            {
                _tasks.Add(LoopSendMsg().ContinueWith(t =>
                {
                    _logger.Info("Loop SendMeg Over");
                }));
            });
            _logger.Info("starting");
        }

        public void ShutDown()
        {
            _logger.Info("shuting down");
            running = false;
            _cancellationTokenSource.Cancel(true);
            ClearCollection(_nodeQueue);
            ClearCollection(_recvMessageQueue);
            ClearCollection(_sendMessageQueue);
            ClearCollection(_responseMessageQueue);
            Task.WaitAll(_tasks.ToArray());
            _logger.Info("close success");
        }

        private static void ClearCollection<T>(BlockingCollection<T> collection)
        {
            while (collection.Count > 0)
            {
                collection.TryTake(out T remove);
            }
        }

        public void Dispose()
        {
            _client?.Dispose();
            _nodeQueue?.Dispose();
            _recvMessageQueue?.Dispose();
            _sendMessageQueue?.Dispose();
            _responseMessageQueue?.Dispose();
        }
    }
}
