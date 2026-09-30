using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LmpClient;

namespace KerbalismSync.Client
{
    /// <summary>
    /// Cached reflection bindings to Kerbalism. There is no compile-time reference on purpose: the
    /// plugin must load on a machine without Kerbalism. Every lookup happens once in
    /// <see cref="TryResolve"/>, which fails cleanly and disables the plugin if a future Kerbalism
    /// version renames something.
    /// </summary>
    public static class KerbalismApi
    {
        
        private const string Namespace = "KERBALISM";

        public static bool Available { get; private set; }

        
        public static Type VesselDataType { get; private set; }
        public static Type ComputerType { get; private set; }
        public static Type StormDataType { get; private set; }
        public static Type SystemHeatLoopCalibrationType { get; private set; }
        public static Type PartDataType { get; private set; }
        public static Type DriveType { get; private set; }
        public static Type FileType { get; private set; }
        public static Type SampleType { get; private set; }
        public static Type SubjectDataType { get; private set; }

        
        public static MethodInfo TryGetVesselDataProto;      // bool TryGetVesselData(this ProtoVessel, out VesselData)
        public static MethodInfo KerbalismDataProto;        // VesselData KerbalismData(this ProtoVessel)

        
        public static MethodInfo VesselDataSave;            // void Save(ConfigNode)
        public static FieldInfo VdComputer;
        public static FieldInfo VdDeviceTransmit;
        public static FieldInfo VdScienceTransmitted;
        public static FieldInfo VdScansatId;
        public static FieldInfo VdStormData;
        public static FieldInfo VdStormDataByStar;
        public static FieldInfo VdSystemHeatLoops;
        public static FieldInfo VdDumpValves;
        public static FieldInfo VdIsGroundCtrl;
        public static FieldInfo VdSpinSnapshotValid;
        public static FieldInfo VdSpinQualifyingCapacity;
        public static FieldInfo VdSpinSampleMinArtificialG;
        public static FieldInfo VdSpinRpm;
        public static FieldInfo VdSolarPanelsAverageExposure;
        public static PropertyInfo VdPartDatas;            // Dictionary<uint, PartData>.ValueCollection
        public static MethodInfo VdGetPartData;            // PartData GetPartData(uint)
        public static PropertyInfo VdHabitatInfo;

        
        public static ConstructorInfo ComputerCtor;         // Computer(ConfigNode)
        public static MethodInfo ComputerSave;             // void Save(ConfigNode)

        
        public static FieldInfo StormTime, StormDuration, StormGeneration, StormState,
                               StormMsgShown, StormDisplayWarning, StormDisplayedDuration;
        public static ConstructorInfo StormDataCtor;        // StormData(ConfigNode)
        public static MethodInfo StormDataSave;

        
        public static MethodInfo ShlSerialize;              // string Serialize()
        public static MethodInfo ShlTryDeserialize;        // bool TryDeserialize(string, out ...)

        
        public static FieldInfo ProfileProcesses;          // static List<Process>
        public static FieldInfo ProcessName;
        public static FieldInfo ProcessDump;
        public static Type ActiveValveType;
        public static ConstructorInfo ActiveValveCtor;      // ActiveValve(DumpSpecs)

        
        public static PropertyInfo PartDataDrive;
        public static PropertyInfo PartDataFlightId;
        public static FieldInfo DriveFiles;
        public static FieldInfo DriveSamples;
        public static FieldInfo DriveFileSendFlags;
        public static FieldInfo DriveName;
        public static FieldInfo DriveIsPrivate;
        public static FieldInfo DriveDataCapacity;
        public static FieldInfo DriveSampleCapacity;
        public static MethodInfo DriveSave;                // void Save(ConfigNode)
        public static ConstructorInfo DriveCtor;           // Drive(ConfigNode)

        
        public static MethodInfo FileLoad;             // static File Load(string, ConfigNode)
        public static MethodInfo SampleLoad;           // static Sample Load(string, ConfigNode)
        public static FieldInfo FileSize, FileResultText, FileUseStockCrediting, FileSubjectData;
        public static FieldInfo SampleSize, SampleResultText, SampleUseStockCrediting,
                                   SampleSubjectData, SampleAnalyze, SampleMass;

        
        public static MethodInfo SubjectAddInFlight;       // void AddDataCollectedInFlight(double)
        public static MethodInfo SubjectRemoveInFlight;    // void RemoveDataCollectedInFlight(double)
        public static MethodInfo ScienceDbGetSubjectData;  // static SubjectData GetSubjectData(string)
        public static MethodInfo ScienceDbGetFromStockId;  // static SubjectData GetSubjectDataFromStockId(string, ...)

        private static PropertyInfo _isSaveGameInitDone;

        /// <summary>Set before DB.Load runs, so necessary but not sufficient; the per-vessel lookup is the real proof.</summary>
        public static bool IsSaveGameReady =>
            _isSaveGameInitDone != null && (bool)_isSaveGameInitDone.GetValue(null, null);

        /// <summary>Locates Kerbalism and caches every member used. False if absent or the layout changed.</summary>
        public static bool TryResolve()
        {
            if (Available)
                return true;

            try
            {
                var assembly = FindAssemblyWithType(Namespace + ".DB");
                if (assembly == null)
                    return false;

                var kerbalism = assembly.GetType(Namespace + ".Kerbalism");
                if (kerbalism != null)
                    _isSaveGameInitDone = kerbalism.GetProperty("IsSaveGameInitDone", BindingFlags.Public | BindingFlags.Static);

                var db = assembly.GetType(Namespace + ".DB");
                VesselDataType = assembly.GetType(Namespace + ".VesselData");
                ComputerType = assembly.GetType(Namespace + ".Computer");
                StormDataType = assembly.GetType(Namespace + ".StormData");
                SystemHeatLoopCalibrationType = assembly.GetType(Namespace + ".SystemHeatLoopCalibration");
                PartDataType = assembly.GetType(Namespace + ".PartData");
                DriveType = assembly.GetType(Namespace + ".Drive");
                FileType = assembly.GetType(Namespace + ".File");
                SampleType = assembly.GetType(Namespace + ".Sample");
                SubjectDataType = assembly.GetType(Namespace + ".SubjectData");

                if (db == null || VesselDataType == null || ComputerType == null)
                    return false;

                //DB extension methods
                TryGetVesselDataProto = db.GetMethod("TryGetVesselData", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(ProtoVessel), VesselDataType.MakeByRefType() }, null);
                KerbalismDataProto = db.GetMethod("KerbalismData", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(ProtoVessel) }, null);

                VesselDataSave = VesselDataType.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(ConfigNode) }, null);

                VdComputer = Field(VesselDataType, "computer");
                VdDeviceTransmit = Field(VesselDataType, "deviceTransmit");
                VdScienceTransmitted = Field(VesselDataType, "scienceTransmitted");
                VdScansatId = Field(VesselDataType, "scansat_id");
                VdStormData = Field(VesselDataType, "stormData");
                VdStormDataByStar = Field(VesselDataType, "stormDataByStar");
                VdSystemHeatLoops = Field(VesselDataType, "systemHeatLoops");
                VdDumpValves = Field(VesselDataType, "dumpValves");
                VdIsGroundCtrl = Field(VesselDataType, "isSerenityGroundController");
                VdSpinSnapshotValid = Field(VesselDataType, "spinSnapshotValid");
                VdSpinQualifyingCapacity = Field(VesselDataType, "spinQualifyingCapacity");
                VdSpinSampleMinArtificialG = Field(VesselDataType, "spinSampleMinArtificialG");
                VdSpinRpm = Field(VesselDataType, "spinRpm");
                VdSolarPanelsAverageExposure = Field(VesselDataType, "solarPanelsAverageHighWarpExposure");
                VdPartDatas = Property(VesselDataType, "PartDatas");
                VdGetPartData = VesselDataType.GetMethod("GetPartData", BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(uint) }, null);
                VdHabitatInfo = Property(VesselDataType, "EnvHabitatInfo");

                ComputerCtor = ComputerType.GetConstructor(new[] { typeof(ConfigNode) });
                ComputerSave = ComputerType.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(ConfigNode) }, null);

                if (StormDataType != null)
                {
                    StormTime = Field(StormDataType, "storm_time");
                    StormDuration = Field(StormDataType, "storm_duration");
                    StormGeneration = Field(StormDataType, "storm_generation");
                    StormState = Field(StormDataType, "storm_state");
                    StormMsgShown = Field(StormDataType, "msg_storm");
                    StormDisplayWarning = Field(StormDataType, "display_warning");
                    StormDisplayedDuration = Field(StormDataType, "displayed_duration");
                    StormDataSave = StormDataType.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(ConfigNode) }, null);
                }

                if (SystemHeatLoopCalibrationType != null)
                {
                    ShlSerialize = SystemHeatLoopCalibrationType.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Instance);
                    ShlTryDeserialize = SystemHeatLoopCalibrationType.GetMethod("TryDeserialize", BindingFlags.Public | BindingFlags.Static);
                }

                //ActiveValve's ctor requires the DumpSpecs instance or every property access NREs
                var dumpSpecs = assembly.GetType(Namespace + ".DumpSpecs");
                ActiveValveType = dumpSpecs?.GetNestedType("ActiveValve");
                if (ActiveValveType != null && dumpSpecs != null)
                    ActiveValveCtor = ActiveValveType.GetConstructor(new[] { dumpSpecs });

                var profile = assembly.GetType(Namespace + ".Profile");
                ProfileProcesses = profile?.GetField("processes", BindingFlags.Public | BindingFlags.Static);
                var processType = assembly.GetType(Namespace + ".Process");
                if (processType != null)
                {
                    ProcessName = Field(processType, "name");
                    ProcessDump = Field(processType, "dump");
                }

                if (PartDataType != null)
                {
                    PartDataDrive = Property(PartDataType, "Drive");
                    PartDataFlightId = Property(PartDataType, "FlightId");
                }

                if (DriveType != null)
                {
                    DriveFiles = Field(DriveType, "files");
                    DriveSamples = Field(DriveType, "samples");
                    DriveFileSendFlags = Field(DriveType, "fileSendFlags");
                    DriveName = Field(DriveType, "name");
                    DriveIsPrivate = Field(DriveType, "is_private");
                    DriveDataCapacity = Field(DriveType, "dataCapacity");
                    DriveSampleCapacity = Field(DriveType, "sampleCapacity");
                    DriveSave = DriveType.GetMethod("Save", BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(ConfigNode) }, null);
                    DriveCtor = DriveType.GetConstructor(new[] { typeof(ConfigNode) });
                }

                if (FileType != null)
                {
                    FileLoad = FileType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
                    FileSize = Field(FileType, "size");
                    FileResultText = Field(FileType, "resultText");
                    FileUseStockCrediting = Field(FileType, "useStockCrediting");
                    FileSubjectData = Field(FileType, "subjectData");
                }

                if (SampleType != null)
                {
                    SampleLoad = SampleType.GetMethod("Load", BindingFlags.Public | BindingFlags.Static);
                    SampleSize = Field(SampleType, "size");
                    SampleResultText = Field(SampleType, "resultText");
                    SampleUseStockCrediting = Field(SampleType, "useStockCrediting");
                    SampleSubjectData = Field(SampleType, "subjectData");
                    SampleAnalyze = Field(SampleType, "analyze");
                    SampleMass = Field(SampleType, "mass");
                }

                if (SubjectDataType != null)
                {
                    SubjectAddInFlight = SubjectDataType.GetMethod("AddDataCollectedInFlight", BindingFlags.Public | BindingFlags.Instance);
                    SubjectRemoveInFlight = SubjectDataType.GetMethod("RemoveDataCollectedInFlight", BindingFlags.Public | BindingFlags.Instance);
                }

                var scienceDb = assembly.GetType(Namespace + ".ScienceDB");
                if (scienceDb != null)
                {
                    ScienceDbGetSubjectData = scienceDb.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "GetSubjectData" && m.GetParameters().Length == 1);
                }

                //Without these nothing can be synced at all
                if (TryGetVesselDataProto == null || VesselDataSave == null ||
                    ComputerCtor == null || VdComputer == null || VdDeviceTransmit == null)
                {
                    Reset();
                    return false;
                }

                Available = true;
                return true;
            }
            catch (Exception e)
            {
                Reset();
                LunaLog.LogError($"[Kerbalism] Could not bind to Kerbalism, disabling Kerbalism sync: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Which optional pieces are missing. Integration modules are compiled unconditionally, so
        /// an install may legitimately lack SystemHeat, CryoTanks and friends; that must degrade to
        /// "not synced" rather than disable the plugin.
        /// </summary>
        public static string DescribeOptionalGaps()
        {
            var gaps = new List<string>();

            if (StormDataType == null || StormDataSave == null) gaps.Add("StormData");
            if (SystemHeatLoopCalibrationType == null || ShlSerialize == null) gaps.Add("SystemHeatLoopCalibration");
            if (VdDumpValves == null || ProfileProcesses == null || ActiveValveCtor == null) gaps.Add("DumpValves");
            if (DriveType == null || DriveSave == null) gaps.Add("Drives");
            if (VdHabitatInfo == null) gaps.Add("SunShielding");

            return gaps.Count == 0 ? "none" : string.Join(", ", gaps);
        }

        /// <summary>
        /// A vessel's data, or null if Kerbalism has none yet. Does not create the entry, which would
        /// make the plugin the author of state the server may already hold.
        /// </summary>
        public static object GetVesselData(ProtoVessel protoVessel)
        {
            if (!Available || protoVessel == null)
                return null;

            try
            {
                var args = new object[] { protoVessel, null };
                var found = (bool)TryGetVesselDataProto.Invoke(null, args);
                return found ? args[1] : null;
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[Kerbalism] VesselData lookup failed for {protoVessel.vesselID}: {e.Message}");
                return null;
            }
        }

        public static object GetOrCreateVesselData(ProtoVessel protoVessel)
        {
            var existing = GetVesselData(protoVessel);
            if (existing != null)
                return existing;

            try
            {
                return KerbalismDataProto.Invoke(null, new object[] { protoVessel });
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[Kerbalism] Could not create VesselData for {protoVessel.vesselID}: {e.Message}");
                return null;
            }
        }

        private static Assembly FindAssemblyWithType(string fullTypeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.GetType(fullTypeName, false) != null)
                        return assembly;
                }
                catch (Exception)
                {
                    
                }
            }

            return null;
        }

        private static FieldInfo Field(Type type, string name)
        {
            if (type == null)
                return null;

            //Gameplay state sits in private fields too, so look at everything declared and inherited
            var field = type.GetField(name,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

            return field ?? (type.BaseType != null && type.BaseType != typeof(object)
                ? Field(type.BaseType, name)
                : null);
        }

        private static PropertyInfo Property(Type type, string name)
        {
            if (type == null)
                return null;

            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return property ?? (type.BaseType != null && type.BaseType != typeof(object)
                ? Property(type.BaseType, name)
                : null);
        }

        private static void Reset()
        {
            Available = false;
            VesselDataType = null;
            ComputerType = null;
            StormDataType = null;
            DriveType = null;

            TryGetVesselDataProto = null;
            VesselDataSave = null;
            ComputerCtor = null;

            VdComputer = null;
            VdDeviceTransmit = null;
            VdScienceTransmitted = null;
        }
    }
}
