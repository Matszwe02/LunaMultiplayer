using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;

namespace LmpCommon.Message.Data.Vessel
{
    public class CustomWaypointDeleteMsgData : VesselBaseMsgData
    {
        public override VesselMessageType VesselMessageType => VesselMessageType.CustomWaypointDelete;

        public Guid WaypointId { get; set; }

        internal CustomWaypointDeleteMsgData() { } // For deserialization

        public CustomWaypointDeleteMsgData(Guid waypointId)
        {
            WaypointId = waypointId;
        }

        public override string ClassName => nameof(CustomWaypointDeleteMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);
            GuidUtil.Serialize(WaypointId, lidgrenMsg);
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);
            WaypointId = GuidUtil.Deserialize(lidgrenMsg);
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() + GuidUtil.ByteSize;
        }
    }
}