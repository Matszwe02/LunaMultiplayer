using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Server.Client;
using Server.Log;
using Server.System;

namespace LmpKerbalismPlugin
{
    /// <summary>
    /// Opcodes of the Kerbalism mod payload protocol (see KerbalismPlugin.ProtocolVersion).
    /// Client -> server: Ping, Notify. Server -> client: Pong.
    /// </summary>
    internal enum KerbalismOpcode : byte
    {
        Ping = 0,
        Pong = 1,
        Notify = 2
    }

    /// <summary>
    /// Handles the "Kerbalism" mod payloads relayed by the server. Runs on the Lidgren
    /// receive thread, therefore all shared state is thread-safe.
    /// </summary>
    internal class KerbalismModHandler
    {
        private const int MinEnvelopeSize = 2;

        /// <summary>Warn about malformed payloads at most once per client to avoid log floods.</summary>
        private readonly ConcurrentDictionary<string, byte> _warnedClients = new ConcurrentDictionary<string, byte>();

        private readonly KerbalismClientRegistry _registry;

        public KerbalismModHandler(KerbalismClientRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>
        /// Callback registered in LmpModInterface for the "Kerbalism" mod name.
        /// Payloads flagged Relay=true are already forwarded to the other clients by
        /// ModDataMsgReader before this runs, so this method only validates, tracks
        /// and answers point-to-point.
        /// </summary>
        public void HandleModMessage(ClientStructure client, byte[] modData, int numBytes)
        {
            if (client == null || modData == null || numBytes < MinEnvelopeSize || modData.Length < numBytes)
            {
                WarnMalformed(client, "payload missing or too small");
                return;
            }

            var version = modData[0];
            if (version != KerbalismPlugin.ProtocolVersion)
            {
                WarnMalformed(client, $"unsupported protocol version {version}");
                return;
            }

            var opcode = (KerbalismOpcode)modData[1];
            switch (opcode)
            {
                case KerbalismOpcode.Ping:
                    _registry.MarkSeen(client);
                    //Reply point-to-point so the client mod knows the server plugin is loaded.
                    SendToClient(client, KerbalismOpcode.Pong);
                    LunaLog.Debug($"[KerbalismPlugin]: Pong -> {client.PlayerName}");
                    break;

                case KerbalismOpcode.Notify:
                    //Opaque state notification. Validated, tracked, never rebroadcast.
                    _registry.MarkSeen(client);
                    LunaLog.Debug($"[KerbalismPlugin]: Notify from {client.PlayerName} ({numBytes} bytes)");
                    break;

                default:
                    WarnMalformed(client, $"unknown opcode {(byte)opcode}");
                    break;
            }
        }

        private static void SendToClient(ClientStructure client, KerbalismOpcode opcode)
        {
            var payload = new byte[] { KerbalismPlugin.ProtocolVersion, (byte)opcode };
            ModDataSystemSender.SendLmpModMessageToClient(client, KerbalismPlugin.ModName, payload);
        }

        private void WarnMalformed(ClientStructure client, string reason)
        {
            var key = client?.UniqueIdentifier ?? "unknown";
            if (!_warnedClients.TryAdd(key, 0))
                return;

            LunaLog.Warning($"[KerbalismPlugin]: Ignoring malformed Kerbalism payload from {client?.PlayerName ?? "unknown client"}: {reason} " +
                               "(further warnings for this client are suppressed until server restart)");
        }
    }

    /// <summary>
    /// Tracks clients that have sent a valid Kerbalism payload. Keyed by unique
    /// identifier so a reconnecting client replaces its old entry.
    /// </summary>
    internal class KerbalismClientRegistry
    {
        private readonly object Lock = new object();
        private readonly Dictionary<string, string> _playerNames = new Dictionary<string, string>();

        public int Count
        {
            get { lock (Lock) return _playerNames.Count; }
        }

        public void MarkSeen(ClientStructure client)
        {
            lock (Lock)
                _playerNames[client.UniqueIdentifier ?? client.PlayerName] = client.PlayerName;
        }

        public bool Remove(ClientStructure client)
        {
            lock (Lock)
                return _playerNames.Remove(client.UniqueIdentifier ?? client.PlayerName);
        }

        public void Clear()
        {
            lock (Lock)
                _playerNames.Clear();
        }

        public string FormatClientNames()
        {
            lock (Lock)
            {
                if (_playerNames.Count == 0) return "none";
                return string.Join(", ", _playerNames.Values.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            }
        }
    }
}
