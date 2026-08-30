using System;
using System.Collections.Concurrent;
using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpCommon.Message.Interface;

namespace LmpClient.Systems.VesselSyncSys
{
    public class VesselSyncMessageHandler : SubSystem<VesselSyncSystem>, IMessageHandler
    {
        public ConcurrentQueue<IServerMessageBase> IncomingMessages { get; set; } = new ConcurrentQueue<IServerMessageBase>();

        public void HandleMessage(IServerMessageBase msg)
        {
            if (!(msg is VesselCliMsg vesselMsg)) return;

            switch (vesselMsg.Type)
            {
                case VesselMessageType.CustomWaypointSync:
                    HandleCustomWaypointSyncMsg(vesselMsg.Data);
                    break;
                case VesselMessageType.CustomWaypointDelete:
                    HandleCustomWaypointDeleteMsg(vesselMsg.Data);
                    break;
                // Add other vessel message types here if needed in the future
                default:
                    // Potentially log or ignore unknown message types
                    break;
            }
        }

        private void HandleCustomWaypointSyncMsg(VesselBaseMsgData msgData)
        {
            // This is where the logic to handle custom waypoint sync messages will go.
            // For now, we'll just acknowledge receipt and assume another system will handle the actual display.
            // In a real implementation, you'd likely access a waypoint management system here.
            // Example: WaypointSystem.Instance.AddOrUpdateWaypoint((CustomWaypointMsgData)msgData);
            // For now, we'll just log that we received it.
            // Debug.Log($"Received custom waypoint sync message for vessel: {((CustomWaypointMsgData)msgData).VesselId}");

            var customWaypointData = msgData as CustomWaypointMsgData;
            if (customWaypointData == null) return;

            // This section needs to interact with KSP's Waypoint system.
            // Based on web search, this might involve the "Waypoint Manager" mod or stock KSP APIs like NavWaypoint.fetch.Setup().
            // The exact implementation will depend on how LmpClient integrates with KSP's game objects.
            // For now, we log the data.
            // Debug.Log($"Handling custom waypoint sync: VesselID={customWaypointData.VesselId}, Name={customWaypointData.Name}, Pos={customWaypointData.Position}");

            // TODO: Implement actual KSP waypoint creation/update logic here.
            // This will likely involve interacting with KSP's WaypointManager (from a mod or stock API)
            // to create or update a waypoint using customWaypointData.
        }

        private void HandleCustomWaypointDeleteMsg(VesselBaseMsgData msgData)
        {
            var customWaypointDeleteData = msgData as CustomWaypointDeleteMsgData;
            if (customWaypointDeleteData == null) return;

            // TODO: Implement actual KSP waypoint deletion logic here.
            // This will likely involve interacting with KSP's WaypointManager (from a mod or stock API)
            // to remove the waypoint with the given WaypointId.
            // Example: KspWaypointManager.Instance.RemoveCustomWaypoint(customWaypointDeleteData.WaypointId);
        }
    }
}
