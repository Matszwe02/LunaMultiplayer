using Lidgren.Network;
using LmpCommon.Message.Base;
using LmpCommon.Message.Types;
using System;
using UnityEngine; // Assuming Vector3 and Color are from Unity, adjust if different

namespace LmpCommon.Message.Data.Vessel
{
    public class CustomWaypointMsgData : VesselBaseMsgData
    {
        public override VesselMessageType VesselMessageType => VesselMessageType.CustomWaypointSync;

        public Guid VesselId { get; set; }
        public string Name { get; set; }
        public Vector3d Position { get; set; }
        public Color Color { get; set; }
        public string Icon { get; set; } // e.g., an identifier for a specific waypoint icon

        internal CustomWaypointMsgData() { } // For deserialization

        public CustomWaypointMsgData(Guid vesselId, string name, Vector3d position, Color color, string icon)
        {
            VesselId = vesselId;
            Name = name;
            Position = position;
            Color = color;
            Icon = icon;
        }

        public override string ClassName => nameof(CustomWaypointMsgData);

        internal override void InternalSerialize(NetOutgoingMessage lidgrenMsg)
        {
            base.InternalSerialize(lidgrenMsg);

            GuidUtil.Serialize(VesselId, lidgrenMsg);
            lidgrenMsg.Write(Name ?? string.Empty); // Write empty string if null
            lidgrenMsg.Write(Position.x);
            lidgrenMsg.Write(Position.y);
            lidgrenMsg.Write(Position.z);
            lidgrenMsg.Write(Color.r);
            lidgrenMsg.Write(Color.g);
            lidgrenMsg.Write(Color.b);
            lidgrenMsg.Write(Color.a);
            lidgrenMsg.Write(Icon ?? string.Empty); // Write empty string if null
        }

        internal override void InternalDeserialize(NetIncomingMessage lidgrenMsg)
        {
            base.InternalDeserialize(lidgrenMsg);

            VesselId = GuidUtil.Deserialize(lidgrenMsg);
            Name = lidgrenMsg.ReadString();
            Position = new Vector3d(lidgrenMsg.ReadDouble(), lidgrenMsg.ReadDouble(), lidgrenMsg.ReadDouble());
            Color = new Color(lidgrenMsg.ReadFloat(), lidgrenMsg.ReadFloat(), lidgrenMsg.ReadFloat(), lidgrenMsg.ReadFloat());
            Icon = lidgrenMsg.ReadString();
        }

        internal override int InternalGetMessageSize()
        {
            return base.InternalGetMessageSize() +
                   GuidUtil.ByteSize +
                   (Name?.Length ?? 0) + sizeof(char) + // Approximate size for string length + null terminator
                   sizeof(double) * 3 + // Vector3d
                   sizeof(float) * 4 + // Color
                   (Icon?.Length ?? 0) + sizeof(char); // Approximate size for string length + null terminator
        }
    }
}