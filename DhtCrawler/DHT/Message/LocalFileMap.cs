﻿using System.IO;
using DhtCrawler.Common.Utils;

namespace DhtCrawler.DHT.Message
{
    public class LocalFileMap : AbstractMessageMap
    {
        public static readonly LocalFileMap Default = new LocalFileMap("msgs");
        private string _storePath;
        private int _index;
        private object[] locks;
        public LocalFileMap(string storePath)
        {
            _storePath = storePath;
            locks = new object[10];
            for (var i = 0; i < locks.Length; i++)
            {
                locks[i] = new object();
            }
            Directory.CreateDirectory(storePath);
        }

        protected override bool RegisterGetPeersMessage(byte[] infoHash, DhtNode node, out TransactionId msgId)
        {
            var nodeId = (ulong)node.CompactEndPoint().ToInt64();
            var tryTimes = 0;
            msgId = null;
            while (tryTimes < 3)
            {
                tryTimes++;
                lock (this)
                {
                    _index++;
                    if (_index >= _bucketArray.Length)
                        _index = 0;
                }
                msgId = _bucketArray[_index];
                var path = nodeId << 16 | (uint)(msgId[0] << 8) | msgId[1];
                var filePath = Path.Combine(_storePath, path + ".bin");
                var syncObj = locks[path % (ulong)locks.Length];
                lock (syncObj)
                {
                    if (File.Exists(filePath))
                    {
                        continue;
                    }
                    File.WriteAllBytes(filePath, infoHash);
                    return true;
                }
            }
            return false;
        }

        protected override bool RequireGetPeersRegisteredInfo(TransactionId msgId, DhtNode node, out byte[] infoHash)
        {
            var nodeId = (ulong)node.CompactEndPoint().ToInt64();
            var path = nodeId << 16 | (uint)(msgId[0] << 8) | msgId[1];
            var filePath = Path.Combine(_storePath, path + ".bin");
            var syncObj = locks[path % (ulong)locks.Length];
            lock (syncObj)
            {
                if (File.Exists(filePath))
                {
                    infoHash = File.ReadAllBytes(filePath);
                    File.Delete(filePath);
                    return true;
                }
                infoHash = null;
                return false;
            }
        }
    }
}