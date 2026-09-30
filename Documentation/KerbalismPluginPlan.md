# Kerbalism plugin — architecture and implementation plan

Companion to `KerbalismSupportTODO.md`, which is the defect audit that motivates this work.

**Status: the core is implemented and builds.** All five projects compile clean. What is
*not* done is in-game verification (section F) — no one has run two clients yet.

## Constraints this design is built around

1. **The Kerbalism support ships side by side with LMP, not bundled into it.** No Kerbalism
   types in `LmpClient`, no Kerbalism XMLs in `LmpClient\ModuleStore\XML`, no Kerbalism
   entries in `LunaMultiPlayer.sln`, no `appveyor.yml` changes, no `LMPModControl.xml` edits.
2. **Any LMP-side change must be generic** — usable by any future plugin.
3. **Real-time.** No "applies at next load", no 30-second poll.
4. **Whatever LMP already syncs natively is left alone.** Part resources go through
   `VesselResourceSys`; persistent `PartModule` fields go through LMP's PartSync transport;
   vessel protos go through `VesselProtoSys`.

## The core insight

The 21 PartSync XMLs this branch used to ship depended on LMP's Harmony transpiler to *notice*
field writes. That mechanism only instruments five Unity lifecycle method names, `KSPAction`s,
visible `KSPEvent`s, and XML-listed names (`PartModulePatcher.cs:38-45`), and it only compares a
field against its own value across that method's frame. Kerbalism writes from `OnGUI`
(`Configure.cs:399-411`), `IAnimatedModule` callbacks (`Harvester.cs:583-584`), `set_State`
properties (`Experiment.cs:79-90`), `PopupDialog` lambdas (`SolarPanelFixer.cs:187-194`) and other
classes' frames — none observable. The audit found a dozen-plus write paths no XML can reach.

**So the plugin does not try to observe writes. It diffs state instead.** That is immune to where
the write came from, which is the actual root problem, and it is why the XMLs are gone rather than
fixed.

## Architecture

```
LMP core — generic additions only
─────────────────────────────────
LmpClient/ClientPlugins/
  ILmpClientPlugin.cs          lifecycle contract (mirrors the server's ILmpPlugin)
  ILmpClientPluginContext.cs   send/receive a mod channel, KspPath, PlayerName, IsNetworkRunning
  LmpClientPluginHandler.cs    loads ClientPlugins/*.dll, fires hooks, survives plugin misbehaviour
  MainSystem.Awake/Update/OnExit + the NetworkState setter drive it
Bug fixes that are generic, not Kerbalism-specific
  ModDataSystemSender          set NumBytes + Reliable  (server→client payloads were EMPTY)
  ModApiSystem                 set Reliable, validate numBytes
  ModMsgData                   IMessageDataResettable + clamped serialisation + length validation
  VesselPartSyncFieldQueue     the 5 missing field types; never throws
  VesselPartSyncUiFieldQueue   never throws
  VesselPartSyncUiField        safe Fields[] lookup
  FieldModuleStore             fail-soft XML loading + a "what actually loaded" line
──────────────────────────────────────────────────────────────────────────────────
LmpKerbalismPlugin/          server, net10.0 -> Server/bin/<cfg>/net10.0/Plugins/
  KerbalismPlugin.cs          dispatch, watches, fan-out
  KerbalismStateStore.cs      the store, disk persistence, pruning
  KerbalismAuthority.cs       lock-based write authority + reject reasons
  Shared/Protocol.cs          wire format and chunking (compiled into BOTH halves)

LmpKerbalismPlugin.Client/   client, net472  -> GameData/LunaMultiplayer/ClientPlugins/
  KerbalismClientPlugin.cs    lifecycle, tracking, the two diff loops
  KerbalismApi.cs             all reflection into Kerbalism, resolved once
  KerbalismVesselState.cs     VesselData capture (strip) / apply (in place)
  DriveState.cs               science drives, the one path that needs manual care
  KerbalismPartFields.cs      the field table that replaces the 21 XMLs
```

`GameData/LunaMultiplayer/ClientPlugins/` is deliberate: `ModSystem.GetModFiles()` skips any
`GameData` folder starting with `lunamultiplayer` (`ModSystem.cs:196`), so a client plugin there is
invisible to mod control and can never make a server's `LMPModControl.xml` go stale. KSP does not
scan the folder, so the assembly is loaded exactly once, by LMP.

### Data flow

**Per-vessel Kerbalism state** (`VesselData`: automation, `deviceTransmit`, `scienceTransmitted`,
storms, SystemHeat calibration, dump valves, per-part science drives):

```
lock-holder client                       server                      other clients
  every 2s: VesselData.Save -> strip
  per-player values -> digest
  changed?  --VesselState(rev)-------->  authority check, store
                                        |-- push to watchers -------->  apply in place
  on tracking: WatchVessel ----------->  reply with stored state --->  apply in place
```

**Persistent part-module fields** (`ProcessController.running`, `Reliability.broken`, …) keep
LMP's PartSync transport, which already persists server-side and survives late join. The plugin
diffs each declared field every second and fires LMP's **public**
`PartModuleEvent.onPartModule*FieldChanged` events; `VesselPartSyncFieldEvents.CallIsValid`
returns `true` for modules with no registered definition (`:29-42`), so no XML is needed.

**Live convergence.** LMP's receiver writes only the proto (`VesselPartSyncField.cs:57`), so a
remote vessel's live module stays stale until reload. The plugin subscribes to the `*Processed`
events and mirrors the value into `moduleRef.Fields[...]`, skipping the vessel owner and
unresolvable fields.

### Two rules the apply path must not break

Both learned from Kerbalism's own loader, and both are why `KerbalismVesselState` is hand written
rather than a generic ConfigNode walker:

1. **Never replace the `VesselData` instance.** A loaded `HardDrive` holds a permanent alias to
   the `Drive` inside the old one (`HardDrive.cs:126`), so a new instance orphans every drive and
   reads and writes of science storage diverge permanently.
2. **Never call `VesselData.Load`, and never `new Drive(ConfigNode)`.** Their `Drive` constructor
   calls `AddDataCollectedInFlight` without removing the previous contribution
   (`Drive.cs:41`), so every apply inflates the global `ScienceCollectedInFlight` counter.
   `DriveState` instead removes each old contribution, clears, repopulates via Kerbalism's own
   `File.Load`/`Sample.Load`, and adds each contribution exactly once.

`scienceTransmitted` is also max-merged, never overwritten downward: it is a lifetime counter, so
overwriting it would undo science another player legitimately collected.

## Task list

### A. LMP core — generic only — **DONE**

- [x] **A.1** `ILmpClientPlugin` — `OnAwake(context)`, `OnEnabled`, `OnDisabled`, `OnUpdate`, `OnDestroy`.
- [x] **A.2** `ILmpClientPluginContext` — `KspPath`, `PluginDirectory`, `IsNetworkRunning`,
      `PlayerName`, `SendModMessage`, `RegisterModHandler`.
- [x] **A.3** `LmpClientPluginHandler` — recursive load of `ClientPlugins/*.dll`, `AssemblyResolve`
      fallback, per-plugin and per-hook try/catch, and it disables a plugin that throws in
      `OnUpdate` rather than flooding the log.
- [x] **A.4** Wired into `MainSystem.Awake()` (after base events, before Harmony), `Update()`, the
      `NetworkState` setter, and `OnExit()`.
- [x] **A.5** `ModDataSystemSender` sets `NumBytes` and `Reliable`. Server→client mod payloads used
      to serialise **zero bytes**.
- [x] **A.6** `ModApiSystem.SendModMessage` takes `reliable` (default true) and validates `numBytes`.
- [x] **A.7** `IMessageDataResettable` (opt-in, so the ~100 existing message classes still compile)
      + `ModMsgData` implements it; `InternalSerialize` clamps, `InternalDeserialize` validates.
- [x] **A.8** `VesselPartSyncFieldQueue` handles Short/UShort/UInteger/Long/ULong and never throws.
      This was a **disconnect**: `uint privateHdId` and `uint hdId` both hit
      `default: throw new ArgumentOutOfRangeException()`.
- [x] **A.9** `FieldModuleStore` fails soft on a missing folder / malformed file / duplicate name,
      and logs one summary line of what actually resolved.
- [x] **A.10** `VesselPartSyncUiFieldQueue` no longer throws; `VesselPartSyncUiField` guards the
      `Fields[FieldName]` lookup that throws on non-`BaseField` names.
- [x] **A.12** All five projects build clean.

Not done: **A.11** (server-side `"Kerbalism"` scenario suppression) — see open decisions. The
client does not upload the blob; the server still stores whatever reaches it, which is harmless
but not yet tidied.

### B. Protocol — **DONE**

- [x] **B.1** `Protocol.cs`: version byte + opcode + body, opcodes Hello / RequestVessel /
      VesselState / RequestGlobal / GlobalState / WatchVessel / UnwatchVessel /
      VesselStateResponse / GlobalStateResponse / UnknownScope / Rejected.
- [x] **B.2** Deflate + chunking with a 7-byte header carrying total, index and a message id,
      because the reassembler must bucket chunks of one message together and this Lidgren build
      has no fragmentation (MTU 1408).
- [x] **B.3** Per-player strip list: `cfg_*`, `msg_*`, `supplies`.

### C. Server — **DONE**

- [x] **C.1** Store keyed by vessel GUID, plus a global entry.
- [x] **C.2** Validate, reassemble, check authority via `Server\System\LockSystem.LockQuery`,
      store, reject stale revisions, fan out to watchers only.
- [x] **C.3** `UnknownScope` so a client can tell "nothing stored" from "empty state" and does not
      wipe its local state.
- [x] **C.4** Disk persistence next to the server data dir, debounced, plus save on stop.
- [x] **C.5** Prune against `VesselStoreSystem.VesselExists` once a minute.
- [x] **C.6** Diagnostics: connected clients, prune counts, rejections with the current owner.

### D. Client — **DONE except D.10**

- [x] **D.1** Presence detection by resolving Kerbalism's types; absent → one log line, disabled.
- [x] **D.2** All reflection resolved once in `KerbalismApi.TryResolve`, cached afterwards.
- [x] **D.3** Tracking via load/reload events **and** a periodic sweep, because the tracking-station
      fast path (`VesselProtoSystem.cs:296-315`) fires no load event.
- [x] **D.4** `WatchVessel` on first sight, which also gets the server's reply.
- [x] **D.5** `Capture` → strip → compare → send, every 2 s, only for lock holders.
- [x] **D.6** In-place apply, never for a vessel we hold.
- [x] **D.7** Part-field diff every 1 s, firing LMP's public events.
- [x] **D.8** `*Processed` → live mirror, owner skipped, unknown fields skipped.
- [x] **D.9** Field table for 33 Kerbalism modules including the SystemHeat / FFT / NFE / CryoTanks /
      SpaceDust / DynamicRadiation integrations. `*ModuleID`-style cross-references are
      deliberately excluded: they must match the local part config, not the sender's.
- [ ] **D.10 Global state** — the opcodes and store slot exist, but nothing populates them.
      Blocked on the authority decision below.
- [x] **D.11** Per-hook try/catch; state dropped on disconnect; vessel removal untracks.

### E. Packaging — **DONE except E.4/E.5**

- [x] **E.1/E.2** Two projects. Server DLL deploys to `Server\bin\<cfg>\net10.0\Plugins`; client
      DLL deploys to `$(KSPPATH)\GameData\LunaMultiplayer\ClientPlugins` and is the only file in
      its output (verified).
- [x] **E.3** `ClientData/PartSync/Kerbalism/*.xml` deleted.
- [ ] **E.4** Document the client plugin API in `Documentation/PluginDevelopment.md`, and correct
      its existing claim that LMP has no client-side extensibility.
- [x] **E.5** No `LMPModControl.xml` change needed. The bundled allowlist already has ~127
      Kerbalism part names.

### F. Verification — **NOT STARTED**

Everything above is unverified at runtime. This is the most important remaining work.

- [ ] **F.1** Two clients: flip a Kerbalism toggle on A, see it on B within one poll interval, B
      holding no lock.
- [ ] **F.2** A edits vessel 1's automation while B edits vessel 50's; neither clobbers the other.
      This is the failure the old design had.
- [ ] **F.3** Third client joins mid-session with correct automation and science.
- [ ] **F.4** A vessel the server has never seen: local state kept, not wiped.
- [ ] **F.5** Server restart: state survives.
- [ ] **F.6** Client without Kerbalism: plugin disables itself, no exceptions, no impact.
- [ ] **F.7** A Kerbalism part with a `uint` field syncs without disconnecting anyone (A.8).
- [ ] **F.8** A remote loaded vessel's live modules converge, not just the proto.
- [ ] **F.9** Vanilla LMP server, plugin absent on both sides: no regression.
- [ ] **F.10** 30-minute soak: no unbounded growth in the server store or the client's caches.
- [ ] **F.11** **Automation device ids.** `Computer`'s device ids come from
      `PartId + |Name.GetHashCode()|` (`Device.cs:38`). Mono's string hashing is expected to be
      deterministic across processes, but if any runtime in use randomises it, no script will ever
      transfer and this must be diagnosed from a log line rather than guessed at.

## Open decisions

1. **Global state authority (D.10).** `scienceDB`/subjects, `scan_global`, kerbal data and storms
   are save-global. Options: the client holding any vessel lock (implemented, permissive), a
   dedicated LMP lock, or server-computed. Currently implemented as "any vessel-lock holder wins",
   which is a policy, not a proof.
2. **Poll interval.** 2 s for vessel state and 1 s for part fields are guesses. The cost is one
   `VesselData.Save` per tracked vessel per 2 s; measure before tuning.
3. **Whether to make the live mirror (D.8) generic.** "PartSync updates the proto but not the live
   module" affects vanilla parts too. A generic LMP fix would be better, but it is a broader
   behavioural change to core; keeping it in the plugin is the lower-risk option for now.
4. **A.11.** Whether to suppress the `"Kerbalism"` scenario module server-side once the plugin is
   active, to remove the last vestige of the last-writer-wins blob.
