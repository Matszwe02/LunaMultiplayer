# LunaMultiPlayer Server Plugin Development Guide

This document describes the standard procedure for developing, registering and deploying
plugins within the LMP framework, using `LmpKerbalismPlugin` (the standalone Kerbalism
support plugin, a port of commit `0c8446c2931db6209d28f9c6b943c9f8f99c01db`) as the
worked example.

## 1. Plugin architecture overview

Everything plugin-related lives in the `Server.Plugin` namespace:

| Component | File | Role |
|---|---|---|
| `ILmpPlugin` | `Server/Plugin/ILmpPlugin.cs` | Contract every plugin implements |
| `LmpPlugin` | `Server/Plugin/LmpPlugin.cs` | Abstract base class with no-op virtuals — derive from this |
| `LmpPluginHandler` | `Server/Plugin/LmpPluginHandler.cs` | Discovers, loads and fires events to plugins |
| `LmpModInterface` | `Server/Plugin/LMPModInterface.cs` | Registry for per-mod payload callbacks |
| `ModDataSystemSender` | `Server/System/ModDataSystemSender.cs` | Send mod payloads from the server |

### 1.1 Lifecycle hooks

`MainServer` loads plugins once at startup, then fires hooks at these points:

| Hook | Fired when | Thread |
|---|---|---|
| `OnUpdate()` | Every main thread tick (10 ms), from `ClientMainThread` | Main thread |
| `OnServerStart()` | Just after the server starts/restarts (`MainServer`) | Startup |
| `OnServerStop()` | Just before the server stops/restarts (`MainServer`) | Shutdown |
| `OnClientConnect()` | Connection accepted (`ClientConnectionHandler`) | Lidgren receive loop |
| `OnClientAuthenticated()` | Handshake completed (`HandshakeSystem`) | Lidgren receive loop |
| `OnClientDisconnect()` | Client disconnects (`ClientConnectionHandler`) | Lidgren receive loop |
| `OnMessageReceived()` | Every message received (`MessageReceiver`) | Lidgren receive loop |
| `OnMessageSent()` | Every message sent (`ClientStructure`) | Client send thread |

Notes:

- All handshakes and disconnects are processed sequentially on the single Lidgren
  receive loop; keep connect/disconnect handlers fast and non-blocking.
- `LmpPluginHandler` wraps every hook dispatch in a try/catch: a throwing plugin can
  never crash the server, but its errors are only logged at **Debug** level, so run
  with debug logging while developing.
- Hook exceptions never propagate to other plugins.

### 1.2 Mod message pipeline

The generic mod-data channel is the preferred transport for plugin payloads:

```
Client mod code
  -> ModApiSystem (LmpClient)            ModCliMsg{ModName, Data, NumBytes, Relay}
  -> Server MessageReceiver
  -> ModDataMsgReader                    if Relay: MessageQueuer.RelayMessage<ModSrvMsg>  (fan-out to other clients)
  -> LmpModInterface.OnModMessageReceived  -> your registered handler
  -> (optionally) ModDataSystemSender.SendLmpModMessageToClient / SendLmpModMessageToAll
  -> Client ModApiMessageHandler -> ModApiEvent.onModMessageReceived.Fire(modName, data)
```

Key rules:

- Register handlers with `LmpModInterface.RegisterModHandler(modName, callback)`; the
  registration must happen in `OnServerStart()` (re-register on every server restart,
  the registry is static). Unregister in `OnServerStop()`.
- Registering twice for the same mod name returns `false` — never call `RegisterModHandler`
  outside `OnServerStart`.
- If the client sets `Relay=true`, `ModDataMsgReader` forwards the payload to all other
  clients **before** your handler runs. A handler that also rebroadcasts causes duplicate
  deliveries. Relay for peer-to-peer data; handle point-to-point in your plugin.
- `SendLmpModMessageToAll(excludeClient, modName, data)` queues to every other client;
  `SendLmpModMessageToClient(client, modName, data)` queues to one client.

## 2. Standard procedure to develop a plugin

1. **Create the project** at the repository root, e.g. `LmpMyPlugin/LmpMyPlugin.csproj`:

   ```xml
   <Project Sdk="Microsoft.NET.Sdk">
     <PropertyGroup>
       <TargetFramework>net10.0</TargetFramework>
       <AssemblyName>LmpMyPlugin</AssemblyName>
     </PropertyGroup>
     <ItemGroup>
       <ProjectReference Include="..\Server\Server.csproj" Private="false" />
     </ItemGroup>
   </Project>
   ```

   - The server targets `net10.0`; the plugin must match it.
   - `Private="false"` keeps referenced assemblies out of the plugin output — the server
     already loads them and `LmpPluginHandler` only needs the plugin DLL itself.
   - Reference `Server.csproj` (not LmpCommon alone) to reach `ILmpPlugin`, `LmpPlugin`,
     `LmpModInterface`, `ClientStructure`, `ModDataSystemSender`, `LunaLog`.

2. **Implement the plugin** as a `public` class deriving from `LmpPlugin` with a
   parameterless constructor:

   ```csharp
   using Server.Client;
   using Server.Plugin;
   using Server.System;

   public class MyPlugin : LmpPlugin
   {
       public override void OnServerStart()
       {
           LmpModInterface.RegisterModHandler("MyMod", HandleModMessage);
       }

       public override void OnServerStop() =>
           LmpModInterface.UnregisterModHandler("MyMod");
   }
   ```

   Discovery requirements enforced by `LmpPluginHandler.LoadPlugins()`:
   - The class must be `public` (only exported types are scanned).
   - It must implement `ILmpPlugin` (deriving from `LmpPlugin` suffices).
   - It must have a parameterless constructor (`Activator.CreateInstance`).
   - One assembly may contain several plugin classes; each is activated separately.

3. **Build and deploy** (registration is purely file-placement, no manifest needed):

   ```
   .\Scripts\build-lmp-projects.bat
   ```

   The script builds `LmpClient`, `Server`, the plugin projects, then `MasterServer`.
   A post-build target in the plugin project copies the DLL into
   `Server\bin\<Configuration>\net10.0\Plugins\`, which is the server's
   `ServerContext.DataDirectory\Plugins` folder at runtime.

   Manual deployment is equally valid: copy the DLL anywhere under `Plugins/`
   (subfolders are scanned recursively).

4. **What happens at startup**: `MainServer` calls `LmpPluginHandler.LoadPlugins()`,
   which creates `DataDirectory\Plugins` if missing, `Assembly.UnsafeLoadFrom`s every
   `.dll` in that tree, then instantiates every exported `ILmpPlugin` type. Assembly
   references the plugin cannot resolve are looked up among already-loaded assemblies
   (`AppDomain.AssemblyResolve`); unresolved ones are logged as errors.

## 3. Shipping client-side mod data with a plugin

LMP has no client-side DLL plugin system by design: the client runs inside KSP and only
the server supports plugin assemblies. Client extensibility is **data-driven** through
the ModuleStore:

- `FieldModuleStore.ReadCustomizationXml()` reads every
  `GameData\LunaMultiplayer\PartSync\**\*.xml` at client startup and builds
  `ModuleDefinition` entries (fields to sync, methods to patch).
- `PartModuleRunner`/`PartModulePatcher` then Harmony-transpiles the matching `PartModule`
  methods so changing a customized field triggers a sync. Since the 0c8446c2 port, the
  patcher also covers editor-only and unfocused `KSPEvent`s and methods explicitly listed
  in the module XML (`CustomizedMethods`) — a generic, data-driven rule with no mod names
  in core code.

A server plugin can therefore ship a **client data payload** next to its DLL:

- Store the XMLs under `<PluginProject>\ClientData\PartSync\` with a
  `<Content Include="ClientData\PartSync\**\*.xml" CopyToOutputDirectory="PreserveNewest" />`
  item; the deploy target copies them beside the plugin DLL under `Plugins/`.
- Distribute them to players' `GameData\LunaMultiplayer\PartSync\` folder — either
  manually or via `Scripts\CopyToKSPDirectory.bat`, which now also copies plugin
  client data into the PartSync folder.

**Device write paths must be listed as `Methods`.** The transpiler only sees a field
change that happens *inside* a patched method (the module's own `Update`/`FixedUpdate`,
its `KSPAction`/`KSPEvent` methods, or a `CustomizedMethods` entry). When a mod's own
code — e.g. Kerbalism's Automation tab devices and scripts — writes the field through a
plain method (`ProcessDevice.Ctrl` → `ProcessController.SetRunning`), that method must
be in the module XML's `Methods` or the change is never observed and never synced. Same
for reflection-only callbacks (`ReliabilityEvent`/`ReliablityEvent`, spelling is
Kerbalism's). Methods that only mutate another module's fields are covered transitively
by that module's own patched update loop.

Scene caveat: this mechanism only sees **loaded** `PartModule` writes, i.e. flight.
Kerbalism's Monitor AUTO page ("Control and automate components") drives **proto**
devices in the Tracking Station (`ProtoDevice.Ctrl` → `Lib.Proto.Set`), writing straight
into the local proto snapshot; LMP has no observer for proto edits and no full-proto send
path out of non-flight scenes (all sends are flight-scene and update-lock gated), so
tracking-station toggles are local-only. In flight the same page drives the loaded
modules (`Device.Ctrl` → `SetRunning`/`Toggle()`/...) and syncs through the mechanisms
above; other players then see the change in their own Tracking Station views because
`PartSyncField` applies into their stored protovessels.

## 4. Docker deployment

`docker-compose.yml` + `Dockerfile_Server` build and run the server in
`lunamultiplayer` (port 8800/udp). The Dockerfile publishes the server **and** the
plugin projects (`LmpKerbalismPlugin.csproj`) into `/LMPServer/Plugins/`.

Caveat: the compose file bind-mounts a host folder over `/LMPServer/Plugins`
(`./../opt/LMPServer/Plugins`), which **shadows** anything baked into the image. For
compose deployments, copy the plugin files into the host folder before starting:

```
.\Scripts\build-lmp-projects.bat --release
New-Item -ItemType Directory -Force -Path ..\opt\LMPServer\Plugins | Out-Null
Copy-Item LmpKerbalismPlugin\bin\Release\net10.0\LmpKerbalismPlugin.dll ..\opt\LMPServer\Plugins\
Copy-Item LmpKerbalismPlugin\bin\Release\net10.0\ClientData ..\opt\LMPServer\Plugins -Recurse -Force
docker compose up -d --build
```

## 5. Worked example: `LmpKerbalismPlugin`

`LmpKerbalismPlugin` recreates the Kerbalism support from commit `0c8446c2` as a plugin:

- **Server side** (`KerbalismPlugin.cs`, `KerbalismModHandler.cs`): registers a
  `Kerbalism` mod handler with a 2-byte envelope (version, opcode: `Ping`/`Pong`/`Notify`),
  validates payloads, tracks Kerbalism-capable clients and logs a periodic status line.
  Relay-flagged payloads are already fanned out by the core, so the handler answers
  point-to-point only.
- **Client side** (`ClientData/PartSync/*.xml`): the 21 Kerbalism `ModuleDefinition`
  files (Comfort, Configure, Deploy, ECDrainViaPM, Emitter, Experiment,
  ExperimentResultImage, GravityRing, Greenhouse, Habitat, HardDrive, Harvester,
  KerbalismScansat, Laboratory, PassiveShield, PlannerController, ProcessController,
  Reliability, Sensor, Sickbay, SolarPanelFixer). Kerbalism scenario/vessel data keeps
  flowing through the generic Vessel/Scenario systems; no server code is mod-specific.
- **Core enablers ported from the same commit** (generic, not Kerbalism-specific):
  - `PartModulePatcher`: patches editor-only/unfocused `KSPEvent`s and XML-listed
    `CustomizedMethods` (required for Harvester.Toggle, Reliability.Repair,
    Configure.ToggleWindow, ... to sync persistent fields).
  - `VesselResourceSys`: throttled duplicate warnings and skipping of proto parts with
    `flightID == 0` both when sending and applying resources.
  - `VesselLoader`: backs up the existing proto vessel before a destructive reload and
    restores the last-known-good state when the reload fails, keeping vessels listed in
    FlightGlobals (e.g. Kerbalism's monitor) instead of letting them vanish.

### 5.1 Received-scenario hardening

`ScenarioSystem.LoadScenarioDataIntoGame` rebuilds the game from the server's scenario
nodes, so a node written by a player with mods that another client lacks can abort the
whole game load. Two stock cases are pre-filtered on receive:

- `ContractSystem`: contracts referencing parts the client doesn't have (or body
  indices out of range) are stripped and replaced with informative stubs
  (`StripContractsWithMissingParts`).
- `ResearchAndDevelopment`: the `ExpParts` node lists part names; stock
  `ResearchAndDevelopment.OnLoad` does
  `experimentalPartsStock.Add(PartLoader.getPartInfoByName(name), int.Parse(value))`
  and throws `ArgumentNullException` for a part from a mod the client doesn't have
  (e.g. `cryoengine-hecate-1` from CryoTanks). The abort leaves R&D's science
  dictionary uninitialized, so Kerbalism's `ScienceDB.Load` then fails on
  `ResearchAndDevelopment.GetSubjects()` and the client cannot start the game.
  `StripExpPartsWithMissingParts` removes such entries before the module is applied.
- Module ordering: `LoadScenarioDataIntoGame` adds the received
  `ResearchAndDevelopment` module **first**, before all other modules. KSP instantiates
  scenario modules in `game.scenarios` order, stock saves always place R&D before
  third-party modules, and Kerbalism's `ScienceDB.Load` calls
  `ResearchAndDevelopment.GetSubjects()` — which returns null while the R&D component
  does not exist yet. The server relays modules in its internal dictionary order
  (commonly Kerbalism before R&D), which made every career load die with
  "Kerbalism.OnLoad FATAL ERROR : ... NullReferenceException at ScienceDB.Load [0x0028d]"
  (sandbox loads are unaffected because the R&D branch is skipped there).
