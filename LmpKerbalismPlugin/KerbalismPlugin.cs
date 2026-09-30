using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KerbalismSync;
using Server.Client;
using Server.Context;
using Server.Log;
using Server.Plugin;
using Server.System;
using Server.System.Vessel;

namespace KerbalismSync.Server
{
    /// <summary>
    /// Server half of the plugin. LMP already provides the generic mod message channel, so this
    /// only does what LMP has no facility for: store the authoritative state, decide who may write
    /// it, and push it to the clients that asked for it.
    /// </summary>
    public class KerbalismPlugin : LmpPlugin
    {
        private static readonly Protocol.Reassembler Reassembler = new Protocol.Reassembler();

        /// <summary>Which vessels each client currently wants updates for.</summary>
        private static readonly ConcurrentDictionary<string, HashSet<Guid>> Watches =
            new ConcurrentDictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Clients that have said hello, so a mod mismatch can be reported.</summary>
        private static readonly ConcurrentDictionary<string, byte> Clients = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private static long _lastPruneTicks;

        public override void OnServerStart()
        {
            KerbalismStateStore.Load();

            LmpModInterface.RegisterModHandler(Protocol.ModName, HandleModMessage);
            LunaLog.Normal($"[Kerbalism] Server plugin started, channel '{Protocol.ModName}' registered");
        }

        public override void OnServerStop()
        {
            KerbalismStateStore.Save();
            LmpModInterface.UnregisterModHandler(Protocol.ModName);
            Watches.Clear();
            Clients.Clear();
            LunaLog.Normal("[Kerbalism] Server plugin stopped");
        }

        public override void OnClientDisconnect(ClientStructure client)
        {
            if (client == null)
                return;

            Watches.TryRemove(client.PlayerName, out _);
            Clients.TryRemove(client.PlayerName, out _);
        }

        public override void OnUpdate()
        {
            Reassembler.Expire();
            KerbalismStateStore.SaveIfDue();

            //Once a minute rather than per removal, so the file cannot keep state for long-gone vessels
            var now = DateTime.UtcNow.Ticks;
            if (now - _lastPruneTicks > TimeSpan.TicksPerMinute)
            {
                _lastPruneTicks = now;
                var removed = KerbalismStateStore.Prune(VesselStoreSystem.VesselExists);
                if (removed > 0)
                    LunaLog.Normal($"[Kerbalism] Pruned {removed} stale vessel state entr{(removed == 1 ? "y" : "ies")}");
            }
        }

        #region Message handling

        private void HandleModMessage(ClientStructure client, byte[] modData, int numBytes)
        {
            if (client == null || modData == null || numBytes <= 0)
                return;

            //Each frame arrives separately; the reassembler returns a message only once its last chunk lands
            var frame = new byte[numBytes];
            Buffer.BlockCopy(modData, 0, frame, 0, numBytes);

            var message = Reassembler.Accept(frame);
            if (message == null)
                return;

            if (!Protocol.TryParse(message, out var opcode, out var body))
            {
                LunaLog.Warning($"[Kerbalism] Unparseable message from '{client.PlayerName}', dropped");
                return;
            }

            try
            {
                Dispatch(client, opcode, body);
            }
            catch (Exception e)
            {
                LunaLog.Error($"[Kerbalism] Error handling {opcode} from '{client.PlayerName}': {e}");
            }
        }

        private void Dispatch(ClientStructure client, Protocol.Opcode opcode, byte[] body)
        {
            switch (opcode)
            {
                case Protocol.Opcode.Hello:
                    HandleHello(client, body);
                    break;
                case Protocol.Opcode.RequestVessel:
                    HandleRequestVessel(client, body);
                    break;
                case Protocol.Opcode.WatchVessel:
                    HandleWatch(client, body, true);
                    break;
                case Protocol.Opcode.UnwatchVessel:
                    HandleWatch(client, body, false);
                    break;
                case Protocol.Opcode.VesselState:
                    HandleVesselState(client, body);
                    break;
                case Protocol.Opcode.RequestGlobal:
                    HandleRequestGlobal(client);
                    break;
                case Protocol.Opcode.GlobalState:
                    HandleGlobalState(client, body);
                    break;
                default:
                    LunaLog.Warning($"[Kerbalism] Unexpected opcode {opcode} from '{client.PlayerName}', dropped");
                    break;
            }
        }

        private void HandleHello(ClientStructure client, byte[] body)
        {
            var theirProtocol = body.Length > 0 ? body[0] : (byte)0;
            Clients[client.PlayerName] = theirProtocol;

            if (theirProtocol != Protocol.Version)
            {
                LunaLog.Warning($"[Kerbalism] '{client.PlayerName}' speaks protocol {theirProtocol}, this server speaks {Protocol.Version}. " +
                                $"Their updates will be ignored until both sides match.");
                return;
            }

            LunaLog.Normal($"[Kerbalism] '{client.PlayerName}' connected with Kerbalism. " +
                        $"{Clients.Count} Kerbalism client(s) online.");
        }

        private void HandleWatch(ClientStructure client, byte[] body, bool watching)
        {
            if (body.Length < 16)
                return;

            var vesselId = ReadGuid(body, 0);
            if (vesselId == Guid.Empty)
                return;

            var watches = Watches.GetOrAdd(client.PlayerName, _ => new HashSet<Guid>());
            lock (watches)
            {
                if (watching)
                    watches.Add(vesselId);
                else
                    watches.Remove(vesselId);
            }

            if (watching)
                SendVesselState(client, vesselId);
        }

        private void HandleRequestVessel(ClientStructure client, byte[] body)
        {
            if (body.Length < 16)
                return;

            var vesselId = ReadGuid(body, 0);
            if (vesselId == Guid.Empty)
                return;

            SendVesselState(client, vesselId);
        }

        private void HandleVesselState(ClientStructure client, byte[] body)
        {
            // guid(16) + revision(4) + payload
            if (body.Length < 20)
                return;

            var vesselId = ReadGuid(body, 0);
            if (vesselId == Guid.Empty)
                return;

            if (!KerbalismAuthority.CanWrite(client, vesselId))
            {
                KerbalismAuthority.LogRejection(client, vesselId, "sender does not hold the vessel lock");
                SendRejected(client, vesselId, "You do not hold the update lock for this vessel.");
                return;
            }

            var revision = ReadUInt32(body, 16);
            var payload = new byte[body.Length - 20];
            Buffer.BlockCopy(body, 20, payload, 0, payload.Length);

            var existing = KerbalismStateStore.GetVessel(vesselId);

            //Guard against a stale writer replaying an older revision after a reconnect
            if (existing != null && revision < existing.Revision)
            {
                LunaLog.Warning($"[Kerbalism] Ignoring out-of-order state for vessel {vesselId} from '{client.PlayerName}': " +
                                $"revision {revision} is older than stored {existing.Revision}");
                SendRejected(client, vesselId, "A newer state for this vessel has already been stored.");
                return;
            }

            KerbalismStateStore.SetVessel(vesselId, payload, client.PlayerName);

            //Every other watcher, so remote clients converge in real time
            foreach (var watcher in Watches)
            {
                if (string.Equals(watcher.Key, client.PlayerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                Guid[] ids;
                lock (watcher.Value)
                {
                    ids = watcher.Value.Where(id => id == vesselId).ToArray();
                }

                if (ids.Length == 0)
                    continue;

                var target = ServerContext.Clients.Values
                    .FirstOrDefault(c => string.Equals(c.PlayerName, watcher.Key, StringComparison.OrdinalIgnoreCase));
                if (target != null)
                    SendVesselState(target, vesselId);
            }
        }

        private void HandleRequestGlobal(ClientStructure client)
        {
            var stored = KerbalismStateStore.GetGlobal();
            if (stored?.Payload == null)
            {
                Send(client, Protocol.Opcode.UnknownScope, new byte[0]);
                return;
            }

            SendGlobal(client, stored);
        }

        private void HandleGlobalState(ClientStructure client, byte[] body)
        {
            if (!KerbalismAuthority.CanWriteGlobal(client))
            {
                LunaLog.Warning($"[Kerbalism] Rejected global state from '{client.PlayerName}': holds no vessel lock");
                SendRejected(client, Guid.Empty, "You hold no vessel lock, so your global state was rejected.");
                return;
            }

            var payload = new byte[Math.Max(0, body.Length - 4)];
            if (payload.Length > 0)
                Buffer.BlockCopy(body, 4, payload, 0, payload.Length);

            KerbalismStateStore.SetGlobal(payload, client.PlayerName);

            var stored = KerbalismStateStore.GetGlobal();
            foreach (var other in ServerContext.Clients.Values)
            {
                if (string.Equals(other.PlayerName, client.PlayerName, StringComparison.OrdinalIgnoreCase))
                    continue;

                SendGlobal(other, stored);
            }
        }

        #endregion

        #region Sending

        private void SendVesselState(ClientStructure client, Guid vesselId)
        {
            var stored = KerbalismStateStore.GetVessel(vesselId);
            //Without this the client cannot tell "no state yet" from "empty" and wipes its own
            if (stored?.Payload == null)
            {
                Send(client, Protocol.Opcode.UnknownScope, vesselId.ToByteArray());
                return;
            }

            var payload = stored.Payload ?? new byte[0];

            // guid(16) + revision(4) + payload. The id is needed because a client watches several
            // vessels and would otherwise not know which one a response refers to.
            var body = new byte[20 + payload.Length];
            Buffer.BlockCopy(vesselId.ToByteArray(), 0, body, 0, 16);
            WriteUInt32(body, 16, stored.Revision);
            Buffer.BlockCopy(payload, 0, body, 20, payload.Length);

            Send(client, Protocol.Opcode.VesselStateResponse, body);
        }

        private void SendGlobal(ClientStructure client, StoredState stored)
        {
            var payload = stored?.Payload ?? new byte[0];

            // revision(4) + payload
            var body = new byte[4 + payload.Length];
            WriteUInt32(body, 0, stored?.Revision ?? 0);
            Buffer.BlockCopy(payload, 0, body, 4, payload.Length);

            Send(client, Protocol.Opcode.GlobalStateResponse, body);
        }

        private void SendRejected(ClientStructure client, Guid vesselId, string reason)
        {
            var reasonBytes = Encoding.UTF8.GetBytes(reason ?? string.Empty);
            var body = new byte[16 + reasonBytes.Length];
            Buffer.BlockCopy(vesselId.ToByteArray(), 0, body, 0, 16);
            Buffer.BlockCopy(reasonBytes, 0, body, 16, reasonBytes.Length);

            Send(client, Protocol.Opcode.Rejected, body);
        }

        private void Send(ClientStructure client, Protocol.Opcode opcode, byte[] body)
        {
            if (client == null)
                return;

            var message = Protocol.Build(opcode, body);

            foreach (var frame in Protocol.Split(message))
                ModDataSystemSender.SendLmpModMessageToClient(client, Protocol.ModName, frame);
        }

        #endregion

        #region Byte helpers

        private static Guid ReadGuid(byte[] buffer, int offset)
        {
            if (buffer.Length < offset + 16)
                return Guid.Empty;

            var raw = new byte[16];
            Buffer.BlockCopy(buffer, offset, raw, 0, 16);
            return new Guid(raw);
        }

        private static uint ReadUInt32(byte[] buffer, int offset)        {
            if (buffer.Length < offset + 4)
                return 0;

            return (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
        }

        private static void WriteUInt32(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        #endregion
    }
}
