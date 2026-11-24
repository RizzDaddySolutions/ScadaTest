# ScadaTest

ScadaTest is a Windows-first .NET 10 framework for automated functional testing of SCADA systems. It drives the system through its data layer, then checks the result in the data (tags, alarms, history) and on the HMI (what the operator sees). AVEVA System Platform 2023+ with InTouch OMI is the first target; Geo SCADA is next.

Tests are written mostly as YAML scenarios by commissioning engineers. C# tests are available for framework developers.

```yaml
id: PUMP-P101-START
title: P-101 start command animates the pump
requires: [tags, hmi]
steps:
  - open_screen: Area200_Overview
  - write: { tag: P-101.StartCmd, value: true }
  - expect_tag: { tag: P-101.Running, equals: true, within: 10s }
  - expect_symbol: { symbol: P-101, state: running, within: 3s }
```

## Quick start

Requires the .NET 10 SDK.

```powershell
dotnet build
dotnet test
# Run the sample scenarios against the built-in in-memory demo plant (no SCADA system needed):
dotnet run --project src/ScadaTest.Runner -- run samples/Plant.Tests --env demo --junit results.xml
```

Other commands: `scadatest list <path> [--filter smoke]` and `scadatest validate <path>`.

## Solution layout

| Project | What it does |
| --- | --- |
| `ScadaTest.Core` | Vendor-neutral interfaces (`ITagClient`, `IAlarmClient`, `IHistoryClient`, `IHmiDriver`, `ISimulatorControl`), the session, write guard and tag restore, fluent expectations |
| `ScadaTest.Scenarios` | YAML scenario loader, built-in steps, scenario executor |
| `ScadaTest.Reporting` | Console, JUnit XML and JSON reports |
| `ScadaTest.Runner` | The `scadatest` CLI (packable as a `dotnet tool`), environments, adapter discovery |
| `ScadaTest.Adapters.OpcUa` | Tags over OPC UA for any server, plus simulator control through OPC UA methods |
| `ScadaTest.Adapters.Historian` | AVEVA Historian tag history over SQL |
| `ScadaTest.Adapters.InMemory` | An in-memory plant for developing tests and steps without a SCADA system |
| `ScadaTest.Hmi.Omi` | Windows only: OMI ViewApp driver (UI Automation navigation, pixel-sampled symbol state) |
| `ScadaTest.Adapters.Aveva.MxHost` | Planned: out-of-process MXAccess host for alarms and Galaxy attributes (see its README) |

## Writing scenarios

Files named `*.scada.yaml` are picked up recursively. Each has `id`, optional `title`, `requires` and `tags`, and `setup`, `steps` and `teardown` lists. Teardown always runs, and every tag a test writes is restored to its original value afterwards.

| Step | Example |
| --- | --- |
| `write` | `- write: { tag: P-101.StartCmd, value: true }` |
| `wait` | `- wait: 2s` (prefer `expect_*` with `within`) |
| `open_screen`, `click`, `capture` | `- open_screen: Area200_Overview` |
| `ack` | `- ack: { alarm: LT-301.Hi, comment: "automated test" }` |
| `sim_load`, `sim_set`, `sim_fault`, `sim_reset` | `- sim_fault: { device: P-101, fault: trip }` |
| `expect_tag` | `- expect_tag: { tag: TT-201.PV, equals: 60.0, tolerance: 0.5, within: 10s }` or `between: [55, 65]` |
| `expect_alarm` | `- expect_alarm: { source: LT-301, condition: Hi, state: active_unacked, priority: 500, within: 2s }` |
| `expect_symbol` | `- expect_symbol: { symbol: P-101, state: running, within: 3s }` |
| `expect_history` | `- expect_history: { tag: TT-201.PV, contains: [20.0, 60.0], tolerance: 0.5 }` or `{ alarm: LT-301.Hi, events: [raise, ack, rtn] }` |

Durations are written as `500ms`, `10s`, `2m`. Custom steps implement `IStepHandler` and are added to the `StepRegistry`.

## Environments and safety

`environments.yaml` (next to the scenarios, or passed with `--config`) defines each target: its adapters, which adapter provides each capability, the tag map, wait defaults and write rules. See [samples/Plant.Tests/environments.yaml](samples/Plant.Tests/environments.yaml).

- `allowWrites` and `writablePatterns` limit what a test may write; every write is logged with old and new values.
- `productionHosts` lists hosts the runner must never touch; any adapter setting that names one stops the run.
- Tests use logical names (`P-101.Running`); the tag map translates them per adapter, so a test suite moves between sites by swapping the map.

## Status

This is the initial scaffold (roadmap phases 1 and 2 of the design). Not yet verified against a live system:

- OPC UA adapter against OI Gateway / the System Platform 2023 OPC UA server and the custom simulator (method names in `environments.yaml` are placeholders).
- Historian `History` query and the site-specific `alarmEventsQuery`.
- OMI pixel sampling, which is the main question for the spike.

The phase-1 spike that answers these on the test VM is in [spike/](spike/README.md): small console programs with a run checklist.

Planned next: the MXAccess host (alarms), an HTML report, spreadsheet import, GRAccess test generation, and the Geo SCADA adapter.
