using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using LmpClient;

namespace KerbalismSync.Client
{
    /// <summary>
    /// Converts a vessel's Kerbalism <c>VesselData</c> to and from a <see cref="ConfigNode"/>. Capture
    /// uses Kerbalism's own <c>Save</c> so both ends agree on the layout; apply is hand written
    /// because several structures are keyed by reference and a generic walker cannot resolve those.
    /// <para>
    /// Two rules from Kerbalism's load path: never replace the <c>VesselData</c> instance (a loaded
    /// <c>HardDrive</c> permanently aliases the <c>Drive</c> inside the old one), and never call
    /// <c>VesselData.Load</c> (its <c>Drive</c> ctor calls <c>AddDataCollectedInFlight</c> without
    /// removing the old contribution, inflating the global science counter every apply).
    /// </para>
    /// </summary>
    public static class KerbalismVesselState
    {
        /// <summary>UI toggles and "already showed this message" flags: per-player by definition.</summary>
        private static readonly HashSet<string> PerPlayerValues = new HashSet<string>(StringComparer.Ordinal)
        {
            "cfg_ec", "cfg_supply", "cfg_signal", "cfg_malfunction",
            "cfg_storm", "cfg_script", "cfg_highlights", "cfg_showlink",
            "cfg_show", "msg_signal", "msg_belt"
        };

        /// <summary>Per-player or local-only nodes.</summary>
        private static readonly HashSet<string> PerPlayerNodes = new HashSet<string>(StringComparer.Ordinal)
        {
            //Only holds per-resource "have I warned about this" counters
            "supplies"
        };

        /// <summary>Serialises a vessel's Kerbalism state and removes the per-player parts.</summary>
        public static ConfigNode Capture(object vesselData)
        {
            if (vesselData == null)
                return null;

            var node = new ConfigNode("vessel");
            KerbalismApi.VesselDataSave.Invoke(vesselData, new object[] { node });

            foreach (var name in PerPlayerValues)
                node.RemoveValues(name);

            foreach (var name in PerPlayerNodes)
                node.RemoveNode(name);

            //Computer's ctor dereferences node.GetNode("scripts") without a null check, so an
            //automation payload must always carry an (possibly empty) scripts node
            var computer = node.GetNode("computer");
            if (computer != null && computer.GetNode("scripts") == null)
                computer.AddNode("scripts");

            return node;
        }

        /// <summary>Applied in place; each step is guarded so one bad structure cannot reject the update.</summary>
        public static ApplyReport Apply(object vesselData, ConfigNode incoming)
        {
            var report = new ApplyReport();
            if (vesselData == null || incoming == null)
                return report;

            Try(report, "automation", () => ApplyComputer(vesselData, incoming));
            Try(report, "deviceTransmit", () => ApplyBool(KerbalismApi.VdDeviceTransmit, vesselData, incoming, "deviceTransmit"));

            //A lifetime counter, so it only ever moves forward; overwriting downward would undo
            //science another player legitimately collected
            Try(report, "scienceTransmitted", () =>
            {
                if (!incoming.HasValue("scienceTransmitted"))
                    return;

                if (TryParseDouble(incoming.GetValue("scienceTransmitted"), out var incomingValue))
                {
                    var current = (double)KerbalismApi.VdScienceTransmitted.GetValue(vesselData);
                    if (incomingValue > current)
                        KerbalismApi.VdScienceTransmitted.SetValue(vesselData, incomingValue);
                }
            });

            Try(report, "scansat_id", () => ApplyScansat(vesselData, incoming));
            Try(report, "spinSnapshot", () =>
            {
                ApplyBool(KerbalismApi.VdSpinSnapshotValid, vesselData, incoming, "spinSnapshotValid");
                ApplyInt(KerbalismApi.VdSpinQualifyingCapacity, vesselData, incoming, "spinQualifyingCapacity");
                ApplyFloat(KerbalismApi.VdSpinSampleMinArtificialG, vesselData, incoming, "spinSampleMinArtificialG");
                ApplyDouble(KerbalismApi.VdSpinRpm, vesselData, incoming, "spinRpm");
                ApplyDouble(KerbalismApi.VdSolarPanelsAverageExposure, vesselData, incoming, "solarPanelsAverageAnalyticExposure");
            });

            Try(report, "groundController", () => ApplyBool(KerbalismApi.VdIsGroundCtrl, vesselData, incoming, "isGroundCtrl"));
            Try(report, "storm", () => ApplyStorm(vesselData, incoming));
            Try(report, "systemHeat", () => ApplySystemHeat(vesselData, incoming));
            Try(report, "dumpValves", () => ApplyDumpValves(vesselData, incoming));
            Try(report, "drives", () => DriveState.Apply(vesselData, incoming, report));

            return report;
        }

        #region Individual pieces

        private static void ApplyComputer(object vesselData, ConfigNode incoming)
        {
            var computerNode = incoming.GetNode("computer");
            if (computerNode == null)
                return;

            var scripts = computerNode.GetNode("scripts");
            if (scripts == null)
                computerNode.AddNode("scripts");

            //Safe to replace wholesale: the old Computer is not cached anywhere and applying a
            //script fires nothing until the next Kerbalism FixedUpdate
            var replacement = KerbalismApi.ComputerCtor.Invoke(new object[] { computerNode });
            KerbalismApi.VdComputer.SetValue(vesselData, replacement);
        }

        private static void ApplyBool(FieldInfo field, object target, ConfigNode node, string valueName)
        {
            if (field == null || !node.HasValue(valueName))
                return;

            if (TryParseBool(node.GetValue(valueName), out var value))
                field.SetValue(target, value);
        }

        private static void ApplyInt(FieldInfo field, object target, ConfigNode node, string valueName)
        {
            if (field == null || !node.HasValue(valueName))
                return;

            if (int.TryParse(node.GetValue(valueName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                field.SetValue(target, value);
        }

        private static void ApplyFloat(FieldInfo field, object target, ConfigNode node, string valueName)
        {
            if (field == null || !node.HasValue(valueName))
                return;

            if (TryParseFloat(node.GetValue(valueName), out var value))
                field.SetValue(target, value);
        }

        private static void ApplyDouble(FieldInfo field, object target, ConfigNode node, string valueName)
        {
            if (field == null || !node.HasValue(valueName))
                return;

            if (TryParseDouble(node.GetValue(valueName), out var value))
                field.SetValue(target, value);
        }

        /// <summary>Written as repeated root-level values, not a sub-node, so clear and refill.</summary>
        private static void ApplyScansat(object vesselData, ConfigNode incoming)
        {
            if (KerbalismApi.VdScansatId == null)
                return;

            var values = incoming.GetValues("scansat_id");
            if (values == null)
                return;

            var list = KerbalismApi.VdScansatId.GetValue(vesselData) as IList;
            if (list == null)
                return;

            list.Clear();
            foreach (var value in values)
            {
                if (uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    list.Add(id);
            }
        }

        private static void ApplyStorm(object vesselData, ConfigNode incoming)
        {
            var byStarNode = incoming.GetNode("StormDataByStar");
            if (byStarNode != null && KerbalismApi.VdStormDataByStar != null)
            {
                var map = KerbalismApi.VdStormDataByStar.GetValue(vesselData) as IDictionary;
                if (map != null)
                {
                    foreach (var child in byStarNode.GetNodes())
                    {
                        if (map.Contains(child.name))
                            ApplyStormData(map[child.name], child);
                        else
                            map.Add(child.name, BuildStormData(child));
                    }
                }
            }

            //stormData aliases one entry of the map, so re-point it at whatever the map now holds
            var single = incoming.GetNode("StormData");
            if (single != null && KerbalismApi.VdStormData != null)
            {
                var current = KerbalismApi.VdStormData.GetValue(vesselData);
                if (current == null)
                    KerbalismApi.VdStormData.SetValue(vesselData, BuildStormData(single));
                else
                    ApplyStormData(current, single);
            }
        }

        private static object BuildStormData(ConfigNode node)
        {
            if (KerbalismApi.StormDataCtor == null)
                return null;

            var stormData = KerbalismApi.StormDataCtor.Invoke(new object[] { node });
            return stormData;
        }

        private static void ApplyStormData(object stormData, ConfigNode node)
        {
            if (stormData == null)
                return;

            SetIfPresent(node, "storm_time", KerbalismApi.StormTime, stormData, ParseDouble);
            SetIfPresent(node, "storm_duration", KerbalismApi.StormDuration, stormData, ParseDouble);
            SetIfPresent(node, "storm_generation", KerbalismApi.StormGeneration, stormData, ParseUInt);
            SetIfPresent(node, "storm_state", KerbalismApi.StormState, stormData, ParseInt);
            SetIfPresent(node, "msg_storm", KerbalismApi.StormMsgShown, stormData, ParseBool);
            SetIfPresent(node, "display_warning", KerbalismApi.StormDisplayWarning, stormData, ParseBool);
            SetIfPresent(node, "displayed_duration", KerbalismApi.StormDisplayedDuration, stormData, ParseDouble);
        }

        /// <summary>The string "residualKw,refK", so it goes through Kerbalism's own deserializer.</summary>
        private static void ApplySystemHeat(object vesselData, ConfigNode incoming)
        {
            var loopsNode = incoming.GetNode("SystemHeatLoops");
            if (loopsNode == null || KerbalismApi.VdSystemHeatLoops == null || KerbalismApi.ShlTryDeserialize == null)
                return;

            var map = KerbalismApi.VdSystemHeatLoops.GetValue(vesselData) as IDictionary;
            if (map == null)
                return;

            foreach (ConfigNode.Value value in loopsNode.values)
            {
                if (string.IsNullOrEmpty(value.name))
                    continue;

                if (!int.TryParse(value.name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var loopId))
                    continue;

                var args = new[] { value.value, null };
                if (!(bool)KerbalismApi.ShlTryDeserialize.Invoke(null, args) || args[1] == null)
                    continue;

                //Keyed by int, so the map has to grow when the key set differs
                if (map.Contains(loopId))
                    map[loopId] = args[1];
                else
                    map.Add(loopId, args[1]);
            }
        }

        /// <summary>Keyed by Process reference, so valves go through their mandatory ctor, not CreateInstance.</summary>
        private static void ApplyDumpValves(object vesselData, ConfigNode incoming)
        {
            var node = incoming.GetNode("dump_specs");
            if (node == null || KerbalismApi.VdDumpValves == null ||
                KerbalismApi.ProfileProcesses == null || KerbalismApi.ActiveValveCtor == null)
                return;

            var map = KerbalismApi.VdDumpValves.GetValue(vesselData) as IDictionary;
            var processes = KerbalismApi.ProfileProcesses.GetValue(null) as IEnumerable;
            if (map == null || processes == null)
                return;

            var byName = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var process in processes)
            {
                if (process == null || KerbalismApi.ProcessName == null)
                    continue;

                var name = KerbalismApi.ProcessName.GetValue(process) as string;
                if (!string.IsNullOrEmpty(name))
                    byName[name] = process;
            }

            foreach (ConfigNode.Value value in node.values)
            {
                if (!byName.TryGetValue(value.name, out var process) ||
                    !int.TryParse(value.value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var valveIndex))
                    continue;

                var dump = KerbalismApi.ProcessDump.GetValue(process);
                if (dump == null)
                    continue;

                var valve = KerbalismApi.ActiveValveCtor.Invoke(new[] { dump });
                var valveIndexProperty = KerbalismApi.ActiveValveType.GetProperty("ValveIndex");
                valveIndexProperty.SetValue(valve, valveIndex, null);

                if (map.Contains(value.name))
                    map[value.name] = valve;
                else
                    map.Add(value.name, valve);
            }
        }

        #endregion

        #region Parsing helpers

        internal static bool TryParseBool(string raw, out bool value)
        {
            //bool.TryParse rejects 1/0, and KSP configs are full of them
            if (bool.TryParse(raw, out value))
                return true;

            switch (raw)
            {
                case "1": value = true; return true;
                case "0": value = false; return true;
                default: value = false; return false;
            }
        }

        internal static bool TryParseDouble(string raw, out double value) =>
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value) && !double.IsInfinity(value);

        internal static bool TryParseFloat(string raw, out float value) =>
            float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);

        private static double? ParseDouble(string raw) => TryParseDouble(raw, out var value) ? value : (double?)null;
        private static float? ParseFloat(string raw) => TryParseFloat(raw, out var value) ? value : (float?)null;
        private static bool? ParseBool(string raw) => TryParseBool(raw, out var value) ? value : (bool?)null;
        private static uint? ParseUInt(string raw) =>
            uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : (uint?)null;
        private static int? ParseInt(string raw) =>
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : (int?)null;

        private static void SetIfPresent<T>(ConfigNode node, string valueName, FieldInfo field, object target, Func<string, T?> parse)
            where T : struct
        {
            if (field == null || !node.HasValue(valueName))
                return;

            var parsed = parse(node.GetValue(valueName));
            if (parsed.HasValue)
                field.SetValue(target, parsed.Value);
        }

        private static void Try(ApplyReport report, string step, Action action)
        {
            try
            {
                action();
                report.Applied.Add(step);
            }
            catch (Exception e)
            {
                report.Skipped.Add($"{step}: {e.GetType().Name} {e.Message}");
                LunaLog.LogWarning($"[Kerbalism] Could not apply '{step}' from a remote state: {e.GetType().Name} {e.Message}");
            }
        }

        #endregion
    }

    /// <summary>What an <see cref="KerbalismVesselState.Apply"/> call managed to do.</summary>
    public sealed class ApplyReport
    {
        public readonly List<string> Applied = new List<string>();
        public readonly List<string> Skipped = new List<string>();

        public bool AnythingFailed => Skipped.Count > 0;
    }
}
