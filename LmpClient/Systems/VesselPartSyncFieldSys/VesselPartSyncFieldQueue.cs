using System;
using LmpClient.Base;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Vessel;
using UnityEngine;

namespace LmpClient.Systems.VesselPartSyncFieldSys
{
    public class VesselPartSyncFieldQueue : CachedConcurrentQueue<VesselPartSyncField, VesselPartSyncFieldMsgData>
    {
        protected override void AssignFromMessage(VesselPartSyncField value, VesselPartSyncFieldMsgData msgData)
        {
            value.GameTime = msgData.GameTime;
            value.VesselId = msgData.VesselId;

            value.PartFlightId = msgData.PartFlightId;
            value.ModuleName = msgData.ModuleName.Clone() as string;
            value.FieldName = msgData.FieldName.Clone() as string;

            value.FieldType = msgData.FieldType;

            switch (value.FieldType)
            {
                case PartSyncFieldType.Boolean:
                    value.BoolValue = msgData.BoolValue;
                    break;
                case PartSyncFieldType.Integer:
                    value.IntValue = msgData.IntValue;
                    break;
                case PartSyncFieldType.Short:
                    value.ShortValue = msgData.ShortValue;
                    break;
                case PartSyncFieldType.UShort:
                    value.UShortValue = msgData.UShortValue;
                    break;
                case PartSyncFieldType.UInteger:
                    value.UIntValue = msgData.UIntValue;
                    break;
                case PartSyncFieldType.Long:
                    value.LongValue = msgData.LongValue;
                    break;
                case PartSyncFieldType.ULong:
                    value.ULongValue = msgData.ULongValue;
                    break;
                case PartSyncFieldType.Float:
                    value.FloatValue = msgData.FloatValue;
                    break;
                case PartSyncFieldType.Double:
                    value.DoubleValue = msgData.DoubleValue;
                    break;
                case PartSyncFieldType.Vector2:
                    value.Vector2Value = new Vector2(msgData.VectorValue[0], msgData.VectorValue[1]);
                    break;
                case PartSyncFieldType.Vector3:
                    value.Vector3Value = new Vector3(msgData.VectorValue[0], msgData.VectorValue[1], msgData.VectorValue[2]);
                    break;
                case PartSyncFieldType.Quaternion:
                    value.QuaternionValue = new Quaternion(msgData.QuaternionValue[0], msgData.QuaternionValue[1], msgData.QuaternionValue[2], msgData.QuaternionValue[3]);
                    break;
                case PartSyncFieldType.Object:
                case PartSyncFieldType.String:
                    value.StrValue = msgData.StrValue.Clone() as string;
                    break;
                case PartSyncFieldType.Enum:
                    value.IntValue = msgData.IntValue;
                    value.StrValue = msgData.StrValue.Clone() as string;
                    break;
                default:
                    //Every type is legal on the wire, so an unhandled one used to disconnect the player.
                    LunaLog.LogError($"[LMP] Unsupported part sync field type {value.FieldType} for '{value.ModuleName}.{value.FieldName}' " +
                                     $"(vessel {value.VesselId}, part {value.PartFlightId}). Field dropped.");
                    break;
            }
        }
    }
}
