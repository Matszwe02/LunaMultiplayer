using LmpClient.Extensions;
using LmpClient.ModuleStore.Structures;
using LmpClient.Utilities;
using LmpCommon.Xml;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace LmpClient.ModuleStore
{
    /// <summary>
    /// This storage class stores all the fields that have the "ispersistent" as true. And also the customizations to the part modules
    /// When we run trough all the part modules looking for changes we will get the fields to check from this class.
    /// Also we will use the customization methods to act accordingly
    /// </summary>
    public class FieldModuleStore
    {
        private static readonly string CustomPartSyncFolder = CommonUtil.CombinePaths(MainSystem.KspPath, "GameData", "LunaMultiplayer", "PartSync");

        /// <summary>
        /// Here we store our customized part modules behaviors
        /// </summary>
        public static Dictionary<string, ModuleDefinition> CustomizedModuleBehaviours = new Dictionary<string, ModuleDefinition>();

        /// <summary>
        /// Here we store all the types that inherit from PartModule including the mod files
        /// </summary>
        private static IEnumerable<Type> PartModuleTypes { get; } = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetLoadableTypes())
            .Where(t => t.IsClass && t.IsSubclassOf(typeof(PartModule)));

        /// <summary>
        /// Reads the module customizations xml.
        /// Basically it fills up the CustomizedModuleBehaviours dictionary
        /// </summary>
        public static void ReadCustomizationXml()
        {
            var moduleValues = new List<ModuleDefinition>();

            //Called from MainSystem.Awake() with no try above it, so throwing here aborts LMP
            //startup entirely - no base events, no Harmony patches, no network. A third-party mod
            //shipping a malformed definition must never be able to do that, so this degrades.
            if (!Directory.Exists(CustomPartSyncFolder))
            {
                LunaLog.LogWarning($"[LMP] No part sync folder at '{CustomPartSyncFolder}'. " +
                                   $"Part module field sync is limited to the always-loaded built-in definitions.");
                CustomizedModuleBehaviours = new Dictionary<string, ModuleDefinition>();
                return;
            }

            var skippedFiles = new List<string>();
            foreach (var file in Directory.GetFiles(CustomPartSyncFolder, "*.xml", SearchOption.AllDirectories))
            {
                try
                {
                    var moduleDefinition = LunaXmlSerializer.ReadXmlFromPath<ModuleDefinition>(file);
                    moduleDefinition.ModuleName = Path.GetFileNameWithoutExtension(file);

                    moduleValues.Add(moduleDefinition);
                }
                catch (Exception e)
                {
                    LunaLog.LogError($"[LMP] Could not read part sync definition '{file}', skipping it: {e.Message}");
                    skippedFiles.Add(Path.GetFileName(file));
                }
            }

            //Two mods can ship the same file name. First wins: ToDictionary threw on the duplicate
            //and took the whole client down with it.
            var duplicates = moduleValues.GroupBy(m => m.ModuleName).Where(g => g.Count() > 1).ToList();
            foreach (var duplicate in duplicates)
            {
                LunaLog.LogWarning($"[LMP] {duplicate.Count()} part sync definitions are named '{duplicate.Key}'. " +
                                   $"They target part modules by class name, so only the first can ever apply. " +
                                   $"Rename the extras; ignoring: {string.Join(", ", duplicate.Skip(1).Select(m => m.ModuleName))}");
            }

            CustomizedModuleBehaviours = moduleValues
                .GroupBy(m => m.ModuleName)
                .ToDictionary(g => g.Key, g => g.First());

            var newChildModulesToAdd = new List<ModuleDefinition>();
            foreach (var value in CustomizedModuleBehaviours.Values)
            {
                var moduleClass = PartModuleTypes.FirstOrDefault(t => t.Name == value.ModuleName);
                if (moduleClass != null)
                {
                    AddParentsCustomizations(value, moduleClass);
                    newChildModulesToAdd.AddRange(GetChildCustomizations(value, moduleClass));
                }
            }

            foreach (var moduleToAdd in newChildModulesToAdd)
            {
                if (!CustomizedModuleBehaviours.ContainsKey(moduleToAdd.ModuleName))
                    CustomizedModuleBehaviours.Add(moduleToAdd.ModuleName, moduleToAdd);
            }

            foreach (var module in CustomizedModuleBehaviours.Values)
                module.Init();

            //Without a summary, a definition that resolves to nothing looks exactly like one
            //that is working.
            var resolvedModules = CustomizedModuleBehaviours.Values.Count(v => PartModuleTypes.Any(t => t.Name == v.ModuleName));
            var totalFields = CustomizedModuleBehaviours.Values.Sum(v => v.CustomizedFields.Count);
            var totalMethods = CustomizedModuleBehaviours.Values.Sum(v => v.CustomizedMethods.Count);

            LunaLog.Log($"[LMP] Part sync definitions: {CustomizedModuleBehaviours.Count} loaded, " +
                        $"{resolvedModules} matched an installed part module, " +
                        $"{totalFields} field(s), {totalMethods} method(s)" +
                        (skippedFiles.Count > 0 ? $", {skippedFiles.Count} file(s) skipped ({string.Join(", ", skippedFiles)})" : string.Empty));
        }

        /// <summary>
        /// Here we return the UNEXISTING CHILD customizations of this moduleDefinition.
        /// Example: ModuleDeployableAntenna inherits from ModuleDeployablePart.
        /// But we don't have ANY ModuleDeployableAntenna customization XML.
        /// Here we return a NEW the ModuleDeployableAntenna XML with the customizations of ModuleDeployablePart
        /// </summary>
        private static List<ModuleDefinition> GetChildCustomizations(ModuleDefinition moduleDefinition, Type moduleClass)
        {
            var newPartModules = new List<ModuleDefinition>();
            foreach (var partModuleType in PartModuleTypes.Where(t => t.BaseType == moduleClass))
            {
                if (!CustomizedModuleBehaviours.ContainsKey(partModuleType.Name))
                {
                    newPartModules.Add(new ModuleDefinition
                    {
                        ModuleName = partModuleType.Name,
                        Fields = moduleDefinition.Fields,
                        Methods = moduleDefinition.Methods
                    });
                }
            }

            return newPartModules;
        }

        /// <summary>
        /// Here we add the PARENT customizations to this moduleDefinition.
        /// Example: ModuleDeployableSolarPanel inherits from ModuleDeployablePart.
        /// Here we add the fields and methods of ModuleDeployablePart into the ModuleDeployableSolarPanel customizations
        /// </summary>
        private static void AddParentsCustomizations(ModuleDefinition moduleDefinition, Type moduleClass)
        {
            if (moduleClass.BaseType == null || moduleClass.BaseType == typeof(MonoBehaviour)) return;

            if (CustomizedModuleBehaviours.TryGetValue(moduleClass.BaseType.Name, out var parentModule))
            {
                moduleDefinition.MergeWith(parentModule);
            }

            AddParentsCustomizations(moduleDefinition, moduleClass.BaseType);
        }
    }
}
