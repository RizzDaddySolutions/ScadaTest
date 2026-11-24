# Phase-1 spike: run checklist

Small console programs that prove the four connections ScadaTest depends on, on the dedicated test VM (System Platform 2023, dev Galaxy with a pump and an alarm object, an OMI ViewApp, Historian and the custom OPC UA simulator). Each one prints `PASS`, `FAIL`, `WARN` and `INFO` lines, ends with a `DONE PASS` or `DONE FAIL` summary, exits 0 or 1, and writes the same lines to `spike-results\<program>-<command>-<time>.log`.

When you're done, zip the `spike-results` folder (logs, screenshots and the UIA tree dump) and send it back along with the verdict table at the bottom.

| # | Program | Question it answers | Uses the real code |
| --- | --- | --- | --- |
| 1 | `spike-omi` | Can we tell a pump symbol's state from screen pixels, reliably and quickly? (riskiest, so it goes first) | `OmiViewAppDriver`, `HmiMap`, `ColorClassifier`, `OpcUaAdapter` |
| 2a | `spike-opcua` | Can we read and write Galaxy attributes over OPC UA? | `OpcUaAdapter` |
| 2b | `spike-mxaccess` | Can we read and write attributes, including alarm sub-attributes, over MXAccess? | Prototype for the planned MxHost |
| 3 | `spike-historian` | Do the History queries return the right samples and times, and where is alarm history? | `HistorianAdapter` |
| 4 | `spike-sim` | Can we drive the simulator's scenario, fault and reset methods? | `OpcUaAdapter` as `ISimulatorControl` |

Every program prints its options when run with no arguments.

## 0. Set up and build

- [ ] Install the .NET 10 SDK on the VM (`dotnet --version` shows 10.0.x).
- [ ] Clone the repo and build from the repo root:
  ```powershell
  powershell -ExecutionPolicy Bypass -File spike\build-spike.ps1
  ```
  If the MXAccess part can't find `ArchestrA.MxAccess.dll`, locate it and pass the path:
  ```powershell
  cmd /c dir /s /b "C:\Program Files (x86)\ArchestrA\ArchestrA.MxAccess.dll"
  powershell -ExecutionPolicy Bypass -File spike\build-spike.ps1 -MxAccessDll "<path from above>"
  ```
- [ ] Set display scaling and resolution to what tests will always use (100% is simplest), log in to the VM's console or RDP session, and start the ViewApp on the screen with the pump.
- [ ] Note the names you'll need. The examples below use these, so replace them with yours:

| Placeholder | Example | Where to find it |
| --- | --- | --- |
| Galaxy OPC UA endpoint | `opc.tcp://localhost:4840` | OI Gateway or the platform OPC UA server configuration |
| Simulator endpoint | `opc.tcp://localhost:4841` | Your simulator |
| Pump command NodeId | `ns=2;s=P101.StartCmd` | Step 2a `browse` |
| Pump running NodeId | `ns=2;s=P101.Running` | Step 2a `browse` |
| Galaxy attribute names | `P101.Running`, `LT301.PV.Hi` | The IDE |
| Historian tag | `P101.Running`, `LT301.PV` | Step 3 `find` |

```powershell
$S = "spike\out"   # shorthand used below
```

## 1. OMI symbol state from screenshots (do this first)

The goal is a verdict on whether pixel sampling can tell running, stopped and fault apart, within the design's 3 s HMI wait, every time.

- [ ] **Find the ViewApp process.**
  ```powershell
  & $S\spike-omi.exe windows
  ```
  Success: a line for the ViewApp window. Use its process name as `--process` from now on (the examples use `ViewApp`).

- [ ] **Check that the window can be captured.**
  ```powershell
  & $S\spike-omi.exe capture --process ViewApp
  ```
  Success: `PASS Screen capture ... saved to spike-results\omi-images\window-screen-*.png`. Open the PNG and check it shows the pump. The PrintWindow result is informational: `PASS` means OMI paints through PrintWindow (captures could then work when the window is covered); `WARN ... is black` means we stick with screen capture.

- [ ] **Measure the pump symbol's region.**
  ```powershell
  & $S\spike-omi.exe cursor --process ViewApp --seconds 30
  ```
  Hover over the pump's top-left corner, then its bottom-right corner. Use the `window-relative` numbers to work out `x, y, width, height`, and write them into `samples\Plant.Tests\hmimap.yaml` under `P-101: region:`. Pick the part of the symbol that changes colour (the pump body), not the whole symbol with its label.

- [ ] **Sample it in each state and set the reference colours.** With the pump stopped:
  ```powershell
  & $S\spike-omi.exe sample --process ViewApp --map samples\Plant.Tests\hmimap.yaml --symbol P-101
  ```
  Open `omi-images\P-101-window-*.png`: the magenta box must sit on the pump. Read the `Dominant colours` line and put the main colour in `hmimap.yaml` as `stopped`. Start the pump, run `sample` again and record `running`. Fault the pump (for example with `spike-sim call --method InjectFault --arg P-101 --arg trip`, step 4) and record `fault`. If the fault animation flashes, sample a few times and note both colours.

  Then confirm each state strictly, for example while running:
  ```powershell
  & $S\spike-omi.exe sample --process ViewApp --map samples\Plant.Tests\hmimap.yaml --symbol P-101 --expect running
  ```
  Success: `PASS Average classifier says 'running'` and `PASS Share classifier says 'running'`, and the last line, `OmiViewAppDriver.GetSymbolStateAsync says: running`, agrees. If only the share classifier passes, the region includes too much background; note it in the verdict, because the driver would need the share classifier.

- [ ] **Drive the pump from code and time the symbol.** This is the main proof.
  ```powershell
  & $S\spike-omi.exe verify --process ViewApp --map samples\Plant.Tests\hmimap.yaml --symbol P-101 `
      --endpoint opc.tcp://localhost:4840 --node "ns=2;s=P101.StartCmd" --on true --off false `
      --on-state running --off-state stopped --feedback "ns=2;s=P101.Running" --cycles 10 --within 3s --auto-accept
  ```
  If your pump uses separate start and stop commands, point `--node` at a single attribute that sets the run state (for example the simulator's running input) instead. Success: `PASS Average classifier (today's driver) followed 20/20 changes within 3s. Latency min / avg / max`, and the same for the share classifier. The `feedback tag` timings show how much of the latency is the tag and how much is the HMI.

- [ ] **Check the conditions that break screen capture.** Run a watch and, during it, toggle the pump, then cover the ViewApp with another window for a few seconds, then minimize the RDP client (if you use RDP):
  ```powershell
  & $S\spike-omi.exe watch --process ViewApp --map samples\Plant.Tests\hmimap.yaml --symbol P-101 --seconds 90
  ```
  Expected: states follow the toggles while the window is visible. Covering or minimizing will probably give `unknown` or `FAIL ... samples were black`, which is what we need to record for the verdict (it decides the auto-logon/console-session requirement).

- [ ] **Check whether UI Automation can navigate screens.**
  ```powershell
  & $S\spike-omi.exe tree --process ViewApp --depth 12
  ```
  Success: `PASS UI Automation sees N elements ...`. Open `spike-results\omi-uia-tree-*.txt` and look for the navigation buttons that open other screens: an `AutomationId` or `Name` for them means `open_screen` can work through UIA. A `WARN No AutomationIds` means navigation would click by position.

## 2a. Galaxy tags over OPC UA

- [ ] **Connect.**
  ```powershell
  & $S\spike-opcua.exe connect --endpoint opc.tcp://localhost:4840 --auto-accept
  ```
  Success: `PASS Connected: ... server <name>` and the namespace list. If it fails with a certificate error, trust the `ScadaTest` certificate on the server (it lands in the server's rejected-certificates folder) and rerun. Try `--secure` too and note which one the server accepts.

- [ ] **Find the pump and alarm attributes.**
  ```powershell
  & $S\spike-opcua.exe browse --endpoint opc.tcp://localhost:4840 --auto-accept --depth 4 --filter P101
  ```
  Success: NodeIds for `P101.StartCmd`, `P101.Running` and `LT301.PV`. Copy them into `samples\Plant.Tests\tagmap.yaml`.

- [ ] **Read them.**
  ```powershell
  & $S\spike-opcua.exe read --endpoint opc.tcp://localhost:4840 --auto-accept --node "ns=2;s=P101.Running" --node "ns=2;s=LT301.PV"
  ```
  Success: a `PASS` per node with quality `Good`, the expected .NET type (`Boolean`, `Double`, `Single`) and a recent timestamp.

- [ ] **Write and restore.**
  ```powershell
  & $S\spike-opcua.exe roundtrip --endpoint opc.tcp://localhost:4840 --auto-accept --node "ns=2;s=LT301.PV" --value 92
  ```
  Success: `PASS Read back 92 after N ms` and `PASS Restored`. If the simulator immediately overwrites the PV, use a setpoint or a writable UDA instead and note it.

## 2b. Galaxy tags and alarms over MXAccess

Run from a normal (non-elevated) prompt on the platform node. The program is 32-bit on purpose.

- [ ] **Read.**
  ```powershell
  spike\out-x86\spike-mxaccess.exe read --item P101.Running --item LT301.PV --item LT301.PV.Hi.InAlarm --item LT301.PV.Hi.Acked
  ```
  Success: `PASS Running as a 32-bit process`, `PASS Registered with MXAccess`, then a `PASS` line per item with quality 192.

- [ ] **Write and restore.**
  ```powershell
  spike\out-x86\spike-mxaccess.exe roundtrip --item LT301.PV --value 92
  ```
  Success: `PASS Write 92 to LT301.PV: ok`, `PASS Data change shows 92`, `PASS Restored`.

- [ ] **Watch an alarm through its lifecycle.** Start the watch, then drive LT-301 above its Hi limit (simulator or step 2a `roundtrip`), acknowledge it in OMI, and bring the level back:
  ```powershell
  spike\out-x86\spike-mxaccess.exe watch --item LT301.PV --item LT301.PV.Hi.InAlarm --item LT301.PV.Hi.Acked --seconds 120
  ```
  Success: `InAlarm` goes true, `Acked` goes true after your ack, `InAlarm` goes false, each with a timestamp.

- [ ] **Acknowledge from code.** Raise the alarm again, then:
  ```powershell
  spike\out-x86\spike-mxaccess.exe ack --alarm LT301.PV.Hi --comment "ScadaTest spike"
  ```
  Success: `PASS LT301.PV.Hi.Acked = True after writing AckMsg`. If it fails, note the write status line; the ack attribute name may differ in your template.

If the build or the first run fails with a type or event signature error, send the compiler message: the MXAccess interop was written from the documented API and hasn't been compiled against the real DLL yet.

## 3. Historian over SQL

Use the connection string for your Historian (the default is `Server=localhost;Database=Runtime;Integrated Security=true;TrustServerCertificate=true`; pass another with `--connection "..."`).

- [ ] **Find the historized names.**
  ```powershell
  & $S\spike-historian.exe find --like "LT301%"
  ```
  Success: `PASS N tags match`, including `LT301.PV`.

- [ ] **Query history and check time zones.**
  ```powershell
  & $S\spike-historian.exe check --tag LT301.PV --minutes 15
  ```
  Success: `PASS Tag ... exists`, `PASS Delta query returned N samples`, `PASS Cyclic 1-minute query returned about 16 samples`, and `PASS Newest sample ... is behind now by 0.x min`. A whole-hour difference there means the UTC handling is wrong; send the log.

- [ ] **Time the storage delay.** This writes through OPC UA and waits for the value to show up in History:
  ```powershell
  & $S\spike-historian.exe roundtrip --tag LT301.PV --endpoint opc.tcp://localhost:4840 --auto-accept --node "ns=2;s=LT301.PV" --value 77.7
  ```
  Success: `PASS 77.7 appeared in History after N s`. The line suggests a history wait to use.

- [ ] **Find alarm history.** Run after the alarm lifecycle in step 2b so there are events to see:
  ```powershell
  & $S\spike-historian.exe alarms
  ```
  Success: `PASS Found N alarm/event tables or views`, with sample rows. Using those columns, write a query returning `EventTime` (UTC) and `EventKind` (`raise`, `ack` or `rtn`) for `@source`, `@condition`, `@from` and `@to`, save it as `alarm.sql` and test it:
  ```powershell
  & $S\spike-historian.exe alarms --query alarm.sql --source LT301 --condition Hi --minutes 120
  ```
  Success: `PASS alarmEventsQuery returned N events ... with kinds Raise, Ack, ReturnToNormal`. If you can't work out the query, send the `alarms` log and I'll write it.

## 4. Simulator methods

- [ ] **List the methods.**
  ```powershell
  & $S\spike-sim.exe browse --endpoint opc.tcp://localhost:4841 --auto-accept --object "ns=2;s=Simulator"
  ```
  Success: `PASS ... has N methods`, with each method's arguments and types. Copy the NodeIds into `environments.yaml` under `sim:` (they are placeholders today).

- [ ] **Call one directly.**
  ```powershell
  & $S\spike-sim.exe call --endpoint opc.tcp://localhost:4841 --auto-accept --method LoadScenario --arg <scenario-name>
  ```
  Success: `PASS Called ... in N ms`, plus any output arguments.

- [ ] **Run the full sequence through the adapter.**
  ```powershell
  & $S\spike-sim.exe check --endpoint opc.tcp://localhost:4841 --auto-accept --scenario <scenario-name> `
      --fault-device P-101 --fault trip --watch "ns=2;s=P101.Running" `
      --object "ns=2;s=Simulator" --load "ns=2;s=Simulator.LoadScenario" --reset "ns=2;s=Simulator.Reset" `
      --fault-node "ns=2;s=Simulator.InjectFault" --state "ns=2;s=Simulator.State"
  ```
  Success: `PASS OpcUaAdapter connected ... capabilities Tags, Simulator`, a `PASS` for each of Reset, LoadScenario, InjectFault and Reset, and the `After ...` lines show the state and the watched tag changing and then returning after the last Reset. If your methods take different arguments than the adapter sends (`LoadScenario(name)` or `LoadScenario(name, json)`, `InjectFault(device, fault)`), note the signatures from `browse`.

## Verdict (fill in and send back)

| Question | Result |
| --- | --- |
| ViewApp process name, resolution and DPI | |
| Symbol region and colours that worked (`hmimap.yaml`) | |
| Classifier that worked: average (today's driver), share, or neither | |
| `verify` score and latency min/avg/max | |
| Running, stopped and fault told apart? Fault flashes? | |
| Capture when covered / RDP minimized / screen locked | |
| UIA navigation possible (AutomationIds or Names on nav buttons)? | |
| OPC UA path (OI Gateway or platform server), port, security mode | |
| OPC UA read/write and data types OK? | |
| MXAccess read, write, alarm watch and ack OK? | |
| Historian delta/cyclic OK, time zones OK, storage delay | |
| Where alarm history lives, and the working `alarm.sql` | |
| Simulator method signatures and state values | |
