using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using LmpClient;
using KerbalismSync;
using LmpClient.ClientPlugins;
using LmpClient.Events;
using LmpClient.Systems.Lock;
using LmpClient.Systems.SettingsSys;
using LmpClient.VesselUtilities;

namespace KerbalismSync.Client
{
    /// <summary>
    /// Client half of the plugin. Per-vessel state (automation, storms, science drives) lives in
    /// Kerbalism's <c>VesselData</c> rather than the part protos, so it travels on this plugin's own
    /// channel; persistent part-module fields instead ride LMP's part-sync transport, which already
    /// persists server-side and survives late joiners. Both are diffed rather than write-observed,
    /// because LMP's transpiler cannot see most Kerbalism writes.
    /// </summary>
    public class KerbalismClientPlugin : ILmpClientPlugin
    {        private const string Tag = "[Kerbalism] ";

        /// <summary>How often the authoritative vessel state is compared against what was last sent.</summary>
        private static readonly TimeSpan StateInterval = TimeSpan.FromSeconds(2);

        /// <summary>How often the part-module fields of owned vessels are compared.</summary>
        private static readonly TimeSpan FieldInterval = TimeSpan.FromSeconds(1);

        private ILmpClientPluginContext _context;
        private readonly Protocol.Reassembler _reassembler = new Protocol.Reassembler();

        /// <summary>Vessels this client is tracking, and what it last sent for them.</summary>
        private readonly Dictionary<Guid, TrackedVessel> _tracked = new Dictionary<Guid, TrackedVessel>();

        private DateTime _nextStateScan = DateTime.MinValue;
        private DateTime _nextFieldScan = DateTime.MinValue;
        private DateTime _nextExpire = DateTime.MinValue;

        private uint _revision;
        private bool _announced;
        private bool _kerbalismUnavailable;

        private sealed class TrackedVessel
        {
            public Guid VesselId;
            public string LastSentDigest;
            public DateTime LastFieldScan = DateTime.MinValue;
            public readonly Dictionary<string, Dictionary<string, string>> LastSentFields =
                new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        }

        //Method groups, not lambdas: EventData.Remove matches by delegate identity, so a lambda never unsubscribes
        private bool _subscribed;

        public string PluginName => "Kerbalism";

        private void OnVesselLoaded(Vessel vessel) => Track(vessel);
        private void OnVesselReloaded(Vessel vessel) => Track(vessel);
        private void OnVesselRemoved(Guid vesselId) => Untrack(vesselId);

        #region Lifecycle

        public void OnAwake(ILmpClientPluginContext context)
        {
            _context = context;

            if (!KerbalismApi.TryResolve())
            {
                _kerbalismUnavailable = true;
                LunaLog.Log($"{Tag}Kerbalism is not installed, Kerbalism sync will stay inactive. " +
                            $"This is not an error if you are not playing with Kerbalism.");
                return;
            }

            LunaLog.Log($"{Tag}Bound to Kerbalism. Watching {KerbalismPartFields.Table.Count} part module(s), " +
                        $"{KerbalismPartFields.DeclaredFieldCount} field(s). " +
                        $"Unavailable optional structures: {KerbalismApi.DescribeOptionalGaps()}");

            //Push into LMP's transport, and observe what arrives from other clients
            PartModuleEvent.onPartModuleBoolFieldProcessed.Add(OnBoolProcessed);
            PartModuleEvent.onPartModuleIntFieldProcessed.Add(OnIntProcessed);
            PartModuleEvent.onPartModuleUIntFieldProcessed.Add(OnUIntProcessed);
            PartModuleEvent.onPartModuleFloatFieldProcessed.Add(OnFloatProcessed);
            PartModuleEvent.onPartModuleDoubleFieldProcessed.Add(OnDoubleProcessed);
            PartModuleEvent.onPartModuleStringFieldProcessed.Add(OnStringProcessed);
            PartModuleEvent.onPartModuleEnumFieldProcessed.Add(OnEnumProcessed);

            VesselLoadEvent.onLmpVesselLoaded.Add(OnVesselLoaded);
            VesselReloadEvent.onLmpVesselReloaded.Add(OnVesselReloaded);
            VesselRemoveEvent.onLmpVesselRemoved.Add(OnVesselRemoved);

            _subscribed = true;

            context.RegisterModHandler(Protocol.ModName, OnServerMessage);
        }

        public void OnEnabled()
        {
            _tracked.Clear();
            _nextStateScan = DateTime.MinValue;
            _nextFieldScan = DateTime.MinValue;

            if (_kerbalismUnavailable || !KerbalismApi.Available)
                return;

            Send(Protocol.Opcode.Hello, new[] { Protocol.Version });

            //A scene change or a new vessel does not always raise a load event, so sweep now
            RefreshTracking();
        }

        public void OnDisabled()
        {
            foreach (var tracked in _tracked.Values.ToArray())
                Send(Protocol.Opcode.UnwatchVessel, tracked.VesselId.ToByteArray());

            _tracked.Clear();
            _announced = false;
            _revision = 0;
        }

        public void OnUpdate()
        {
            if (_kerbalismUnavailable || !KerbalismApi.Available || _context == null)
                return;

            if (HighLogic.CurrentGame?.flightState == null)
                return;

            var now = DateTime.UtcNow;

            if (now >= _nextExpire)
            {
                _nextExpire = now.AddSeconds(5);
                _reassembler.Expire();
            }

            //Kerbalism re-loads on every scene change, so its data is not readable until then
            if (!KerbalismApi.IsSaveGameReady)
                return;

            RefreshTracking();

            if (now >= _nextStateScan)
            {
                _nextStateScan = now.Add(StateInterval);
                ScanVesselState();
            }

            if (now >= _nextFieldScan)
            {
                _nextFieldScan = now.Add(FieldInterval);
                ScanPartFields();
            }
        }

        public void OnDestroy()
        {
            if (!_subscribed)
                return;

            _subscribed = false;

            PartModuleEvent.onPartModuleBoolFieldProcessed.Remove(OnBoolProcessed);
            PartModuleEvent.onPartModuleIntFieldProcessed.Remove(OnIntProcessed);
            PartModuleEvent.onPartModuleUIntFieldProcessed.Remove(OnUIntProcessed);
            PartModuleEvent.onPartModuleFloatFieldProcessed.Remove(OnFloatProcessed);
            PartModuleEvent.onPartModuleDoubleFieldProcessed.Remove(OnDoubleProcessed);
            PartModuleEvent.onPartModuleStringFieldProcessed.Remove(OnStringProcessed);
            PartModuleEvent.onPartModuleEnumFieldProcessed.Remove(OnEnumProcessed);

            VesselLoadEvent.onLmpVesselLoaded.Remove(OnVesselLoaded);
            VesselReloadEvent.onLmpVesselReloaded.Remove(OnVesselReloaded);
            VesselRemoveEvent.onLmpVesselRemoved.Remove(OnVesselRemoved);
        }

        #endregion

        #region Tracking

        private void Track(Vessel vessel)
        {
            if (vessel != null)
                Track(vessel.id);
        }

        private void Track(Guid vesselId)
        {
            if (_kerbalismUnavailable || vesselId == Guid.Empty || _tracked.ContainsKey(vesselId))
                return;

            _tracked[vesselId] = new TrackedVessel { VesselId = vesselId };
            Send(Protocol.Opcode.WatchVessel, vesselId.ToByteArray());
        }

        private void Untrack(Guid vesselId)
        {
            if (!_tracked.TryGetValue(vesselId, out var tracked))
                return;

            _tracked.Remove(vesselId);
            Send(Protocol.Opcode.UnwatchVessel, vesselId.ToByteArray());
        }

        /// <summary>
        /// Watches every visible vessel. Load events are not guaranteed on every path into a scene -
        /// the tracking station reloads through a fast path that fires none - so this is the net.
        /// </summary>
        private void RefreshTracking()
        {
            if (HighLogic.CurrentGame?.flightState == null)
                return;

            foreach (var protoVessel in HighLogic.CurrentGame.flightState.protoVessels)
            {
                if (protoVessel != null && protoVessel.vesselID != Guid.Empty)
                    Track(protoVessel.vesselID);
            }
        }

        #endregion

        #region Sending authoritative vessel state

        private void ScanVesselState()
        {
            var me = SettingsSystem.CurrentSettings?.PlayerName;

            foreach (var tracked in _tracked.Values)
            {
                var protoVessel = FindProtoVessel(tracked.VesselId);
                if (protoVessel == null)
                    continue;

                //Only lock holders author state (the server enforces it too); this avoids a wasted serialisation
                if (!HoldsVesselLock(tracked.VesselId, me))
                    continue;

                var vesselData = KerbalismApi.GetVesselData(protoVessel);
                if (vesselData == null)
                    continue;

                var node = KerbalismVesselState.Capture(vesselData);
                if (node == null)
                    continue;

                //This text is the whole payload, so serialising twice per interval is waste
                var text = node.ToString();
                if (text == tracked.LastSentDigest)
                    continue;

                tracked.LastSentDigest = text;

                var payload = System.Text.Encoding.UTF8.GetBytes(text);

                var body = new byte[20 + payload.Length];
                Buffer.BlockCopy(tracked.VesselId.ToByteArray(), 0, body, 0, 16);
                WriteUInt32(body, 16, ++_revision);
                Buffer.BlockCopy(payload, 0, body, 20, payload.Length);

                Send(Protocol.Opcode.VesselState, body);

                if (!_announced)
                {
                    _announced = true;
                    LunaLog.Log($"{Tag}Tracking {tracked.VesselId} and sending state changes");
                }
            }
        }

        private static bool HoldsVesselLock(Guid vesselId, string playerName)
        {
            if (string.IsNullOrEmpty(playerName))
                return false;

            return LockSystem.LockQuery.UpdateLockBelongsToPlayer(vesselId, playerName) ||
                   LockSystem.LockQuery.ControlLockBelongsToPlayer(vesselId, playerName);
        }

        #endregion

        #region Part module fields

        /// <summary>
        /// Diffs the declared part-module fields on vessels we own and pushes changes through LMP's
        /// part-sync transport by firing its public events.
        /// </summary>
        private void ScanPartFields()
        {
            var me = SettingsSystem.CurrentSettings?.PlayerName;
            var now = DateTime.UtcNow;

            foreach (var tracked in _tracked.Values)
            {
                if (now < tracked.LastFieldScan)
                    continue;

                tracked.LastFieldScan = now;

                if (!HoldsVesselLock(tracked.VesselId, me))
                    continue;

                var vessel = FlightGlobals.FindVessel(tracked.VesselId);
                if (vessel == null || !vessel.loaded || !VesselCommon.DoVesselChecks(tracked.VesselId))
                    continue;

                var moduleNames = new HashSet<string>(KerbalismPartFields.Table.Keys, StringComparer.Ordinal);

                foreach (var part in vessel.Parts)
                {
                    foreach (var module in part.Modules)
                    {
                        if (module == null || !moduleNames.Contains(module.GetType().Name))
                            continue;

                        var specs = KerbalismPartFields.Table[module.GetType().Name];
                        PushFieldChanges(module, specs, tracked);
                    }
                }
            }
        }

        private void PushFieldChanges(PartModule module, FieldSpec[] specs, TrackedVessel tracked)
        {
            var moduleKey = module.GetType().Name;
            var partKey = PartKey(module);

            if (!tracked.LastSentFields.TryGetValue(partKey, out var lastForPart))
            {
                lastForPart = new Dictionary<string, string>(StringComparer.Ordinal);
                tracked.LastSentFields[partKey] = lastForPart;
            }

            foreach (var spec in specs)
            {
                if (!KerbalismPartFields.TryRead(module, spec, out var value))
                    continue;

                if (lastForPart.TryGetValue(spec.Name, out var previous) && previous == value)
                    continue;

                lastForPart[spec.Name] = value;

                try
                {
                    Fire(module, spec, value);
                }
                catch (Exception e)
                {
                    LunaLog.LogWarning($"{Tag}Could not send {moduleKey}.{spec.Name}: {e.Message}");
                }
            }
        }

        private static string PartKey(PartModule module) => module.part?.flightID.ToString(CultureInfo.InvariantCulture) ?? "?";

        private static void Fire(PartModule module, FieldSpec spec, string value)
        {
            switch (spec.Kind)
            {
                case FieldKind.Bool:
                    PartModuleEvent.onPartModuleBoolFieldChanged.Fire(module, spec.Name,
                        KerbalismVesselState.TryParseBool(value, out var b) && b);
                    break;

                case FieldKind.Int:
                    PartModuleEvent.onPartModuleIntFieldChanged.Fire(module, spec.Name,
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : 0);
                    break;

                case FieldKind.UInt:
                    PartModuleEvent.onPartModuleUIntFieldChanged.Fire(module, spec.Name,
                        uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ui) ? ui : 0u);
                    break;

                case FieldKind.Float:
                    PartModuleEvent.onPartModuleFloatFieldChanged.Fire(module, spec.Name,
                        KerbalismVesselState.TryParseFloat(value, out var f) ? f : 0f);
                    break;

                case FieldKind.Double:
                    PartModuleEvent.onPartModuleDoubleFieldChanged.Fire(module, spec.Name,
                        KerbalismVesselState.TryParseDouble(value, out var d) ? d : 0d);
                    break;

                case FieldKind.String:
                    PartModuleEvent.onPartModuleStringFieldChanged.Fire(module, spec.Name, value);
                    break;

                case FieldKind.Enum:
                    PartModuleEvent.onPartModuleEnumFieldChanged.Fire(module, spec.Name,
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? e : 0, value);
                    break;
            }
        }

        #endregion

        #region Receiving

        private void OnServerMessage(byte[] data)
        {
            var message = _reassembler.Accept(data);
            if (message == null)
                return;

            if (!Protocol.TryParse(message, out var opcode, out var body))
                return;

            try
            {
                switch (opcode)
                {
                    case Protocol.Opcode.VesselStateResponse:
                        ApplyRemoteVesselState(body);
                        break;

                    case Protocol.Opcode.UnknownScope:
                        //Keep whatever is local rather than treating this as an empty state
                        if (body.Length >= 16)
                        {
                            LunaLog.LogWarning($"{Tag}Server has no stored state for vessel {new Guid(body)}. Keeping local state.");
                        }
                        break;

                    case Protocol.Opcode.Rejected:
                        if (body.Length >= 16)
                        {
                            var id = new Guid(body);
                            var reason = System.Text.Encoding.UTF8.GetString(body, 16, body.Length - 16);
                            LunaLog.LogWarning($"{Tag}Server rejected an update{(id == Guid.Empty ? string.Empty : $" for vessel {id}")}: {reason}");
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"{Tag}Error handling {opcode}: {e.GetType().Name} {e.Message}");
            }
        }

        private void ApplyRemoteVesselState(byte[] body)
        {
            if (body.Length < 20)
                return;

            var vesselId = new Guid(body);

            //Never apply our own state back: it would undo a newer local edit and fight our automation
            if (HoldsVesselLock(vesselId, SettingsSystem.CurrentSettings?.PlayerName))
                return;

            var protoVessel = FindProtoVessel(vesselId);
            if (protoVessel == null)
                return;

            var node = ConfigNode.Parse(System.Text.Encoding.UTF8.GetString(body, 20, body.Length - 20)) as ConfigNode;
            if (node == null)
            {
                LunaLog.LogWarning($"{Tag}Could not parse remote state for vessel {vesselId}");
                return;
            }

            if (!node.HasValue("scienceTransmitted") && node.GetNodes("parts").Length == 0 && node.GetNode("computer") == null)
            {
                //Almost certainly a malformed send; applying it would clear the local automation
                LunaLog.LogWarning($"{Tag}Ignoring an empty remote state for vessel {vesselId}");
                return;
            }

            var vesselData = KerbalismApi.GetOrCreateVesselData(protoVessel);
            if (vesselData == null)
                return;

            var report = KerbalismVesselState.Apply(vesselData, node);
            if (report.AnythingFailed)
                LunaLog.LogWarning($"{Tag}Applied remote state for vessel {vesselId} with gaps: {string.Join("; ", report.Skipped)}");
        }

        #endregion

        #region Live mirror for remote part modules

        //LMP writes only the persisted config node, so a remote vessel's live module stays stale until reload

        private void OnBoolProcessed(ProtoPartModuleSnapshot module, string field, bool value) => Mirror(module, field, value);
        private void OnIntProcessed(ProtoPartModuleSnapshot module, string field, int value) => Mirror(module, field, value);
        private void OnUIntProcessed(ProtoPartModuleSnapshot module, string field, uint value) => Mirror(module, field, value);
        private void OnFloatProcessed(ProtoPartModuleSnapshot module, string field, float value) => Mirror(module, field, value);
        private void OnDoubleProcessed(ProtoPartModuleSnapshot module, string field, double value) => Mirror(module, field, value);
        private void OnStringProcessed(ProtoPartModuleSnapshot module, string field, string value) => Mirror(module, field, value);
        private void OnEnumProcessed(ProtoPartModuleSnapshot module, string field, int value, string valueStr) => Mirror(module, field, value);

        private void Mirror(ProtoPartModuleSnapshot snapshot, string fieldName, object value)
        {
            try
            {
                if (snapshot?.moduleRef == null)
                    return;

                var module = snapshot.moduleRef;
                var typeName = module.GetType().Name;

                if (!KerbalismPartFields.Table.ContainsKey(typeName))
                    return;

                //Only mirror declared fields, so a vanilla part sync keeps LMP's own behaviour
                bool declared = false;
                foreach (var spec in KerbalismPartFields.Table[typeName])
                {
                    if (spec.Name == fieldName)
                    {
                        declared = true;
                        break;
                    }
                }

                if (!declared)
                    return;

                var vessel = module.vessel;
                if (vessel != null && HoldsVesselLock(vessel.id, SettingsSystem.CurrentSettings?.PlayerName))
                    return;

                module.Fields[fieldName].SetValue(value, module);
            }
            catch (KeyNotFoundException)
            {
                    //Not a registered BaseField (many Kerbalism fields are plain properties); the proto value is already correct
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"{Tag}Could not mirror {fieldName} into the live module: {e.Message}");
            }
        }

        #endregion

        #region Plumbing

        private void Send(Protocol.Opcode opcode, byte[] body)
        {
            if (_context == null)
                return;

            foreach (var frame in Protocol.Split(Protocol.Build(opcode, body)))
                _context.SendModMessage(Protocol.ModName, frame);
        }

        private static ProtoVessel FindProtoVessel(Guid vesselId)
        {
            var flightState = HighLogic.CurrentGame?.flightState;
            if (flightState == null || vesselId == Guid.Empty)
                return null;

            foreach (var protoVessel in flightState.protoVessels)
            {
                if (protoVessel != null && protoVessel.vesselID == vesselId)
                    return protoVessel;
            }

            return null;
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
