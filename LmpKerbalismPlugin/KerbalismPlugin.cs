using System;
using Server.Client;
using Server.Log;
using Server.Plugin;
using Server.System;

namespace LmpKerbalismPlugin
{
    /// <summary>
    /// Standalone LMP server plugin providing Kerbalism mod support.
    ///
    /// Recreates the functionality of commit 0c8446c2 ("feat: add kerbalism support")
    /// within the LMP plugin architecture instead of the core:
    /// - Registers a "Kerbalism" mod handler so Kerbalism payloads sent through the
    ///   generic mod message pipeline (ModApiSystem on clients -> ModCliMsg -> server)
    ///   are accepted, validated and answered instead of silently dropped.
    /// - Tracks which clients run Kerbalism so server logs reflect the real mod mix.
    /// - Ships the client-side ModuleStore part module sync definitions for Kerbalism
    ///   (ClientData/PartSync), which FieldModuleStore loads at runtime from
    ///   GameData/LunaMultiplayer/PartSync. Those XMLs are data, not core code, so no
    ///   Kerbalism-specific logic is compiled into LmpClient.
    ///
    /// Relay behavior: ModDataMsgReader already relays client messages flagged with
    /// Relay=true to the other clients before this handler runs. The plugin therefore
    /// never rebroadcasts received payloads, only responds point-to-point, to avoid
    /// duplicate deliveries.
    /// </summary>
    // ReSharper disable once UnusedType.Global (activated by LmpPluginHandler via reflection)
    public class KerbalismPlugin : LmpPlugin
    {
        /// <summary>Mod name used in ModCliMsg/ModSrvMsg payloads.</summary>
        public const string ModName = "Kerbalism";

        /// <summary>
        /// Wire protocol carried in the mod payload:
        /// byte 0 = protocol version (1), byte 1 = opcode, rest = opaque data.
        /// </summary>
        internal const byte ProtocolVersion = 1;

        private const int StatusLogIntervalMs = 60_000;

        private readonly KerbalismClientRegistry _registry = new KerbalismClientRegistry();
        private readonly KerbalismModHandler _modHandler;
        private long _nextStatusLog;

        public KerbalismPlugin()
        {
            _modHandler = new KerbalismModHandler(_registry);
        }

        public override void OnServerStart()
        {
            if (!LmpModInterface.RegisterModHandler(ModName, _modHandler.HandleModMessage))
                LunaLog.Warning($"[KerbalismPlugin]: Could not register the {ModName} mod handler, another handler already owns '{ModName}'");

            _registry.Clear();
            _nextStatusLog = Environment.TickCount64 + StatusLogIntervalMs;
            LunaLog.Info($"[KerbalismPlugin]: Registered '{ModName}' mod handler (protocol v{ProtocolVersion}). " +
                        "Distribute ClientData/PartSync to clients so Kerbalism part modules sync (see Documentation/PluginDevelopment.md).");
        }

        public override void OnServerStop()
        {
            LmpModInterface.UnregisterModHandler(ModName);
            _registry.Clear();
            LunaLog.Info("[KerbalismPlugin]: Unregistered mod handler and cleared client tracking");
        }

        public override void OnClientDisconnect(ClientStructure client)
        {
            if (_registry.Remove(client))
                LunaLog.Debug($"[KerbalismPlugin]: Kerbalism client disconnected: {client.PlayerName}");
        }

        public override void OnUpdate()
        {
            var now = Environment.TickCount64;
            if (now < _nextStatusLog)
                return;

            _nextStatusLog = now + StatusLogIntervalMs;
            var tracked = _registry.Count;
            if (tracked > 0)
                LunaLog.Debug($"[KerbalismPlugin]: {tracked} client(s) running Kerbalism " +
                              $"({_registry.FormatClientNames()})");
        }
    }
}
