# Kerbalism support for LunaMultiplayer

Syncs Kerbalism's multiplayer state. Ships **side by side with LMP**, not bundled into it, so
LMP and this plugin version independently.

## Why it is a separate download

LMP has no Kerbalism code in it, no Kerbalism data files, and no Kerbalism entries in the build or
release pipeline. The only things LMP provides are two generic APIs that any plugin can use:

- a **client-side plugin loader** (`ILmpClientPlugin`, `LmpClient/ClientPlugins/`)
- a **reliable mod message channel** (`ModApiSystem.SendModMessage` / `LmpModInterface`)

## Install

**Server** — drop `LmpKerbalismPlugin.dll` into the server's `Plugins` folder
(`<server>/Plugins/`, alongside `Server.dll`; LMP loads every dll in there). State is persisted to
`<server>/KerbalismSync/vesselstate.bin`.

**Client** — drop `LmpKerbalismPlugin.Client.dll` into
`<KSP>/GameData/LunaMultiplayer/ClientPlugins/`. Create the folder if it does not exist.

That folder is on purpose: LMP's mod-control scanner skips anything under `GameData/LunaMultiplayer`,
so installing the plugin can never invalidate a server's `LMPModControl.xml`.

No `LMPModControl.xml` changes are needed. The bundled allowlist already contains Kerbalism parts.

## What it syncs

**Through this plugin's own channel** — Kerbalism keeps this state in `VesselData`, not in the
part protos, so LMP cannot reach it:

- automation (`Computer`, i.e. Kerbalism 2.x's automation, not a 3.x FPA graph)
- the vessel-wide data transmit switch
- storm / CME state per star
- SystemHeat background loop calibration
- dump-valve selection per process
- science files and samples on hard drives, and the science total

**Through LMP's existing part-sync transport** — the plugin detects changes and pushes them through
LMP's own path, which gives server-side persistence and late-join delivery for free:

- all persistent fields of 33 Kerbalism part modules, including the SystemHeat, Far Future
  Technologies, CryoTanks, Near Future Electrical, SpaceDust, PlanetSideExploration and Dynamic
  Radiation integrations

**Already handled by LMP, deliberately untouched:** part resource amounts
(`VesselResourceSys`), full vessel protos (`VesselProtoSys`), crew, contract and funding state.

## Requirements

- LMP with the client plugin API (this branch or later)
- Kerbalism 2.x on every client that plays with Kerbalism. A client without Kerbalism logs one line
  at startup and stays completely inactive.
- All players should run the same Kerbalism version. Part fields are matched by module class name
  and field name, so a rename in a new Kerbalism release simply means those fields stop syncing.

## Reading the log

The plugin logs one line at startup naming how many part modules and fields it is watching, and
which optional Kerbalism structures were unavailable. Server rejections are logged with the reason
*and* the current lock owner, so a "my automation disappeared" report is diagnosable without a
reproduction.

## Design notes

`Documentation/KerbalismPluginPlan.md` has the full architecture, the task list and what is still
unverified. `Documentation/KerbalismSupportTODO.md` is the audit of what the previous approach got
wrong and why the plugin diffs state instead of trying to observe writes.
