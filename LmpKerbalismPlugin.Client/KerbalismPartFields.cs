using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace KerbalismSync.Client
{
    /// <summary>How a part module field is transported.</summary>
    public enum FieldKind
    {
        Bool,
        Int,
        UInt,
        Float,
        Double,
        String,
        Enum
    }

    public sealed class FieldSpec
    {
        public FieldSpec(string name, FieldKind kind)
        {
            Name = name;
            Kind = kind;
        }

        public string Name { get; }
        public FieldKind Kind { get; }

        /// <summary>
        /// For fields that change continuously and are only worth shipping occasionally. Without a
        /// throttle, a field rewritten every physics frame becomes a message every frame.
        /// </summary>
        public int MaxIntervalMs { get; set; }
    }

    /// <summary>
    /// The Kerbalism part module fields LMP does not already sync, replacing the 21 part-sync XMLs
    /// the previous approach used: those relied on the transpiler noticing a write, which covers few
    /// method names and so missed a dozen Kerbalism write paths.
    /// <para>
    /// Only <c>[KSPField(isPersistant = true)]</c> fields belong here, since LMP's receiver writes the
    /// persisted config node. Cross-references to other parts (<c>*ModuleID</c>) are excluded: they
    /// must match the local part config, not the sender's.
    /// </para>
    /// </summary>
    public static class KerbalismPartFields
    {
        private static FieldSpec S(string name) => new FieldSpec(name, FieldKind.String);
        private static FieldSpec B(string name) => new FieldSpec(name, FieldKind.Bool);
        private static FieldSpec I(string name) => new FieldSpec(name, FieldKind.Int);
        private static FieldSpec U(string name) => new FieldSpec(name, FieldKind.UInt);
        private static FieldSpec F(string name) => new FieldSpec(name, FieldKind.Float);
        private static FieldSpec D(string name) => new FieldSpec(name, FieldKind.Double);
        private static FieldSpec E(string name) => new FieldSpec(name, FieldKind.Enum);

        private static FieldSpec Throttled(FieldSpec spec, int intervalMs)
        {
            spec.MaxIntervalMs = intervalMs;
            return spec;
        }

        /// <summary>module type name (the class name, which is what LMP matches on) -> fields.</summary>
        public static readonly Dictionary<string, FieldSpec[]> Table = new Dictionary<string, FieldSpec[]>(StringComparer.Ordinal)
        {
            // --- Kerbalism core -------------------------------------------------
            { "Comfort", new[] { S("bonus") } },

            // cfg/prev_cfg are written from OnGUI, which no transpiler can see
            { "Configure", new[] { S("cfg"), S("prev_cfg") } },
            { "Deploy", new[] { B("isBroken") } },

            { "ECDrainViaPM", new[] { S("title"), S("moduleTitle"), S("targetModule"), D("ec_rate"), B("running") } },

            { "Emitter", new[] { B("toggle"), D("radiation"), D("ec_rate"), B("running"), Throttled(D("radiation_impact"), 10000) } },

            { "Experiment", new[]
                {
                    S("issue"),
                    I("situationId"),
                    B("didPrepare"),
                    B("shrouded"),
                    D("remainingSampleMass"),
                    U("privateHdId"),
                    B("firstStart"),
                    Throttled(D("prodFactor"), 10000),
                    Throttled(E("expState"), 10000),
                    Throttled(E("status"), 10000)
                }
            },

            { "ExperimentResultImage", new[] { B("unlocked"), B("observedProducing") } },

            { "GravityRing", new[] { B("deployed") } },

            { "Greenhouse", new[] { B("active"), Throttled(D("natural"), 30000), Throttled(D("artificial"), 30000), S("issue") } },

            { "Habitat", new[] { E("state"), Throttled(D("perctDeployed"), 5000) } },

            { "HardDrive", new[] { U("hdId"), D("effectiveDataCapacity"), I("effectiveSampleCapacity") } },

            { "Harvester", new[] { B("deployed"), B("running"), S("issue"), D("simulated_abundance") } },

            { "KerbalismScansat", new[]
                {
                    I("sensorType"),
                    S("body_name"),
                    Throttled(D("body_coverage"), 30000),
                    Throttled(D("warp_buffer"), 30000),
                    B("power_disabled"),
                    B("storage_disabled")
                }
            },

            { "Laboratory", new[] { B("running") } },

            { "PassiveShield", new[] { D("radiation"), D("ec_rate"), B("deployed") } },

            { "PlannerController", new[] { B("considered") } },

            { "ProcessController", new[] { B("running"), B("broken") } },

            { "Reliability", new[]
                {
                    S("type"),
                    B("broken"),
                    B("critical"),
                    B("quality"),
                    D("last"),
                    D("next"),
                    D("last_inspection"),
                    B("needMaintenance"),
                    B("enforce_breakdown"),
                    B("radiator_state_stored"),
                    B("radiator_was_cooling")
                }
            },

            { "Sensor", new[] { S("type") } },

            { "Sickbay", new[] { S("patients"), B("running") } },

            { "SolarPanelFixer", new[]
                {
                    D("nominalRate"),
                    S("resourceName"),
                    E("state"),
                    I("trackedSunIndex"),
                    B("manualTracking"),
                    Throttled(D("launchUT"), 30000),
                    B("hasPanelOrientation"),
                    D("panelPivotPartLocalX"), D("panelPivotPartLocalY"), D("panelPivotPartLocalZ"),
                    D("panelNormalPartLocalX"), D("panelNormalPartLocalY"), D("panelNormalPartLocalZ"),
                    B("hasFrozenExposure"),
                    Throttled(D("frozenExposure"), 30000),
                    B("editorEnabled")
                }
            },

            // --- Integrations: listed explicitly, since the plugin matches module classes by exact type name
            { "HarvesterSystemHeat", new[] { B("deployed"), B("running"), S("issue") } },

            { "ProcessControllerSystemHeat", new[] { B("running"), B("broken"), B("deployed"), D("CoreDamage"), D("CurrentSafetyOverride") } },

            { "ProcessControllerDeployable", new[] { B("running"), B("broken"), B("deployed") } },

            { "SHRadiatorKerbalism", new[] { D("scale"), D("scaleEmissionPower"), B("IsCooling") } },

            { "SHFissionReactorKerbalismUpdater", new[] { B("FirstLoad"), D("MaxECGeneration"), D("MinThrottle"), D("MaxThrottle") } },

            { "SHFissionEngineKerbalismUpdater", new[] { B("FirstLoad"), D("MaxECGeneration"), D("MinThrottle"), D("MaxThrottle"), B("GeneratesElectricity") } },

            { "FFTFusionReactorKerbalismUpdater", new[] { I("lastReactorModeIndex"), D("MaxECGeneration"), D("MinThrottle") } },

            { "FFTFusionEngineKerbalismUpdater", new[] { I("lastReactorModeIndex"), D("MaxECGeneration"), D("MinThrottle") } },

            { "FFTAntimatterTankKerbalismUpdater", new[] { D("ThermalFluxToAddOnLoad"), D("ecDeficitSeconds") } },

            { "SystemHeatConverterKerbalismUpdater", new FieldSpec[0] },
            { "SystemHeatHarvesterKerbalismUpdater", new FieldSpec[0] },
            { "SpaceDustHarvesterKerbalismUpdater", new FieldSpec[0] },
            { "SystemHeatCryoTankKerbalismUpdater", new FieldSpec[0] },
            { "CryoTankKerbalismUpdater", new FieldSpec[0] },
            { "NFECapacitorKerbalismUpdater", new FieldSpec[0] },

            { "DynamicRadiationController", new[]
                {
                    S("powerModuleName"),
                    I("powerActiveMode"),
                    D("minEmissionPercent"),
                    Throttled(D("emissionDecayRate"), 10000),
                    B("reactorHasStarted"),
                    D("reactorStoppedAt"),
                    D("emitterMaxRadiation"),
                    I("emitterIndex"),
                    B("initialized")
                }
            }
        };

        /// <summary>Cached FieldInfo per (module type, field); resolving by name per comparison would cost more.</summary>
        private static readonly Dictionary<string, Dictionary<string, FieldInfo>> FieldCache =
            new Dictionary<string, Dictionary<string, FieldInfo>>(StringComparer.Ordinal);

        public static FieldInfo ResolveField(Type moduleType, string fieldName)
        {
            if (moduleType == null)
                return null;

            var key = moduleType.FullName;
            if (!FieldCache.TryGetValue(key, out var fields))
            {
                fields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
                FieldCache[key] = fields;
            }

            if (fields.TryGetValue(fieldName, out var cached))
                return cached;

            //Gameplay state sits in private fields too, so look at everything declared and inherited
            var info = moduleType.GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

            fields[fieldName] = info;
            return info;
        }

        /// <summary>
        /// Renders a field as the string LMP's part sync persists, using invariant culture so two
        /// clients with different locales agree. False if unreadable or of an unhandled type.
        /// </summary>
        public static bool TryRead(PartModule module, FieldSpec spec, out string value)
        {
            value = null;

            var field = ResolveField(module.GetType(), spec.Name);
            if (field == null)
                return false;

            object raw;
            try
            {
                raw = field.GetValue(module);
            }
            catch (Exception)
            {
                return false;
            }

            if (raw == null)
            {

                return false;
            }

            switch (spec.Kind)
            {
                case FieldKind.Bool:
                    value = (bool)raw ? "True" : "False";
                    return true;
                case FieldKind.Int:
                    value = ((int)raw).ToString(CultureInfo.InvariantCulture);
                    return true;
                case FieldKind.UInt:
                    value = ((uint)raw).ToString(CultureInfo.InvariantCulture);
                    return true;
                case FieldKind.Float:
                    value = ((float)raw).ToString("R", CultureInfo.InvariantCulture);
                    return true;
                case FieldKind.Double:
                    value = ((double)raw).ToString("R", CultureInfo.InvariantCulture);
                    return true;
                case FieldKind.String:
                    value = (string)raw;
                    return true;
                case FieldKind.Enum:
                    value = raw.ToString();
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Declared field count, for the startup log so a player can confirm what is watched.</summary>
        public static int DeclaredFieldCount
        {
            get
            {
                var count = 0;
                foreach (var pair in Table)
                    count += pair.Value.Length;
                return count;
            }
        }
    }
}
