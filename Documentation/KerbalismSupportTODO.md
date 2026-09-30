# Kerbalism support for LunaMultiplayer — implementation TODO

Status: planning document for branch `feat/kerbalism-plugin`.
Scope: make Kerbalism (v2.x, commit clone in `Kerbalism/`) actually work under LMP.

Everything below is derived from reading `LmpClient`, `Server`, `LmpCommon` and the
Kerbalism source in `Kerbalism/src/Kerbalism`, plus building `LmpClient` and
`LmpKerbalismPlugin` on this branch. File:line references are from that tree.

---

## 0. TL;DR — why "barely anything is synced"

The branch has three layers. Only the middle one can work today, and it is broken
in five independent ways.

| Layer | What it is | Verdict |
|---|---|---|
| `LmpKerbalismPlugin` server plugin | `Ping`/`Pong`/`Notify` over LMP's generic mod channel | **Dead code, twice over.** Nothing calls `ModApiSystem.SendModMessage` or subscribes to `ModApiEvent.onModMessageReceived`, so the handler never receives a byte; and the underlying channel is broken anyway — `ModDataSystemSender` never sets `NumBytes`, so server→client payloads serialize zero bytes. |
| `ClientData/PartSync/Kerbalism/*.xml` (21 files) | LMP's data-driven part-module field sync | **Never reaches players.** Not built by the solution, not in the release pipeline. Even when present it is wrong in ~15 places, and two of its fields (`uint`) trip a receive path that disconnects the client. |
| Kerbalism's own `VesselData` (via the `"Kerbalism"` `ScenarioModule`) | all real gameplay state: automation, resources, comms, science drives, failures | **Already synced, but wrong.** Whole-blob `AddOrUpdate` with no authority, applied only at game load. |
| `LMPModControl.xml` allowlist | part allowlist, enforced on both sides | **Works in the bundled server config (~127 Kerbalism parts), but a regenerated or unparseable file rejects every Kerbalism vessel** — and an unparseable file with an empty list rejects every vessel at all. |

There is no separate "Kerbalism sync system". Kerbalism state is split across three
storage mechanisms, and each needs a different fix.

---

## 1. Phase 0 — Unblock delivery (do this first, nothing else matters until it lands)

- [ ] **P0.1 Add `LmpKerbalismPlugin` to `LunaMultiPlayer.sln`.**
      Verified: `Select-String LunaMultiPlayer.sln -Pattern LmpKerbalismPlugin` returns nothing,
      so `dotnet build LunaMultiPlayer.sln` never produces the plugin.
- [ ] **P0.2 Ship the PartSync XMLs in the client release.**
      `appveyor.yml:87` copies only `LmpClient\ModuleStore\XML\*.*` into
      `GameData\LunaMultiplayer\PartSync`. Add the equivalent step for
      `LmpKerbalismPlugin\ClientData\PartSync\*.*`. Without this, released clients hit
      `FieldModuleStore.cs:42` with an empty set and `PartModulePatcher.cs:35` skips every module.
- [ ] **P0.3 Ship the plugin DLL in the server release.**
      `appveyor.yml` has no reference to `LmpKerbalismPlugin` at all, so both server zips
      (`appveyor.yml:92-93`) ship without it.
- [ ] **P0.4 Fix the Docker compose shadowing.**
      `docker-compose.yml:23` mounts `/opt/LMPServer/Plugins` over `/LMPServer/Plugins`, hiding
      the plugin that `Dockerfile_Server` now publishes into `Publish/Plugins`. The branch's own
      Dockerfile comment acknowledges this. Either mount the host folder *into* the image's plugin
      directory content-wise, or document that compose deployments must copy the files manually.
- [ ] **P0.5 Decide where the XMLs live.**
      They currently ship as plugin `ClientData` but are consumed by `LmpClient` from a fixed
      path (`FieldModuleStore.cs:20`). Either move them into `LmpClient\ModuleStore\XML\Kerbalism\`
      (alongside `Squad\`) so the existing `appveyor.yml:87` and
      `Scripts\CopyToKSPDirectory.bat:69` steps pick them up for free, or keep them in the plugin
      and add the release steps in P0.2/P0.3. The first option is materially cheaper and removes a
      whole class of "did the files get there" bugs.
- [ ] **P0.7 [BLOCKER] Audit `LMPModControl.xml` — it can reject every Kerbalism vessel.**
      With `GeneralSettings.SettingsStore.ModControl = true` (the default,
      `GeneralSettingsDefinition.cs:67`), two code paths silently refuse vessels:
      `Server\System\Vessel\VesselDataUpdater.cs:53-62` drops any vessel with a part not in
      `AllowedParts`, and `LmpClient\Extensions\ProtoVesselExtension.cs:34-44` refuses to load it.
      The list is vanilla-only by construction: `ModControlStructure.SetDefaultAllowedParts()`
      (`:37`) and `SetDefaultAllowedResources()` (`:591`) enumerate stock parts only, and
      `Universe.cs:35-39` calls `GenerateNewModFile()` whenever the file is absent — so a fresh
      server rejects all Kerbalism vessels. Worse, `ModFileSystem.LoadModFile()` (`:28-32`)
      falls back to `new ModControlStructure()` on *any* parse error, whose `AllowedParts` is
      empty (`:34`), which rejects **every** vessel including vanilla ones.
      The bundled `opt/LMPServer\Config\LMPModControl.xml` already contains ~127 Kerbalism part
      names, so the shipped server works — but nothing keeps it that way.
      **Fix:** (a) make the empty-allowlist fallback safe (fail closed on "no data" instead of
      "allow nothing", or disable `ModControl`); (b) regenerate `LMPModControl.xml` with the
      Kerbalism part and resource list — `LmpClient\Systems\Mod\ModSystem.cs:84-106`
      (`GenerateModControlFile`) is the existing mechanism; (c) add a server log line naming the
      rejected part so this failure is never silent again.
- [ ] **P0.8 Write a startup self-report.**
      After `FieldModuleStore.ReadCustomizationXml()`, log one line:
      `N definitions loaded, X/Y fields resolved, unknown: a.b, c.d`.
      Today a single unresolvable `FieldName` silently disables an entire module
      (`FieldChangeTranspiler.cs:72-77` skips it, then throws `KeyNotFoundException` at `:136`,
      falls back to `Restore`, and logs at `Error` only). With 21 modules and ~150 fields this is
      the single highest-value diagnostic, and it is what turns "nothing syncs" into a list.

---

## 2. Phase 1 — Fix LMP part-sync engine blockers (core, benefits every mod)

These are in `LmpClient`/`Server` and are not Kerbalism-specific, but Kerbalism is what
exposes them.

- [ ] **P1.1 [BLOCKER] Handle the five missing field types on receive.**
      `VesselPartSyncFieldQueue.AssignFromMessage` (`VesselPartSyncFieldQueue.cs:22-55`) handles
      `Boolean, Integer, Float, Double, Vector2, Vector3, Quaternion, Object, String, Enum` and
      falls through to `default: throw new ArgumentOutOfRangeException()` at `:53-54`.
      `Short(1) UShort(2) UInteger(4) Long(6) ULong(7)` are produced by the sender
      (`VesselPartSyncFieldMessageSender.cs:57,66,85,106,115`) and are legal on the wire
      (`VesselPartSyncFieldMsgData.cs`), so the receiver is simply incomplete.
      The throw escapes to `Base\MessageSystem.cs:65-75`, which calls `NetworkConnection.Disconnect`.
      Live triggers in the shipped XMLs: `Experiment.privateHdId` (`Experiment.cs:48`, `uint`) and
      `HardDrive.hdId` (`HardDrive.cs:27`, `uint`).
      **Fix:** add the five `case` arms, and replace `default: throw` with log-and-drop so no
      single field can ever kick a client. Audit `VesselPartSyncUiFieldQueue` the same way.
- [ ] **P1.2 [BLOCKER] Make PartSync XML loading fail-soft.**
      `FieldModuleStore.ReadCustomizationXml` throws, uncaught from `MainSystem.cs:296`, on:
      a missing `PartSync` folder (`:42` `DirectoryNotFoundException`); any malformed XML
      (`:44`, wrapped at `LunaXmlSerializer.cs:28-31`); and duplicate base file names across
      subfolders (`:50` `ToDictionary`). Any of these aborts `LmpBaseEvent.Awake`,
      `HarmonyPatcher.Awake`, `PartModuleRunner.Awake` and network init.
      Third-party mod payloads must never be able to prevent LMP from starting.
- [ ] **P1.3 [BLOCKER] Add lock-authority validation on the server.**
      `Server\Message\VesselMsgReader.cs:53-63` relays and persists
      `PartSyncField`/`PartSyncUiField`/`PartSyncCall` with no lock check. Any client can
      rewrite any vessel's module fields on every peer and in the persisted vessel.
      Model on `VesselActionGroupEvents.cs:11-13`.
- [ ] **P1.4 [BLOCKER] Drop unknown modules/fields on receive.**
      `VesselPartSyncField.cs:51-52` writes into `module.moduleValues` (`ConfigNode`) with no
      existence check, so a client without Kerbalism silently accumulates orphan `= running`
      nodes inside `<MODULE name="Harvester">`, and `VesselMsgReader.cs:54` persists them for
      everyone else and for late joiners. Log and skip instead.
- [ ] **P1.5 Fix the `Type.Name` vs `module.moduleName` split.**
      Patch/throttle lookups use the CLR type name (`FieldModuleStore.cs:55`,
      `PartModulePatcher.cs:35`), send/UI lookups use the KSP config `name`
      (`VesselPartSyncFieldEvents.cs:29`, `UIPartActionButton_OnClick.cs:21`). When they differ the
      throttle silently falls through to "always send". Add an optional `ModuleName`/type element
      to the schema instead of overwriting it at `FieldModuleStore.cs:45`.
- [ ] **P1.6 Make `Methods`-only definitions work.**
      `PartModulePatcher.cs:35` gates all patching on `CustomizationModule.CustomizedFields.Any()`,
      so a definition with `<Fields/>` and `<Methods>` is never transpiled and its methods are
      never patchable. Gate on `Fields.Any() || Methods.Any()`.
- [ ] **P1.7 Deduplicate `Fields` once.**
      `ModuleDefinition.MergeWith` (`:34`) `AddRange`s into a list that `Init()` (`:28`) never
      deduplicates; `FieldChangeTranspiler` iterates raw `Fields` at `:69` but
      `Fields.DistinctBy(...)` at `:127`, so the two index spaces can diverge after a parent merge.
      Have both passes read `CustomizedFields`.
- [ ] **P1.8 Fix `FieldChangeTranspiler`'s `ceq` and return rewriting.**
      `ceq` (`:188-192`) is only spec-defined for `(int32,int32)` and `(O,O)`; struct and
      `Nullable<T>` fields produce invalid IL and an `InvalidProgramException` on first call.
      `RedirectExistingReturns` (`:325-329`) rewrites every interior `ret` to `br`, so listing a
      non-`void` method in `<Methods>` corrupts it. Use an injected
      `EqualityComparer<T>.Default.Equals` and refuse to transpile non-void methods.
- [ ] **P1.9 `MethodDefinition.MaxIntervalInMs` is dead.**
      Declared at `MethodDefinition.cs:11`, read nowhere. Authors assume method calls are
      throttleable. Either honour it or delete it.
- [ ] **P1.10 `PartModuleRunner.Awake` throws away patching for every module on one bad assembly.**
      It uses raw `a.GetTypes()` (`PartModuleRunner.cs:27`) while `FieldModuleStore` uses the
      `ReflectionTypeLoadException`-tolerant `GetLoadableTypes()`. One unloadable type in any mod
      assembly throws inside the `Task.Run` at `:25-43`, `Ready` still reports `true`, and the
      client connects with zero modules patched.
- [ ] **P1.11 `PartSyncField` never updates a loaded remote vessel's live module.**
      `VesselPartSyncField.cs:57` writes only the proto `ConfigNode`. Remote in-memory modules stay
      stale until reload. Only the separate UI path writes `moduleRef.Fields`
      (`VesselPartSyncUiField.cs:48-63`), and only for bool/int/float and only under the control
      lock. Decide whether this is acceptable (documented) or needs a live-write path.
- [ ] **P1.12 Stop patching in the editor.**
      `PartModulePatcher` is scene-agnostic, so the VAB/SPH pays IL-generation and per-call
      comparison cost while `CallIsValid` (`VesselPartSyncFieldEvents.cs:17-19`) discards every
      event. Editor toggles mutate the local craft and are then overwritten by the next proto.
- [ ] **P1.13 [BLOCKER] `ModDataSystemSender` sends zero bytes.**
      `Server\System\ModDataSystemSender.cs:20-22` and `:35-37` set `Data` and `ModName` but never
      `NumBytes`. `ModMsgData.InternalSerialize` (`ModMsgData.cs:44`) writes
      `lidgrenMsg.Write(Data, 0, NumBytes)` with `NumBytes == 0`, and
      `InternalGetMessageSize` (`:59`) also ignores the payload. Every server→client mod message
      therefore arrives empty. `Reliable` is never set either, so both directions default to
      `UnreliableSequenced` on channel 0 (`ModCliMsg.cs:16-24`).
      **Fix:** set `msgData.NumBytes = messageData.Length;` and `msgData.Reliable = true;` in both
      senders. The client sender (`ModApiSystem.cs:53-57`) has the same missing `Reliable`.
- [ ] **P1.14 Reset pooled message data.**
      `MessageStore.RecycleMessage` (`:21-38`) bags instances and `GetMessageData` (`:40-49`) hands
      them back **without clearing fields**; `ModMsgData`'s ctor is empty (`ModMsgData.cs:9`). A
      recycled instance with a stale `NumBytes` larger than `Data.Length` makes
      `NetBuffer.Write` throw in `Buffer.BlockCopy`, which `ClientStructure.cs:77-81` turns into a
      client disconnect. Fix P1.13 makes this unreachable for mod payloads, but the same class of
      bug exists for every other `ModMsgData`-shaped payload — consider a reset in `RecycleMessage`.
- [ ] **P1.15 Document the MTU ceiling: no fragmentation.**
      `Lidgren\NetPeerConfiguration.cs:42` caps MTU at 1408 and `SendMessageFragmented` has zero
      call sites, so a single mod payload must stay under roughly 1.3 KB.
      `ModApiSystem.SendModMessage` performs no size validation, so an oversized payload is sent
      as one oversized datagram. Any per-vessel Kerbalism blob transport must chunk or stay small.

---

## 3. Phase 2 — Correct the Kerbalism PartSync XMLs

21 XMLs, all in `LmpKerbalismPlugin\ClientData\PartSync\Kerbalism\`.
Loader facts: module identity is the **file name** (`FieldModuleStore.cs:45`, must equal the CLR
type name); only `Fields/FieldDefinition/{FieldName,MaxIntervalInMs}` and
`Methods/MethodDefinition/{MethodName,MaxIntervalInMs}` are valid elements; receive writes only
`moduleValues`, so **only `[KSPField(isPersistant=true)]` fields are worth syncing**; a field is
observed only when written inside a patched method.

- [ ] **P2.1 [BLOCKER] `Experiment.xml`: add `ForegroundFixedUpdate` to `<Methods>`.**
      `Experiment.cs:329`. Its writes (`issue:381`, `situationId:351/365`, `shrouded:378`,
      `remainingSampleMass:381`, `prodFactor:381`, `status:399`) happen in a method that is neither
      a Unity lifecycle name nor a `KSPEvent`, so **8 of the 10 declared fields are currently
      unobservable**. Single highest-value XML edit.
- [ ] **P2.2 [BLOCKER] `Configure.xml`: the in-flight reconfiguration path is unpatched.**
      `cfg`/`prev_cfg` are only written from `OnGUI` (`Configure.cs:399-411`), which is not
      whitelisted and not listed. Listing `DoConfigure` does not help — the write happens before
      `DoConfigure` is entered, in a different frame. Decide: patch `OnGUI` for this module, or
      accept that Kerbalism `Configure` reconfiguration is editor/ground-only.
- [ ] **P2.3 [BLOCKER] Remove or fix the two `uint` entries.**
      `Experiment.privateHdId` and `HardDrive.hdId`. Both are written only in `OnStart`
      (`Experiment.cs:228`, `HardDrive.cs:108`), which is unpatched, so today they are doubly
      broken. Prefer P1.1 (fix the receiver) **and** keep them; if P1.1 is deferred, drop them now
      because they are landmines.
- [ ] **P2.4 `Harvester.xml`: add `EnableModule` / `DisableModule` to `<Methods>`.**
      `Harvester.cs:583-584`. These are `IAnimatedModule` callbacks invoked by stock
      `ModuleAnimationGroup`, so for any drill deployed by an animation group `deployed`/`running`
      never sync. `Harvester.Toggle` is already covered (it has `guiActiveEditor`).
- [ ] **P2.5 `ProcessController.xml`: add `ReliablityEvent` to `<Methods>`.**
      `ProcessController.cs:166-170` (sic, typo in Kerbalism). It is the only writer of `broken`,
      and it is invoked from `Reliability.Apply` (`Reliability.cs:851`), i.e. from another class's
      patched frame.
- [ ] **P2.6 `KerbalismScansat.xml`: add `StartScan` / `StopScan` to `<Methods>`.**
      `KerbalismScansat.cs:367`, `:378`. The file currently has **no** `<Methods>` block at all,
      so `power_disabled`/`storage_disabled` never sync when scanning is toggled.
- [ ] **P2.7 `SolarPanelFixer.xml`: add `ReliabilityEvent` / `ToggleState` to `<Methods>`.**
      `SolarPanelFixer.cs:942-946`, `:937`. These set `PanelState.Failure`.
- [ ] **P2.8 `SolarPanelFixer.xml`: throttle `launchUT`.**
      `SolarPanelFixer.cs:446-447` rewrites it every `FixedUpdate` in `PRELAUNCH` with no
      `MaxIntervalInMs`, so it floods the channel. Add e.g. `30000`.
- [ ] **P2.9 `Greenhouse.xml`: add `Configure` to `<Methods>`** (and audit `Experiment` and
      `ProcessController` for the same `IConfigurable` pattern). `Greenhouse.Configure(bool,int)`
      (`Greenhouse.cs:57-60`) writes `active` from `Configure.DoConfigure`, another class's frame.
- [ ] **P2.10 `SolarPanelFixer.manualTracking` / `trackedSunIndex`: accept they cannot sync.**
      They are assigned only in popup lambdas (`SolarPanelFixer.cs:187`, `:193-194`) executed by
      `PopupDialog` after `ManualTracking` returns. Listing `ManualTracking` is ineffective. Same
      shape as P2.2. If these matter, the engine needs to patch deferred callbacks, not a longer XML.
- [ ] **P2.11 `Experiment.expState` / `status`: assign in a patched frame or drop.**
      They are written in `set_State` (`Experiment.cs:79-90`). `ToggleEvent` is patched so manual
      toggles are caught, but deployable instruments assign `State` from an animation-completion
      callback (`:881-886`) and the stop delegate (`:842-847`).
- [ ] **P2.12 `ProcessController.xml`: list `persistentValveIndex`.**
      `ProcessController.cs:41`, persisted via `OnSave`/`OnLoad` (`:72`/`:66`) so it round-trips,
      but it is unlisted — the dump-valve choice is entirely unsynced.
- [ ] **P2.13 Drop ineffective `<Methods>` entries.**
      `Configure.ToggleWindow`, `Habitat.Vent` (writes resources, not `state`),
      `Laboratory.CleanExperiments`, `ProcessController.DumpValve`, and
      `HardDrive.ToggleUI`/`TransferData`/`TakeData`/`StoreData`. They mutate no declared field and
      cost transpilation on every invocation.
- [ ] **P2.14 Fix the false comment in `HardDrive.xml`.**
      It claims the drive contents are synchronized through the scenario system. Nothing in
      `LmpKerbalismPlugin` touches science/`VesselData`/`DB`. See Phase 4.
- [ ] **P2.15 Note the three functional no-ops.**
      `Comfort.bonus`, `Deploy.isBroken`, `Sensor.type` are persistent but never reassigned
      anywhere in Kerbalism. Harmless (they pin the save format) but they are not coverage and
      should be commented so future maintainers do not count them.
- [ ] **P2.16 Add the missing integration-module XMLs** — see Phase 5.

---

## 4. Phase 3 — Per-vessel Kerbalism state (`VesselData`)

Kerbalism is `[KSPScenario(AddToAllGames)] public sealed class Kerbalism` (`Kerbalism.cs:35-36`);
`OnSave` → `DB.Save` (`Kerbalism.cs:250`). Everything is under
`GAME/SCENARIO[name=Kerbalism]`, keyed `vessels2/<pv.vesselID Guid>` (`DB.cs:124-136`).
There is **no** `[KSPAddon]`+`Persistent` anywhere in Kerbalism — nothing is stored in the
`ProtoVessel`. `"Kerbalism"` is **not** in `LmpCommon\IgnoredScenarios.cs`, so LMP already ships
the whole node every 30 s (`ScenarioSystem.cs:58,94`).

- [ ] **P3.1 Understand and document that the whole node is already in flight.**
      `DB.Save` writes every vessel plus the global science DB plus `scan_global`. On a busy server
      that is tens-to-hundreds of KB re-serialized every 30 s per client. Measure it.
- [ ] **P3.2 [BLOCKER] Fix last-writer-wins across all vessels.**
      `ScenarioBaseDataUpdater.cs:53` does `CurrentScenarios.AddOrUpdate(...)` — wholesale replace,
      no merge, no lock check, no author tracking. Every client uploads a full `vessels2`, so the
      most recent upload overwrites every vessel's `computer` node globally. Player A editing
      vessel 1 destroys player B's automation on vessel 50.
- [ ] **P3.3 [BLOCKER] Add live application of scenario changes.**
      `LoadScenarioDataIntoGame` (`ScenarioSystem.cs:209`) is called only from `MainSystem.cs:447`
      during `StartGameNow`. Edits made mid-session reach nobody until someone reconnects. This is
      the direct cause of "vessel automation is not synced" from a player's point of view.
      Any live-apply path must be Kerbalism-aware: a generic `OnLoad` re-entry would re-run
      `DB.Load` and reset unrelated state.
- [ ] **P3.4 Resolve the `Script.prev` edge-detector latch.**
      `Computer.Automate` uses `script.prev` (`Script.cs:28`, `Computer.cs:140-204`) and persists it.
      Two clients with identical scripts but different `prev` fire at different times. Either make
      it server-authoritative or reset it on every apply.
- [ ] **P3.5 Gate Kerbalism automation editing on LMP's update lock.**
      Kerbalism's `DevManager` (the automation tab) only checks `Lib.IsControlUnit` (a Serenity
      science-cluster test) and a comms timeout (`UI\DevManager.cs:33`, `UI\TimedOut.cs:12`). It has
      no notion of LMP's `Control`/`Update` lock (`LmpClient\Systems\Lock\LockSystem.cs:65,105`).
      Until this is fixed, any client can edit any vessel's automation from any scene.
- [ ] **P3.6 Decide the ownership split for the `Kerbalism` scenario node.**
      Global content (science subjects, `scan_global`, `landmarks`, storms, kerbal rules) belongs
      on the server; per-vessel content belongs with whoever holds the vessel lock. Either split the
      node into a server-owned global half and a per-vessel half, or add `"Kerbalism"` to
      `IgnoredScenarios.IgnoreSend` and build a dedicated channel. Note the science-subject sync
      currently rides along in the same node, so ignoring it loses that too.
- [ ] **P3.7 Handle `Device.Id` fragility.**
      `Device.Id = PartId + |Name.GetHashCode()|` (`Automation\Device.cs:33-43`). `PartId` is
      `part.flightID` (LMP-synced, stable) but `Name` is a display string derived from
      `GUIName`/`GetModuleTitle()` and can differ across Kerbalism config packs. Re-key on
      `(flightID, moduleName, moduleIndex)` or version the payload and discard mismatches.
- [ ] **P3.8 Treat script application as a pure snapshot.**
      `Script.Execute` calls `Ctrl` for every matching device with no guard. Applying a settings
      node must re-assert state, never fire `Automate`, or every peer will re-run every action.
- [ ] **P3.9 Sync `VesselData.deviceTransmit` and the `cfg_*` toggles.**
      `VesselData.cs:60` (save `:1103`), plus `cfg_ec/supply/signal/malfunction/storm/script/
      highlights/showlink/show` (`:50-58`, save `:1091-1099`). The vessel-wide transmit master
      switch currently controls transmission per client.
- [ ] **P3.10 Sync `dump_specs`, `supplies`, `SunShielding/sspi`, `SystemHeatLoops`, `scansat_id`.**
      `VesselData.cs:1147-1171`. All per-vessel and all gameplay-relevant.
- [ ] **P3.11 Exclude UI-only state from the shared blob.**
      `ui/*`, `cfg_*`, `msg_*` are per-player and currently ride the shared node, causing false
      sharing and merge conflicts. They should be either explicitly local or lock-scoped.

### Hooks that already exist and should be reused

These are the integration points any Kerbalism client-side system must use. Getting them wrong is
the usual cause of "it fights the vessel's owner" bugs.

- [ ] **H.1 Push immediately instead of waiting 30 s.**
      `ScenarioSystem.SendScenarioModuleImmediate(string moduleName)`
      (`ScenarioSystem.cs:118-151`) is a targeted re-send hook and is the natural entry point for
      pushing `Kerbalism` state on edit rather than on the periodic routine.
- [ ] **H.2 Honour the update lock on every apply path.**
      `VesselCommon.DoVesselChecks(vesselId)` (`LmpClient\VesselUtilities\VesselCommon.cs:108-127`)
      is the rule: skip if the vessel is kill-listed, skip if the control lock belongs to this
      player, and skip otherwise-owned vessels. `VesselResource.cs:61-62` and
      `VesselPartSyncCall.cs:30-31` both call it. Any Kerbalism apply path must too.
- [ ] **H.3 Purge per-vessel caches on vessel removal.**
      `VesselCommon.RemoveVesselFromSystems(Guid)` (`VesselCommon.cs:55-68`) already purges
      Position, FlightState, Resource, Proto, all three PartSync systems, Fairings, Couple,
      Decouple and Undock. Any new Kerbalism per-vessel dictionary must be registered here.
- [ ] **H.4 Subscribe to the LMP vessel lifecycle events.**
      `VesselLoadEvent.onLmpVesselLoaded` (fired only on `FreshlyLoaded`,
      `VesselProtoSystem.cs:331`), `VesselReloadEvent.onLmpVesselReloaded` (only on `Reloaded`,
      `:335`), and `VesselInitializeEvent` (from `Harmony\Vessel_Initialize.cs`). Note there is
      **no** `LmpGameEvents` class, and `onLmpVesselLoaded` does **not** fire on the
      SPACECENTER/EDITOR fast path (`VesselProtoSystem.cs:296-315`, `UpdateProtoInPlace`) — so a
      tracking-station reload is invisible to load events.
- [ ] **H.5 Remember that only the proto is written by the field path.**
      `VesselPartSyncField.cs:57` updates `moduleValues` only; `VesselPartSyncUiField.cs:48-63` is
      the single path that writes a live module, and only for bool/int/float under the control
      lock, driven by `LockEvent.onLockAcquire`. If Kerbalism needs remote live modules to converge
      immediately, this is the gap (see P1.11).

---

## 5. Phase 4 — Science, hard drives, experiments

- [ ] **P4.1 Sync `Drive` contents per part.**
      `vessels2/<guid>/parts/<flightID>/drive` (`Science\Drive.cs:79-107`) holds the actual science
      files and samples: `name`, `is_private`, `dataCapacity`, `sampleCapacity`, and per-subject
      `size`, `resultText`, `analyze`, `mass`, `useStockCrediting`. `VesselData.cs:1173-1182` only
      writes it when `partData.Drive != null`.
      This is the largest unsynced gameplay payload and it is currently reachable only through the
      load-time scenario blob.
- [ ] **P4.2 Understand why the science-store trigger never fires.**
      Kerbalism records science directly into the `Drive` and never fires
      `GameEvents.OnExperimentStored` (only `OnScienceRecieved` at `SubjectData.cs:299,302`), so
      `VesselProtoEvents.cs:92`'s full-proto backup is never triggered by Kerbalism science.
- [ ] **P4.3 Sync `VesselData.scienceTransmitted`** (`VesselData.cs:94`, save `:1106`) — it is a
      cumulative score and each client currently keeps its own.
- [ ] **P4.4 Sync `ExperimentResultImage.unlocked` / `observedProducing`** — already declared in
      `ExperimentResultImage.xml` and correctly reachable via `FixedUpdate`. Verify in-game.
- [ ] **P4.5 Decide the scope of the global science DB.**
      `subjectData/<id>/percentRetrieved` (`SubjectData.cs:127-130`), `uncreditedScience`
      (`ScienceDB.cs:511`), `APISubjects`/`sandboxScienceSubjects`, and `scan_global`
      (`ScanCoverageStore.cs:118-140`) are all save-global and must not go through vessel sync.
      They need a single owner — see P3.6.
- [ ] **P4.6 Sync Kerbalism `Preferences*` nodes.**
      `System\Preferences.cs` defines `GameParameters.CustomParameterNode` subclasses
      (`PreferencesReliability`, `PreferencesGeneral`, `PreferencesRadiation`, …). LMP syncs neither
      the nodes nor the values, so a client with a different `mtbfFailures`/`criticalChance`/
      `safeModeChance` produces different failure rolls. Either server-authoritative them or
      require identical configs.

---

## 6. Phase 5 — Reliability, comms, integration modules

- [ ] **P5.1 `Reliability.xml` is the one fully-correct definition** (11/11 fields persistent and
      reachable). Use it as the template. Verify `Break()` propagation in-game — it is called from
      the patched `Update()` (`Reliability.cs:166`, `:513`).
- [ ] **P5.2 Add PartSync XMLs for 18 integration PartModules that have persistent state and
      currently have none.** All are compiled unconditionally, so each is a silent hole:
      - `ProcessControllerSystemHeat` (`CoreDamage`, `SafetyOverride`, `deployed`)
      - `ProcessControllerDeployable` (`deployed`)
      - `SystemHeatRadiatorKerbalism` (`scale`, `scaleEmissionPower`, `IsCooling`)
      - `SHFissionReactorKerbalismUpdater` (5 fields — **reactor output and meltdown**)
      - `SHFissionEngineKerbalismUpdater` (6 fields)
      - `FFTFusionReactorKerbalismUpdater` / `FFTFusionEngineKerbalismUpdater` (4 each)
      - `FFTAntimatterTankKerbalismUpdater` (`ThermalFluxToAddOnLoad`, `ecDeficitSeconds`)
      - `DynamicRadiationController` (10 fields)
      - `SystemHeatConverterKerbalismUpdater`, `SystemHeatHarvesterKerbalismUpdater`,
        `SpaceDustHarvesterKerbalismUpdater`, `SystemHeatCryoTankKerbalismUpdater`,
        `CryoTankKerbalismUpdater`, `NFECapacitorKerbalismUpdater`
      Adding these is safe even when the host mod is absent: `FieldModuleStore.cs:55`
      `FirstOrDefault` silently skips unmatched names.
- [ ] **P5.3 Comms.** The only persisted comms state lives in stock modules and LMP has no XML for
      it: `ModuleDataTransmitter.xml` ships only `xmitIncomplete`, omitting the persistent
      `antennaTransmit`/`omniTransmit`; there is no `CommNetVessel.xml` and no
      `ModuleRTAntenna.xml`. Kerbalism's `vd.Connection` drives transmission rates in
      `Science\Science.cs`, so per-client divergence changes science throughput.
      **Additionally there is an active conflict, not just a gap:** `MainSystem.SetCommNetParams`
      (`LmpClient\MainSystem.cs:488-503`) force-overwrites `CommNetParams` (`plasmaBlackout`,
      `requireSignalForControl`, `rangeModifier`, `occlusionMultiplier*`) on every client from
      server settings, while Kerbalism's `CommHandlerCommNetBase` *patches* CommNet behaviour and
      its `plasmaBlackout` interacts with `Radiation\Radiation.cs`. Decide explicitly whether
      Kerbalism defers to LMP's forced parameters or overrides them per vessel; do not leave it
      accidental.
- [ ] **P5.4 Sync kerbal-level state.** `kerbals/<safeKey>/{eva_dead, disabled, rescue, sickbay,
      rules/*}` (`Database\KerbalData.cs:21-48`). `DB.KillKerbal` (`Kerbalism.cs:1026-1105`) mutates
      kerbal death state on whichever client ran it.
- [ ] **P5.5 Sync storm/CME state.** `bodies/<body|star>` plus per-vessel `StormData` /
      `StormDataByStar` (`Radiation\StormData.cs:40-47`). Each client independently schedules storms
      and computes its own shield draw.
- [ ] **P5.6 Add a per-client mod/capability list.** There is no `ModCollection` anywhere in
      `Server`/`LmpCommon`/`LmpClient` (verified: zero matches), so LMP cannot detect a client
      lacking Kerbalism, SystemHeat, FFT, NFE, CryoTanks, SpaceDust or CommNet before applying a
      node. This blocks P1.4 and P4.5 from being done properly.

---

## 7. Phase 6 — Decide what to do with the plugin itself

- [ ] **P6.1 Decide whether `LmpKerbalismPlugin` should exist.**
      Its only payload (`Ping`/`Pong`/`Notify`) has no client-side sender, so it is dead weight.
      Options:
      - **Delete it** and put everything in `LmpClient`/`LmpCommon` as normal LMP systems
    (consistent with every other LMP subsystem, gets released builds for free — P0.2/P0.3 disappear).
      - **Keep it as a pure data/metadata carrier** (no message protocol), so it only needs the
        XMLs shipped and a plugin DLL that the server can interrogate.
      - **Make it a real transport** for per-vessel Kerbalism blobs (Option D in the FPA analysis),
      which requires solving reliability, persistence and authority below.
- [ ] **P6.2 If it keeps a message protocol, fix the transport first.**
      The channel is broken in both directions today, independently of Kerbalism:
      server→client payloads serialize **zero bytes** because `ModDataSystemSender` never sets
      `NumBytes` (P1.13), and `Reliable` is never set by either sender, so everything goes
      `UnreliableSequenced`. Fix P1.13 before writing a single Kerbalism opcode.
- [ ] **P6.3 Respect the ~1.3 KB payload ceiling** (P1.15). A single vessel's `computer` node is
      small; a whole `vessels2` blob is not. The transport must be strictly per-vessel.
- [ ] **P6.4 If it stores state, add persistence.**
      Nothing in `LmpModInterface`/`ModDataSystemSender` touches disk; the registry in
      `KerbalismModHandler.cs:104-140` is wiped on `OnServerStop` (`KerbalismPlugin.cs:64`).
      `ScenarioStoreSystem.BackupScenarios()` is the pattern to copy.
- [ ] **P6.5 Add authorization.**
      `ModDataMsgReader.cs:18` dispatches purely on the client-supplied `modName` string, so any
      client can claim any mod; `MessageQueuer.RelayMessage<T>` (`:45-51`) fans out to **every**
      connected client with no subspace filter, and there is no lock check on the handler side.
- [ ] **P6.6 Remember there is no client plugin system.**
      `Documentation\PluginDevelopment.md:141-143` states it: the client runs inside KSP and only
      the server supports plugin assemblies; `SystemsHandler` (`:23-43`) reflects over
      `LmpClient`'s own assembly only, so an external DLL cannot register a subsystem. Client-side
      Kerbalism logic must therefore be either a data-driven PartSync XML or a Harmony patch
      following `HarmonyPatcher.PatchOptionalMods()` (`LmpClient\Base\HarmonyPatcher.cs:32-36`)
      — `AccessTools.TypeByName` in a try/catch, returning early with a log line if absent.
      That is the established pattern for optional-mod support and should be followed here.
- [ ] **P6.7 Reconsider the split.**
      LMP has no generic per-vessel blob channel today; the two existing generic channels are
      `ScenarioModule` sync (per-game `ConfigNode`, wholesale replace, load-only) and `PartSyncField`
      (per-part-module field, typed, writes into the proto). A third option is a proper
      `VesselSettings` system shaped like `VesselPartSyncFieldSys`: cost is touching
      `LmpCommon\Enums\{Client,Server}MessageType`, which makes version skew a connection failure
      rather than a missing feature.

---

## 8. Branch code quality — fixes needed regardless of the plan above

- [ ] **Q.1** `LmpClient\Systems\Scenario\ScenarioSystem.cs:529` has a collapsed brace from the
      branch's reformat:
      `{            var scenarioNode = scenarioNode.GetNode(sectionName);`
      Restore the line break.
- [ ] **Q.2** The `ScenarioSystem` reorder comment claims the server "commonly sends Kerbalism
      before ResearchAndDevelopment". Verify that claim, or soften it — the fix (hoisting R&D to
      position 0) is good either way, but the stated justification should be accurate.
- [ ] **Q.3** `VesselResourceMessageSender`/`VesselResource` throttling was changed with a
      `Dictionary` that `Clear()`s wholesale at 1000 entries (`VesselResource.cs`). Confirm this
      cannot be hit spuriously and that the `flightID == 0` skip does not permanently drop a vessel's
      resources until the next full proto.
- [ ] **Q.4** `KerbalismModHandler` comments say the plugin exists so payloads "are accepted,
      validated and answered instead of silently dropped". With no sender, the honest comment is
      "no-op pending Phase 6". Update the docs to match reality (see `Documentation\PluginDevelopment.md`,
      which currently presents this plugin as a working reference example).
- [ ] **Q.5** `Dockerfile_Server` adds a second `dotnet publish` for the plugin but the image is
      self-contained for `Server`; confirm the plugin's own runtime dependencies resolve, or that
      `Private="false"` in `LmpKerbalismPlugin.csproj` is sufficient.
- [ ] **Q.6** Both projects currently build clean (`dotnet build LmpClient.csproj -c Debug` and
      `LmpKerbalismPlugin.csproj -c Debug` both succeed), so there is no compile breakage to fix.
      Keep it that way — add them to a CI build once P0.1 lands.
- [ ] **Q.7 [MAJOR] `TryRestorePreviousVessel` leaves `FlightGlobals.ActiveVessel` dangling.**
      `VesselLoader.cs:906-933` reloads the backed-up proto but never calls
      `ForceSetActiveVessel`. If the failure happened after
      `FlightGlobals.ForceSetActiveVessel(vesselProto.vesselRef)` (`:859`) — i.e. while the
      `reloadingOwnVessel` block was spawning crew — then `CleanUpFailedVesselLoad` destroys the
      very object `ActiveVessel` still points at, and the restored vessel is never made active.
      The result is a null/destroyed active vessel with no log line saying so.
      **Fix:** capture `wasActiveVessel = reloadingOwnVessel` and, after a successful restore,
      `FlightGlobals.ForceSetActiveVessel(restoreProto.vesselRef)` when that flag was set.
- [ ] **Q.8 [MINOR] `CleanUpFailedVesselLoad` does not purge LMP's per-vessel caches.**
      `VesselLoader.cs:264-352` removes the Unity objects and the `protoVessels` entry but never
      calls `VesselCommon.RemoveVesselFromSystems(vesselId)` (`VesselCommon.cs:55-68`). Combined with
      the new restore path, a failed-then-restored vessel leaves stale Position/Resource/PartSync
      entries pointing at a destroyed object. Pre-existing, made more reachable by Q.7's change.
- [ ] **Q.9 [MINOR] The restore is a local-only divergence.**
      `TryRestorePreviousVessel` reinstates the *previous* proto locally while the server already
      holds the new one. That is the right call for a failed reload, but nothing tells the server or
      the other clients, and `VesselLoadOutcome.Failed` is deliberately a no-op in
      `VesselProtoSystem.cs:345-346` (verified: it does not add to `VesselsUnableToLoad`, so the next
      proto update retries). Worth an explicit comment so nobody "fixes" it into a permanent block.
- [ ] **Q.10 [MINOR] `existingVessel.protoVessel?.Save(previousVesselNode)` runs on every destructive
      reload** (`VesselLoader.cs:755-763`). That is a full proto serialization on the main thread per
      reload. Acceptable next to the destroy+`Load` that follows it, but it should be measured if
      large-vessel reload storms are ever a problem.
- [ ] **Q.11 [NIT] The `ScenarioSystem.cs:529` collapsed brace** — see Q.1; both are cosmetic fallout
      from an in-place edit rather than a deliberate reformat.

---

## 9. Verification plan

Without these, none of the above can be claimed as working.

- [ ] **V.1** In-game matrix over two clients, one server:
      flight scene / tracking station / editor × loaded / unloaded vessel ×
      vessel you hold the update lock on / vessel you do not.
- [ ] **V.2** For each Phase 2 XML, flip the field on client A and confirm it lands on client B's
      *proto* **and** on client B's *live* module after reload. Remember P1.11: the field path
      updates the proto only.
- [ ] **V.3** Confirm no client is ever disconnected while running an experiment (P1.1).
- [ ] **V.4** Confirm a single malformed XML dropped into `GameData\LunaMultiplayer\PartSync` does
      not stop LMP from starting (P1.2).
- [ ] **V.5** Automation: A edits vessel 1's scripts, B edits vessel 50's, neither clobbers the
      other; both clients see both changes live, not on reconnect (P3.2, P3.3).
- [ ] **V.6** Science: run an experiment on A, store it on a hard drive, confirm the file and its
      `resultText` are present on B (P4.1).
- [ ] **V.7** Failures: force a failure on A, confirm `broken`/`next`/`quality` converge on B.
- [ ] **V.8** Late join: a client that joins 30 minutes in has the correct automation, science,
      resource and failure state.
- [ ] **V.9** Server restart: state survives a full restart+reconnect cycle (P6.3).
- [ ] **V.10** Mixed-mod server: a client without Kerbalism, and a client without SystemHeat/FFT,
      connect to a server where other players do have them. Must not crash, corrupt the vessel, or
      silently corrupt the scenario node.

---

## 10. Open decisions

1. **Where does the XML payload live?** Plugin `ClientData` (current) vs `LmpClient\ModuleStore\XML\Kerbalism\`.
   Decides whether P0.2/P0.3 are one line each or a new release pipeline.
2. **Core or plugin?** Adding per-vessel Kerbalism channels to `LmpCommon` means client/server
   version skew becomes a hard connection failure. Keeping it in a plugin avoids that but means
   manual distribution.
3. **Live vs load-time sync.** Does Kerbalism state need to converge mid-session? Everything else
   is cheap compared with answering this.
4. **How much of Kerbalism's scenario blob is server-authoritative?** Science subjects, storms,
   landmarks, kerbal death and preferences are save-global; mixing them with per-vessel state in
   one node is the root cause of P3.2.
5. **Failure determinism.** Two clients with different `Preferences*` values roll different
   failures. Server-authoritative parameters, or a required-identical-config handshake?
6. **Scope.** Core Kerbalism only, or the 18 integration modules too? P5.2 is mechanical once
   decided.

---

## Appendix — evidence index

| Claim | Where to verify |
|---|---|
| Plugin protocol has no sender | no caller of `ModApiSystem.SendModMessage` / `ModApiEvent.onModMessageReceived` outside the API definition |
| Plugin not built by the solution | `LunaMultiPlayer.sln` contains no `LmpKerbalismPlugin` |
| Plugin not in releases | `appveyor.yml` has no `LmpKerbalismPlugin` reference |
| XMLs not in client release | `appveyor.yml:87` copies only `LmpClient\ModuleStore\XML` |
| Module identity = file name | `FieldModuleStore.cs:45` |
| Methods-only definitions inert | `PartModulePatcher.cs:35` |
| `uint` receive gap | `VesselPartSyncFieldQueue.cs:22-55`; `Experiment.cs:48`; `HardDrive.cs:27` |
| Kerbalism is a scenario module | `Kerbalism.cs:35-36`; `DB.cs:104-156` |
| Scenario node is not ignored | `LmpCommon\IgnoredScenarios.cs` |
| Scenario applied only at load | `ScenarioSystem.cs:209`, `MainSystem.cs:447` |
| Scenario server does wholesale replace | `ScenarioBaseDataUpdater.cs:53` |
| Automation model is `Computer`/`Script`/`Device` | `Automation\Computer.cs`, `Script.cs`, `Device.cs` — no FPA/Subroutine classes exist |
| No per-client mod list | zero `ModCollection` matches in `Server`/`LmpCommon`/`LmpClient` |
| No lock check on PartSync receive | `Server\Message\VesselMsgReader.cs:53-63` |
| Mod control rejects non-allowlisted parts | `ProtoVesselExtension.cs:34-44`; `Server\System\Vessel\VesselDataUpdater.cs:53-62`; `ModFileSystem.cs:13-33`; `Universe.cs:35-39` |
| Server→client mod payloads are empty | `Server\System\ModDataSystemSender.cs:20-22,35-37`; `ModMsgData.cs:44,59` |
| Pooled message data is not reset | `MessageStore.cs:21-49`; `ModMsgData.cs:9` |
| No fragmentation / ~1.3 KB ceiling | `Lidgren\NetPeerConfiguration.cs:42`; `Lidgren\NetConnection.cs:298-300` |
| Authority gate for apply paths | `LmpClient\VesselUtilities\VesselCommon.cs:108-127` |
| Per-vessel cache purge list | `LmpClient\VesselUtilities\VesselCommon.cs:55-68` |
| Targeted scenario push hook | `LmpClient\Systems\Scenario\ScenarioSystem.cs:118-151` |
| Optional-mod patch pattern to follow | `LmpClient\Base\HarmonyPatcher.cs:32-36,84-112` |
| Client has no plugin/DLL system by design | `Documentation\PluginDevelopment.md:141-143`; `SystemsHandler.cs:23-43` |
| CommNet params force-overwritten | `LmpClient\MainSystem.cs:488-503` vs `Kerbalism\src\Kerbalism\Comms\CommHandlerCommNetBase.cs` |
