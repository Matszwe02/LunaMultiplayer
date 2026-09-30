using System;
using LmpCommon.Locks;
using Server.Client;
using Server.Log;
using Server.System;

namespace KerbalismSync.Server
{
    /// <summary>
    /// Decides whether a client may write a vessel's Kerbalism state. Without this the plugin
    /// inherits the last-writer-wins race of LMP's generic scenario sync, and Kerbalism's automation
    /// tab does not consult LMP's locks at all, so the check has to live on the server.
    /// </summary>
    public static class KerbalismAuthority
    {
        /// <summary>
        /// Control and Update both qualify: a client flying a vessel without taking an explicit lock
        /// still legitimately drives its Kerbalism state.
        /// </summary>
        private static readonly LockType[] WriterLocks =
        {
            LockType.Control,
            LockType.Update
        };

        /// <summary>
        /// False when nobody holds a lock: without a lock there is no authority, and accepting the
        /// write would reintroduce the race.
        /// </summary>
        public static bool CanWrite(ClientStructure client, Guid vesselId)
        {
            if (client == null || vesselId == Guid.Empty)
                return false;

            foreach (var type in WriterLocks)
            {
                if (LockSystem.LockQuery.LockBelongsToPlayer(type, vesselId, string.Empty, client.PlayerName))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// True when a client is entitled to write save-global state. LMP has no global lock, so the
        /// rule is permissive: holding any vessel lock is the closest thing to "is participating".
        /// </summary>
        public static bool CanWriteGlobal(ClientStructure client)
        {
            if (client == null)
                return false;

            foreach (var lockDefinition in LockSystem.LockQuery.GetAllPlayerLocks(client.PlayerName))
            {
                if (lockDefinition.Type == LockType.Update || lockDefinition.Type == LockType.Control)
                    return true;
            }

            return false;
        }

        /// <summary>Who does hold authority, for a rejection message.</summary>
        public static string DescribeOwner(Guid vesselId)
        {
            foreach (var type in WriterLocks)
            {
                var held = LockSystem.LockQuery.GetLock(type, string.Empty, vesselId, string.Empty);
                if (held != null)
                    return $"{held.PlayerName} ({type})";
            }

            return "nobody";
        }

        public static void LogRejection(ClientStructure client, Guid vesselId, string reason)
        {
            LunaLog.Warning($"[Kerbalism] Rejected state update for vessel {vesselId} from " +
                            $"'{client?.PlayerName ?? "<unknown>"}': {reason}. Current authority: {DescribeOwner(vesselId)}");
        }
    }
}
