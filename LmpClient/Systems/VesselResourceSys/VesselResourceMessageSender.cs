using LmpClient.Base;
using LmpClient.Base.Interface;
using LmpClient.Network;
using LmpClient.Systems.TimeSync;
using LmpCommon.Message.Client;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Interface;
using System;
using System.Collections.Generic;

namespace LmpClient.Systems.VesselResourceSys
{
    public class VesselResourceMessageSender : SubSystem<VesselResourceSystem>, IMessageSender
    {
        private static readonly List<VesselResourceInfo> Resources = new List<VesselResourceInfo>();

        /// <summary>
        /// Vessels for which we already warned about proto parts with flightID 0.
        /// A proto part with flightID 0 can never be matched by receiving clients, so
        /// sending its resources is pointless and only spams warnings on the other side.
        /// </summary>
        private static readonly HashSet<Guid> WarnedZeroFlightIdVessels = new HashSet<Guid>();

        public void SendMessage(IMessageData msg)
        {
            NetworkSender.QueueOutgoingMessage(MessageFactory.CreateNew<VesselCliMsg>(msg));
        }

        public void SendVesselResources(Vessel vessel)
        {
            var resourceCount = 0;

            var msgData = NetworkMain.CliMsgFactory.CreateNewMessageData<VesselResourceMsgData>();
            msgData.GameTime = TimeSyncSystem.UniversalTime;
            msgData.VesselId = vessel.id;

            for (var i = 0; i < vessel.protoVessel.protoPartSnapshots.Count; i++)
            {
                var partSnapshot = vessel.protoVessel.protoPartSnapshots[i];
                if (partSnapshot?.resources == null) continue;

                if (partSnapshot.flightID == 0)
                {
                    if (WarnedZeroFlightIdVessels.Add(vessel.id))
                    {
                        LunaLog.LogWarning($"[LMP]: Skipping resource send for vessel {vessel.id} ({vessel.vesselName}) - " +
                                           $"proto part '{partSnapshot.partName}' has flightID 0 (broken vessel proto). " +
                                           "Resources of this part cannot be matched by other clients until the vessel proto is re-synchronized.");
                    }
                    continue;
                }

                for (var j = 0; j < vessel.protoVessel.protoPartSnapshots[i].resources.Count; j++)
                {
                    var resource = vessel.protoVessel.protoPartSnapshots[i].resources[j]?.resourceRef;
                    if (resource == null) continue;

                    if (Resources.Count > resourceCount)
                    {
                        Resources[resourceCount].ResourceName = resource.resourceName;
                        Resources[resourceCount].PartFlightId = partSnapshot.flightID;
                        Resources[resourceCount].Amount = resource.amount;
                        Resources[resourceCount].FlowState = resource.flowState;
                    }
                    else
                    {
                        Resources.Add(new VesselResourceInfo
                        {
                            ResourceName = resource.resourceName,
                            PartFlightId = partSnapshot.flightID,
                            Amount = resource.amount,
                            FlowState = resource.flowState
                        });
                    }

                    resourceCount++;
                }
            }

            msgData.ResourcesCount = resourceCount;

            if (msgData.Resources.Length < resourceCount)
            {
                msgData.Resources = new VesselResourceInfo[resourceCount];
            }

            for (var i = 0; i < resourceCount; i++)
            {
                if (msgData.Resources[i] == null)
                    msgData.Resources[i] = new VesselResourceInfo(Resources[i]);
                else
                    msgData.Resources[i].CopyFrom(Resources[i]);
            }

            SendMessage(msgData);
        }

        /// <summary>
        /// Clears the zero-flightID warning cache, called when the system is disabled
        /// (e.g. disconnect) so warnings can reappear in a future session
        /// </summary>
        public void ClearZeroFlightIdWarnings()
        {
            WarnedZeroFlightIdVessels.Clear();
        }
    }
}
