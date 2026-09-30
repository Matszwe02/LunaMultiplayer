using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using LmpClient;

namespace KerbalismSync.Client
{
    /// <summary>
    /// Applies hard drive files and samples from a remote state. A <c>Drive</c> is keyed by
    /// <c>SubjectData</c> and every entry contributes to a global in-flight counter, so the existing
    /// instance is mutated in place: return the old contributions, clear, repopulate via Kerbalism's
    /// own <c>File.Load</c>/<c>Sample.Load</c>, add each once.
    /// </summary>
    public static class DriveState
    {
        public static void Apply(object vesselData, ConfigNode incoming, ApplyReport report)
        {
            var partsNode = incoming.GetNode("parts");
            if (partsNode == null || KerbalismApi.DriveSave == null)
                return;

            var partDatas = KerbalismApi.VdPartDatas?.GetValue(vesselData) as IEnumerable;
            if (partDatas == null)
                return;

            var byFlightId = new Dictionary<uint, object>();
            foreach (var partData in partDatas)
            {
                if (partData == null || KerbalismApi.PartDataFlightId == null)
                    continue;

                var flightId = KerbalismApi.PartDataFlightId.GetValue(partData);
                if (flightId is uint id)
                    byFlightId[id] = partData;
            }

            foreach (var partNode in partsNode.GetNodes())
            {
                if (!uint.TryParse(partNode.name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var flightId))
                    continue;

                var driveNode = partNode.GetNode("drive");
                if (driveNode == null)
                    continue;

                if (!byFlightId.TryGetValue(flightId, out var partData))
                {
                    report.Skipped.Add($"drive:{flightId}: no local part data for flight id {flightId}");
                    continue;
                }

                try
                {
                    ApplyToDrive(partData, driveNode, flightId, report);
                }
                catch (Exception e)
                {
                    report.Skipped.Add($"drive:{flightId}: {e.GetType().Name} {e.Message}");
                    LunaLog.LogWarning($"[Kerbalism] Could not apply science drive for part {flightId}: {e.GetType().Name} {e.Message}");
                }
            }
        }

        private static void ApplyToDrive(object partData, ConfigNode driveNode, uint flightId, ApplyReport report)
        {
            var drive = KerbalismApi.PartDataDrive.GetValue(partData);

            if (drive == null)
            {
                //No local drive for this part yet; the parameterised ctor has no side effects, unlike Drive(ConfigNode)
                drive = CreateEmptyDrive(driveNode);
                if (drive == null)
                {
                    report.Skipped.Add($"drive:{flightId}: cannot create a drive");
                    return;
                }

                KerbalismApi.PartDataDrive.SetValue(partData, drive);
            }

            var files = KerbalismApi.DriveFiles?.GetValue(drive) as IDictionary;
            var samples = KerbalismApi.DriveSamples?.GetValue(drive) as IDictionary;
            if (files == null || samples == null)
            {
                report.Skipped.Add($"drive:{flightId}: unexpected drive layout");
                return;
            }

            //1. Return the old contributions or the global counter grows on every apply
            RemoveContributions(files, KerbalismApi.FileSize, KerbalismApi.FileSubjectData);
            RemoveContributions(samples, KerbalismApi.SampleSize, KerbalismApi.SampleSubjectData);

            files.Clear();
            samples.Clear();
            (KerbalismApi.DriveFileSendFlags?.GetValue(drive) as IDictionary)?.Clear();

            //2. Scalars first, so capacity/name are right before contents land
            if (driveNode.HasValue("name") && KerbalismApi.DriveName != null)
                KerbalismApi.DriveName.SetValue(drive, driveNode.GetValue("name"));

            if (driveNode.HasValue("is_private") && KerbalismApi.DriveIsPrivate != null &&
                KerbalismVesselState.TryParseBool(driveNode.GetValue("is_private"), out var isPrivate))
                KerbalismApi.DriveIsPrivate.SetValue(drive, isPrivate);

            if (driveNode.HasValue("dataCapacity") && KerbalismApi.DriveDataCapacity != null &&
                KerbalismVesselState.TryParseDouble(driveNode.GetValue("dataCapacity"), out var dataCapacity))
                KerbalismApi.DriveDataCapacity.SetValue(drive, dataCapacity);

            if (driveNode.HasValue("sampleCapacity") && KerbalismApi.DriveSampleCapacity != null &&
                int.TryParse(driveNode.GetValue("sampleCapacity"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleCapacity))
                KerbalismApi.DriveSampleCapacity.SetValue(drive, sampleCapacity);

            //3. Contents, via Kerbalism's resolvers so each client maps ids onto its own subjects
            var filesNode = driveNode.GetNode("files");
            if (filesNode != null)
            {
                foreach (var fileNode in filesNode.GetNodes())
                {
                    var file = KerbalismApi.FileLoad?.Invoke(null, new object[] { fileNode.name, fileNode });
                    if (file == null)
                    {
                        //Subject unknown here: skipping is right when a client has fewer experiments
                        report.Skipped.Add($"drive:{flightId}: unknown subject '{fileNode.name}'");
                        continue;
                    }

                    var subject = KerbalismApi.FileSubjectData.GetValue(file);
                    if (subject == null)
                        continue;

                    files[subject] = file;
                    AddContribution(subject, KerbalismApi.FileSize, file);
                }
            }

            var samplesNode = driveNode.GetNode("samples");
            if (samplesNode != null)
            {
                foreach (var sampleNode in samplesNode.GetNodes())
                {
                    var sample = KerbalismApi.SampleLoad?.Invoke(null, new object[] { sampleNode.name, sampleNode });
                    if (sample == null)
                    {
                        report.Skipped.Add($"drive:{flightId}: unknown sample subject '{sampleNode.name}'");
                        continue;
                    }

                    var subject = KerbalismApi.SampleSubjectData.GetValue(sample);
                    if (subject == null)
                        continue;

                    samples[subject] = sample;
                    AddContribution(subject, KerbalismApi.SampleSize, sample);
                }
            }

            //4. sendFileNames is a CSV, not a node, so rehydrated by hand
            var sendFlags = KerbalismApi.DriveFileSendFlags?.GetValue(drive) as IDictionary;
            if (sendFlags != null && driveNode.HasValue("sendFileNames"))
            {
                var csv = driveNode.GetValue("sendFileNames");
                if (!string.IsNullOrEmpty(csv))
                {
                    foreach (var entry in csv.Split(','))
                    {
                        var trimmed = entry.Trim();
                        if (trimmed.Length > 0)
                            sendFlags[trimmed] = true;
                    }
                }
            }
        }

        private static object CreateEmptyDrive(ConfigNode driveNode)
        {
            if (KerbalismApi.DriveCtor == null)
                return null;

            //Not the ConfigNode ctor: it re-adds in-flight science, which a fresh drive would then double
            var name = driveNode.HasValue("name") ? driveNode.GetValue("name") : "DRIVE";
            var dataCapacity = driveNode.HasValue("dataCapacity") &&
                              KerbalismVesselState.TryParseDouble(driveNode.GetValue("dataCapacity"), out var dc) ? dc : 100000.0;
            var sampleCapacity = driveNode.HasValue("sampleCapacity") &&
                                 int.TryParse(driveNode.GetValue("sampleCapacity"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sc) ? sc : 1000;
            var isPrivate = driveNode.HasValue("is_private") &&
                            KerbalismVesselState.TryParseBool(driveNode.GetValue("is_private"), out var ip) && ip;

            var nameCtor = KerbalismApi.DriveType.GetConstructor(new[] { typeof(string), typeof(double), typeof(int), typeof(bool) });
            if (nameCtor != null)
                return nameCtor.Invoke(new object[] { name, dataCapacity, sampleCapacity, isPrivate });

            return KerbalismApi.DriveCtor.Invoke(new object[] { new ConfigNode() });
        }

        private static void RemoveContributions(IDictionary contents, FieldInfo sizeField, FieldInfo subjectField)
        {
            if (KerbalismApi.SubjectRemoveInFlight == null || sizeField == null || subjectField == null)
                return;

            foreach (var entry in new List<DictionaryEntry>(ToEntries(contents)))
            {
                if (entry.Value == null)
                    continue;

                var subject = subjectField.GetValue(entry.Value);
                var size = sizeField.GetValue(entry.Value);
                if (subject == null || !(size is double amount) || amount == 0)
                    continue;

                try
                {
                    KerbalismApi.SubjectRemoveInFlight.Invoke(subject, new object[] { amount });
                }
                catch (Exception e)
                {
                    LunaLog.LogWarning($"[Kerbalism] Could not remove in-flight science contribution: {e.Message}");
                }
            }
        }

        private static void AddContribution(object subject, FieldInfo sizeField, object entry)
        {
            if (KerbalismApi.SubjectAddInFlight == null || sizeField == null || subject == null)
                return;

            var size = sizeField.GetValue(entry);
            if (!(size is double amount) || amount == 0)
                return;

            try
            {
                KerbalismApi.SubjectAddInFlight.Invoke(subject, new object[] { amount });
            }
            catch (Exception e)
            {
                LunaLog.LogWarning($"[Kerbalism] Could not add in-flight science contribution: {e.Message}");
            }
        }

        private static IEnumerable<DictionaryEntry> ToEntries(IDictionary dictionary)
        {
            foreach (var key in dictionary.Keys)
                yield return new DictionaryEntry(key, dictionary[key]);
        }
    }
}
