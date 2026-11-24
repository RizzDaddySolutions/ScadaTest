# ScadaTest.Adapters.Aveva.MxHost (planned)

MXAccess and GRAccess are 32-bit COM components that need an STA thread with a message pump, and they ship with System Platform rather than on NuGet. They can't load into the .NET 10 x64 runner, so they will run in a small separate host:

- .NET Framework 4.8, x86, referencing `ArchestrA.MxAccess.dll` from the System Platform install on the test VM.
- Talks to the runner over named pipes (or gRPC), exposing `ITagClient` and `IAlarmClient` (alarm sub-attributes such as `LT301.PV.Hi.InAlarm` and `.Acked`).
- A crash in COM code takes down only the host, not the test run.
- GRAccess (read-only) later adds test generation: one alarm test per configured alarm, one animation test per pump instance.

This project is intentionally not in the solution yet, because it can only be built on a machine with System Platform installed. It is roadmap phase 2.
