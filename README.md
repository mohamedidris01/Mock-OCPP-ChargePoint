# Mock OCPP Charge Point (C#)

A **mock OCPP 1.6J charge point** — JSON-RPC over a WebSocket carrying the
`ocpp1.6` subprotocol. It connects to a real CSMS (central system), runs the
boot handshake, and then behaves like a physical charger you can drive from the
terminal: plug a cable, tap a tag, draw power, raise a fault, suspend the EV.

It is a C# port of the ideas in the C++ `ocpp-charge-point` simulator, with
every OCPP 1.6 message type wired up (all six profiles) and richer terminal
controls: multiple charge points, multiple connectors, per-connector power,
`SuspendedEV` / `SuspendedEVSE`, auto-cycling sessions, and time compression.

```
ws://csms.example.com/ocpp/<chargePointId>
Sec-WebSocket-Protocol: ocpp1.6
```

## Build & run

```powershell
dotnet build
dotnet run --project Mock-OCPP-ChargePoint -- --url ws://127.0.0.1:9000/ocpp --id CP0001
```

The built executable is `mock-ocpp-cp.exe`.

### Quick start against the bundled Python mock CSMS

The sibling C++ project ships a stdlib-only CSMS that is handy for offline
testing:

```powershell
# terminal 1
python "..\CPP Projects\ocpp-charge-point\sim\tools\mock_csms.py" --port 8887

# terminal 2
dotnet run --project Mock-OCPP-ChargePoint -- --url ws://127.0.0.1:8887/ocpp --id CP0001
```

> On Windows, TCP ports below ~9200 can be reserved by the OS. 8887 works.

## Terminal options

| Option | Meaning |
|---|---|
| `--url, -u <ws://host:port/path>` | CSMS base URL; the charge point id is appended |
| `--id <id>` | exact charge point id (single unit) |
| `--id-prefix <prefix>` | id prefix for a fleet (`CP` → `CP0001`, `CP0002`, …) |
| `--count, -n <n>` | number of charge points to run in this process |
| `--connectors, -c <n>` | connectors per charge point |
| `--power, -p <watts>` | power the EVSE offers per connector (default 7400) |
| `--vendor` / `--model` / `--firmware` | BootNotification identity fields |
| `--heartbeat <s>` | initial `HeartbeatInterval` |
| `--meter-interval <s>` | `MeterValueSampleInterval` |
| `--user <u> --pass <p>` | HTTP Basic auth for the WebSocket upgrade |
| `--tag <idTag>` | idTag presented by auto sessions |
| `--auto` | auto-cycle sessions: plug → tag → charge → stop → unplug, forever |
| `--suspended-ev` | auto mode, but every session parks in `SuspendedEV` for a while |
| `--passive` | connect, boot and heartbeat only — let the CSMS drive |
| `--speed <factor>` | compress simulated time (`10` = 10× meter speed) |
| `--wire` | print raw JSON frames |
| `--help, -h` | usage |

Examples:

```powershell
# one charge point, two connectors, 22 kW, interactive prompt
mock-ocpp-cp -u ws://host:9000/ocpp --id CP0001 -c 2 -p 22000

# a fleet of 20, auto-cycling, 20× time
mock-ocpp-cp -u ws://host:9000/ocpp -n 20 --auto --speed 20

# five chargers that always end up SuspendedEV mid-session
mock-ocpp-cp -u ws://host:9000/ocpp -n 5 --suspended-ev
```

## Interactive prompt

With a single unit the prompt opens automatically. In fleet/auto mode it is
still there — `cp <n>` selects the charge point commands act on, `connector <n>`
the connector.

```
list                        every charge point and its connectors
cp <n|id>                   select the active charge point
connector <n>               select the active connector
status                      link / boot / connector / transaction state
plug | unplug [conn]        cable in / out
tag <idTag> [conn]          present an RFID tag (same tag again = stop)
remotestop [conn]           stop the active transaction
fault <code> [conn]         raise a fault (ground overcurrent overvoltage
                            undervoltage temperature lock comms reader meter
                            switch internal other)
clear [conn]                clear the fault
power <watts> [conn]        power the EVSE offers
suspendev [conn]            → SuspendedEV   (car stops drawing)
suspendevse [conn]          → SuspendedEVSE (station withholds power)
resume [conn]               back to Charging
avail <op|inop> [conn]      ChangeAvailability, locally
boot | heartbeat | meter    send that message now
dt <vendorId> [msgId] [data]  send a DataTransfer
get [filter] | set <k> <v>  configuration keys
speed <factor>              compress simulated time
reconnect                   drop and re-open the WebSocket
help | quit
```

## OCPP 1.6 coverage

`SupportedFeatureProfiles` advertises
`Core,FirmwareManagement,LocalAuthListManagement,Reservation,SmartCharging,RemoteTrigger`.

**CP → CS (sent):** BootNotification · Heartbeat · Authorize · StartTransaction ·
StopTransaction (with `transactionData`) · StatusNotification · MeterValues
(Energy, Power, Current, Voltage, SoC) · DataTransfer · DiagnosticsStatusNotification ·
FirmwareStatusNotification

**CS → CP (answered):**

| Profile | Operations |
|---|---|
| Core | `GetConfiguration` `ChangeConfiguration` `ChangeAvailability` `ClearCache` `DataTransfer` `RemoteStartTransaction` `RemoteStopTransaction` `Reset` `UnlockConnector` |
| RemoteTrigger | `TriggerMessage` (Boot/Heartbeat/StatusNotification/MeterValues/Diagnostics/Firmware) |
| LocalAuthListManagement | `GetLocalListVersion` `SendLocalList` (full + differential) |
| Reservation | `ReserveNow` `CancelReservation` |
| SmartCharging | `SetChargingProfile` `ClearChargingProfile` `GetCompositeSchedule` — an installed profile actually caps the connector's offered power |
| FirmwareManagement | `GetDiagnostics` `UpdateFirmware` — **stubbed**: they acknowledge and emit the matching status notifications, but nothing is downloaded or flashed |

Anything outside that set answers `NotImplemented`, which is the spec-correct
response and what compliance suites check for.

## Behaviour worth knowing

- **The CSMS owns the clock.** `BootNotification.conf` / `Heartbeat.conf`
  `currentTime` is recorded as an offset and applied to every outgoing
  timestamp; the OS clock is never touched.
- **One CALL outstanding per direction.** All outbound calls funnel through a
  single-in-flight queue with a 30 s timeout; `StartTransaction` /
  `StopTransaction` are marked transactional and retried until delivered.
- **The meter integrates energy** at the offered power while the contactor is
  closed and the EV is drawing. SoC climbs as energy flows; near 100 % the draw
  tapers and the connector settles into `SuspendedEV` — the real end-of-charge
  shape.
- **`ChangeAvailability` during a transaction** answers `Scheduled` and applies
  when the session ends.
- **Auto-reconnect** with a 5 s backoff; `Reset` drops the socket and lets it
  come back.

## Layout

```
Mock-OCPP-ChargePoint/
├── Program.cs                    arg parsing, fleet wiring, prompt vs headless
├── Cli/
│   ├── CliOptions.cs             every --flag
│   └── Repl.cs                   the interactive prompt
├── Protocol/
│   ├── OcppEnums.cs              wire enums + [WireName] spellings
│   ├── RpcFrame.cs               CALL / CALLRESULT / CALLERROR framing
│   ├── CallResult.cs             what a CS→CP handler returns
│   └── OcppConnection.cs         resilient WebSocket + call queue + dispatch
├── Model/
│   ├── ChargePoint.cs            state machine (partial: .Outbound .Session .Handlers)
│   ├── Connector.cs  Transaction.cs  MeterSim.cs
│   ├── OcppConfiguration.cs      the standard 1.6 keys
│   ├── OcppClock.cs              CSMS clock offset + ISO 8601
│   └── FeatureStores.cs          local auth list, charging profiles
└── Sim/
    └── ChargePointNode.cs        one unit: transport + tick loop + auto driver
```
