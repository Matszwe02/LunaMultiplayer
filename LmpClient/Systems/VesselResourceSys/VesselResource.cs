using System;
using System.Collections.Generic;
using LmpClient;
using LmpClient.Extensions;
using LmpClient.VesselUtilities;
using LmpCommon.Message.Data.Vessel;

namespace LmpClient.Systems.VesselResourceSys
{
    /// <summary>
    /// Class that maps a message class to a system class. This way we avoid the message caching issues
    /// </summary>
    public class VesselResource
    {
        #region Fields and Properties

        public double GameTime;
        public Guid VesselId;
        public int ResourcesCount;
        public VesselResourceInfo[] Resources = new VesselResourceInfo[0];

        private const int WarningThrottleSeconds = 30;
        private static readonly Dictionary<string, (DateTime LastLoggedUtc, int Suppressed)> RecentWarnings =
            new Dictionary<string, (DateTime, int)>();

        #endregion

        /// <summary>
        /// Logs a warning at most once every <see cref="WarningThrottleSeconds"/> per key.
        /// Resource updates arrive every 2.5s per vessel, so a single unmatched part used to
        /// flood the log with one line per resource; the suppressed count is appended instead.
        /// </summary>
        private static void LogThrottled(string key, string message)
        {
            var now = DateTime.UtcNow;
            lock (RecentWarnings)
            {
                if (RecentWarnings.Count > 1000)
                    RecentWarnings.Clear();

                if (RecentWarnings.TryGetValue(key, out var entry) && (now - entry.LastLoggedUtc).TotalSeconds < WarningThrottleSeconds)
                {
                    RecentWarnings[key] = (entry.LastLoggedUtc, entry.Suppressed + 1);
                    return;
                }

                var suppressed = RecentWarnings.TryGetValue(key, out var oldEntry) ? oldEntry.Suppressed : 0;
                RecentWarnings[key] = (now, 0);

                LunaLog.LogWarning(suppressed > 0
                    ? $"{message} (suppressed {suppressed} identical messages in the last {WarningThrottleSeconds}s)"
                    : message);
            }
        }

        public void ProcessVesselResource()
        {
            var vessel = FlightGlobals.FindVessel(VesselId);
            if (vessel == null) return;

            if (!VesselCommon.DoVesselChecks(vessel.id))
                return;

            UpdateVesselFields(vessel);
        }

        private void UpdateVesselFields(Vessel vessel)
        {
            if (vessel.protoVessel == null) return;

            for (var i = 0; i < ResourcesCount; i++)
            {
                if (Resources == null || i >= Resources.Length || Resources[i] == null)
                {
                    LunaLog.LogWarning(
                        $"[LMP]: Skipping ProtoPart resource write due to failure to match (vessel {VesselId}, index {i}, reason: invalid resource entry).");
                    continue;
                }

                var partSnapshot = vessel.protoVessel.GetProtoPart(Resources[i].PartFlightId);

                //The part may have been removed (e.g. destroyed by a collision) while this update was in flight, skip it
                if (partSnapshot == null)
                {
                    if (Resources[i].PartFlightId == 0)
                    {
                        LogThrottled($"zeropart-{VesselId}",
                            $"[LMP]: Skipping ProtoPart resource write for vessel {VesselId} ({vessel.vesselName}), resource '{Resources[i].ResourceName}' - " +
                            "the sender's vessel proto contains a part with flightID 0 (broken proto on the sending client). " +
                            "Waiting for the next full vessel update to recover.");
                    }
                    else
                    {
                        LogThrottled($"part-{VesselId}-{Resources[i].PartFlightId}",
                            $"[LMP]: Skipping ProtoPart resource write due to failure to match (vessel {VesselId} ({vessel.vesselName}), " +
                            $"part {Resources[i].PartFlightId}, resource '{Resources[i].ResourceName}', reason: proto part not found).");
                    }
                    continue;
                }

                var resourceSnapshot = partSnapshot.FindResourceInProtoPart(Resources[i].ResourceName);
                if (resourceSnapshot == null)
                {
                    LogThrottled($"resource-{VesselId}-{Resources[i].PartFlightId}-{Resources[i].ResourceName}",
                        $"[LMP]: Skipping ProtoPart resource write due to failure to match (vessel {VesselId} ({vessel.vesselName}), " +
                        $"part {Resources[i].PartFlightId}, resource '{Resources[i].ResourceName}', reason: proto resource not found).");
                    continue;
                }

                resourceSnapshot.amount = Resources[i].Amount;
                resourceSnapshot.flowState = Resources[i].FlowState;

                //Using "resourceSnapshot.resourceRef" sometimes returns null so we also try to get the resource from the part...
                if (resourceSnapshot.resourceRef == null)
                {
                    if (partSnapshot.partRef != null)
                    {
                        var foundResource = partSnapshot.partRef.FindResource(resourceSnapshot.resourceName);

                        //The resource may no longer exist on the part (e.g. the part is being destroyed by a collision)
                        if (foundResource != null)
                        {
                            foundResource.amount = Resources[i].Amount;
                            foundResource.flowState = Resources[i].FlowState;
                        }
                        else
                        {
                            LogThrottled($"live-{VesselId}-{Resources[i].PartFlightId}-{resourceSnapshot.resourceName}",
                                $"[LMP]: Skipping ProtoPart resource write due to failure to match (vessel {VesselId} ({vessel.vesselName}), " +
                                $"part {Resources[i].PartFlightId}, resource '{resourceSnapshot.resourceName}', reason: live resource not found on part).");
                        }
                    }
                }
                else
                {
                    resourceSnapshot.resourceRef.amount = Resources[i].Amount;
                    resourceSnapshot.resourceRef.flowState = Resources[i].FlowState;
                }
            }
        }
    }
}
