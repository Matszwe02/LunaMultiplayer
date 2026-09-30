using LmpClient.Events;
using LmpClient.Extensions;
using LmpClient.VesselUtilities;
using LmpCommon.Enums;
using System;
using System.Collections.Generic;

namespace LmpClient.Systems.VesselPartSyncUiFieldSys
{
    /// <summary>
    /// Class that maps a message class to a system class. This way we avoid the message caching issues
    /// </summary>
    public class VesselPartSyncUiField
    {
        #region Fields and Properties

        public double GameTime;
        public Guid VesselId;

        public uint PartFlightId;
        public string ModuleName;
        public string FieldName;

        public PartSyncFieldType FieldType;

        public bool BoolValue;
        public int IntValue;
        public float FloatValue;

        #endregion

        public void ProcessPartMethodSync()
        {
            var vessel = FlightGlobals.FindVessel(VesselId);
            if (vessel == null) return;

            if (!VesselCommon.DoVesselChecks(VesselId))
                return;

            var part = vessel.protoVessel.GetProtoPart(PartFlightId);
            if (part != null)
            {
                var module = part.FindProtoPartModuleInProtoPart(ModuleName);
                if (module != null)
                {
                    switch (FieldType)
                    {
                        case PartSyncFieldType.Boolean:
                            module.moduleValues.SetValue(FieldName, BoolValue);
                            SetLiveField(module, BoolValue);
                            PartModuleEvent.onPartModuleBoolFieldProcessed.Fire(module, FieldName, BoolValue);
                            break;
                        case PartSyncFieldType.Integer:
                            module.moduleValues.SetValue(FieldName, IntValue);
                            SetLiveField(module, IntValue);
                            PartModuleEvent.onPartModuleIntFieldProcessed.Fire(module, FieldName, IntValue);
                            break;
                        case PartSyncFieldType.Float:
                            module.moduleValues.SetValue(FieldName, FloatValue);
                            SetLiveField(module, FloatValue);
                            PartModuleEvent.onPartModuleFloatFieldProcessed.Fire(module, FieldName, FloatValue);
                            break;
                        default:
                            //The message type only carries bool/int/float, but never let a bad
                            //value type disconnect the player
                            LunaLog.LogError($"[LMP] Unsupported UI part sync field type {FieldType} for '{ModuleName}.{FieldName}'. Field dropped.");
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Writes the value into the live PartModule as well as the proto. The indexer throws for any
        /// field that is not a registered <see cref="BaseField"/> - most fields a mod exposes as a
        /// plain property - and that escapes the message handler, so skip it: the proto write above
        /// has already succeeded.
        /// </summary>
        private void SetLiveField(ProtoPartModuleSnapshot module, object value)
        {
            if (module.moduleRef == null)
                return;

            try
            {
                module.moduleRef.Fields[FieldName].SetValue(value, module.moduleRef);
            }
            catch (KeyNotFoundException)
            {
                LunaLog.LogWarning($"[LMP] '{ModuleName}.{FieldName}' is not a registered part field, so the live module was not updated (the proto value was).");
            }
        }
    }
}
