using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Server.Context;
using Server.Log;

namespace KerbalismSync.Server
{
    /// <summary>One vessel's authoritative state, as last accepted from the client holding its lock.</summary>
    public sealed class StoredState
    {
        public byte[] Payload;
        public uint Revision;
        public string LastWriter;
        public DateTime LastWriteUtc;
    }

    /// <summary>The server's copy of every vessel's state plus the save-global blob, persisted to disk.</summary>
    public static class KerbalismStateStore
    {
        private static readonly ConcurrentStore<string, StoredState> Vessels = new ConcurrentStore<string, StoredState>();
        private static StoredState _global;

        private static readonly object FileLock = new object();
        private static bool _dirty;
        private static DateTime _lastWriteUtc = DateTime.MinValue;

        /// <summary>Minimum interval between disk writes, to bound IO.</summary>
        private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(20);

        public static int VesselCount => Vessels.Count;
        public static long TotalBytes => Vessels.Values.Sum(v => (long)v.Payload.Length);

        public static void SetVessel(Guid vesselId, byte[] payload, string writer)
        {
            var key = vesselId.ToString();
            var existing = Vessels.GetOrAdd(key, () => new StoredState { Revision = 0 });

            lock (existing)
            {
                existing.Payload = payload;
                existing.Revision++;
                existing.LastWriter = writer;
                existing.LastWriteUtc = DateTime.UtcNow;
            }

            MarkDirty();
        }

        public static void SetGlobal(byte[] payload, string writer)
        {
            lock (FileLock)
            {
                _global = new StoredState
                {
                    Payload = payload,
                    Revision = (_global?.Revision ?? 0) + 1,
                    LastWriter = writer,
                    LastWriteUtc = DateTime.UtcNow
                };
            }

            MarkDirty();
        }

        public static StoredState GetVessel(Guid vesselId) => Vessels.Get(vesselId.ToString());
        public static StoredState GetGlobal() => _global;

        public static bool RemoveVessel(Guid vesselId)
        {
            if (!Vessels.Remove(vesselId.ToString()))
                return false;

            MarkDirty();
            return true;
        }

        /// <summary>Drops vessels the server no longer knows about, so the file cannot grow without bound.</summary>
        public static int Prune(Func<Guid, bool> vesselExists)
        {
            var removed = 0;
            foreach (var pair in Vessels.Snapshot())
            {
                if (!Guid.TryParse(pair.Key, out var id))
                {
                    Vessels.Remove(pair.Key);
                    removed++;
                    continue;
                }

                if (vesselExists != null && !vesselExists(id))
                {
                    Vessels.Remove(pair.Key);
                    removed++;
                }
            }

            if (removed > 0)
                MarkDirty();

            return removed;
        }

        private static string StateDirectory => Path.Combine(ServerContext.DataDirectory, "KerbalismSync");
        private static string StateFile => Path.Combine(StateDirectory, "vesselstate.bin");

        private static void MarkDirty()
        {
            lock (FileLock)
            {
                _dirty = true;
            }
        }

        /// <summary>Writes to disk if it changed and the interval elapsed. Safe to call every update.</summary>
        public static void SaveIfDue()
        {
            lock (FileLock)
            {
                if (!_dirty || DateTime.UtcNow - _lastWriteUtc < SaveInterval)
                    return;
            }

            Save();
        }

        public static void Save()
        {
            lock (FileLock)
            {
                try
                {
                    if (!Directory.Exists(StateDirectory))
                        Directory.CreateDirectory(StateDirectory);

                    var snapshot = Vessels.Snapshot();
                    var global = _global;

                    using (var file = new FileStream(StateFile, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new BinaryWriter(file, Encoding.UTF8))
                    {
                        writer.Write(SaveFormatVersion);
                        writer.Write(snapshot.Length);
                        foreach (var pair in snapshot)
                        {
                            writer.Write(pair.Key);
                            writer.Write(pair.Value.Revision);
                            writer.Write(pair.Value.LastWriter ?? string.Empty);
                            WriteBytes(writer, pair.Value.Payload);
                        }

                        if (global == null)
                        {
                            writer.Write(false);
                        }
                        else
                        {
                            writer.Write(true);
                            writer.Write(global.Revision);
                            writer.Write(global.LastWriter ?? string.Empty);
                            WriteBytes(writer, global.Payload);
                        }
                    }

                    _dirty = false;
                    _lastWriteUtc = DateTime.UtcNow;
                }
                catch (Exception e)
                {
                    LunaLog.Error($"[Kerbalism] Could not save vessel state: {e}");
                }
            }
        }

        private const int SaveFormatVersion = 1;

        private static void WriteBytes(BinaryWriter writer, byte[] data)
        {
            data = data ?? new byte[0];
            writer.Write(data.Length);
            writer.Write(data, 0, data.Length);
        }

        public static void Load()
        {
            lock (FileLock)
            {
                try
                {
                    if (!File.Exists(StateFile))
                        return;

                    Vessels.Clear();
                    _global = null;

                    using (var file = new FileStream(StateFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var reader = new BinaryReader(file, Encoding.UTF8))
                    {
                        var version = reader.ReadInt32();
                        if (version != SaveFormatVersion)
                        {
                            LunaLog.Warning($"[Kerbalism] Vessel state file is version {version}, expected {SaveFormatVersion}. Starting empty.");
                            return;
                        }

                        var count = reader.ReadInt32();
                        for (var i = 0; i < count; i++)
                        {
                            var key = reader.ReadString();
                            var revision = reader.ReadUInt32();
                            var writer = reader.ReadString();
                            var payload = ReadBytes(reader);
                            Vessels.Set(key, new StoredState
                            {
                                Revision = revision,
                                LastWriter = writer,
                                Payload = payload,
                                LastWriteUtc = DateTime.UtcNow
                            });
                        }

                        if (reader.ReadBoolean())
                        {
                            var revision = reader.ReadUInt32();
                            var writer = reader.ReadString();
                            _global = new StoredState
                            {
                                Revision = revision,
                                LastWriter = writer,
                                Payload = ReadBytes(reader),
                                LastWriteUtc = DateTime.UtcNow
                            };
                        }
                    }

                    LunaLog.Normal($"[Kerbalism] Loaded {Vessels.Count} vessel state(s) and " +
                                $"{(_global != null ? 1 : 0)} global state(s) from disk " +
                                $"({TotalBytes / 1024} KB)");
                }
                catch (Exception e)
                {
                    LunaLog.Error($"[Kerbalism] Could not load vessel state, starting empty: {e}");
                }
            }
        }

        private static byte[] ReadBytes(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length <= 0)
                return new byte[0];

            if (length > Protocol.MaxMessageBytes)
                throw new InvalidDataException($"Stored payload of {length} bytes is implausible");

            return reader.ReadBytes(length);
        }

        public static void Clear()
        {
            Vessels.Clear();
            lock (FileLock) { _global = null; }
        }

        /// <summary>Minimal concurrent map, to avoid depending on a specific collection type.</summary>
        private sealed class ConcurrentStore<TKey, TValue> where TValue : class
        {
            private readonly Dictionary<TKey, TValue> _items = new Dictionary<TKey, TValue>();
            private readonly object _lock = new object();

            public int Count { get { lock (_lock) { return _items.Count; } } }

            public TValue Get(TKey key)
            {
                lock (_lock) { TValue value; return _items.TryGetValue(key, out value) ? value : null; }
            }

            public void Set(TKey key, TValue value)
            {
                lock (_lock) { _items[key] = value; }
            }

            public TValue GetOrAdd(TKey key, Func<TValue> factory)
            {
                lock (_lock)
                {
                    TValue value;
                    if (!_items.TryGetValue(key, out value))
                    {
                        value = factory();
                        _items[key] = value;
                    }

                    return value;
                }
            }

            public bool Remove(TKey key)
            {
                lock (_lock) { return _items.Remove(key); }
            }

            public void Clear()
            {
                lock (_lock) { _items.Clear(); }
            }

            public KeyValuePair<TKey, TValue>[] Snapshot()
            {
                lock (_lock) { return _items.ToArray(); }
            }

            public TValue[] Values
            {
                get { lock (_lock) { return _items.Values.ToArray(); } }
            }
        }
    }
}
