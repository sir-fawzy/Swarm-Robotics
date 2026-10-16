# SwarmSecureCoordination

A C# simulation for exploring how authentication, identity admission, replay protection, message budgets, and simple filtering affect a swarm's shared estimate when some senders are faulty or malicious.

The central question is: **How does a protected aggregation pipeline compare with an unprotected average when robots send incorrect readings or an attacker submits messages through fake identities?**

The current implementation runs locally in one process. Its “consensus” result is a centralized aggregate of sensor readings. It does not implement distributed agreement, a blockchain network, or a production security system.

## Requirements and quick start

- .NET 10 SDK, matching the project's `net10.0` target.
- Access to NuGet when restoring the test dependencies for the first time.

From the `SwarmSecureCoordination` project directory, run:

```sh
dotnet build SwarmSecureCoordination.sln
dotnet test SwarmSecureCoordination.sln
dotnet run --project SwarmSecureCoordination.csproj
```

The application prints a baseline and a secure result for each of four simulation runs. Honest readings include random noise, so results vary between executions. A missing result is displayed as `no consensus`; it is never replaced with zero.

## Project layout

```text
SwarmSecureCoordination/
├── Program.cs
├── SwarmSecureCoordination.csproj
├── SwarmSecureCoordination.sln
├── SECURITY.md
├── .gitignore
├── Models/
│   ├── Message.cs
│   └── LogEntry.cs
├── Interfaces/
│   ├── IMessageSigner.cs
│   ├── IMessageVerifier.cs
│   ├── IBudgetManager.cs
│   ├── IHashLog.cs
│   ├── IFilterModule.cs
│   └── IConsensusModule.cs
├── Security/
│   ├── IdentityManager.cs
│   ├── AuthenticatedPayload.cs
│   ├── MessageVerifier.cs
│   └── BudgetManager.cs
├── Core/
│   ├── Robot.cs
│   ├── FaultyRobot.cs
│   ├── SybilAttacker.cs
│   ├── SecureCoordinationLayer.cs
│   ├── HashLog.cs
│   ├── FilterModule.cs
│   ├── ConsensusModule.cs
│   ├── BaselineConsensus.cs
│   └── ExperimentRunner.cs
└── Tests/
    ├── SwarmSecureCoordination.Tests.csproj
    └── SecurityTests.cs
```

## How a reading moves through the project

1. Trusted startup code provisions a separate signing capability for each robot and decides which identities are admitted.
2. Each robot senses a value and creates a message containing its identity, round, reading, message ID, UTC timestamp, and observed log snapshot.
3. The robot authenticates that message using its own HMAC key.
4. The baseline path simply averages submitted values without security checks or filtering.
5. The secure path examines a bounded number of messages, validates their fields, checks admission and authentication, rejects accepted-message replays, and checks the sender's remaining budget.
6. Accepted messages enter the local hash log. Their values pass through the filter and then the averaging module.
7. The experiment runner returns the result for that batch, or `null` if no values remain.

## Entry point and project files

### `Program.cs`

This is the composition root: it creates the concrete implementations and connects them through their interfaces. It provisions honest and fake signing identities, initializes budgets, creates messages, and prints the experiment results.

Each loop iteration creates a fresh experiment and processes round 1:

| Scenario | Submitted messages | What it demonstrates |
| --- | --- | --- |
| 1 | Five honest robots around a true value of 0.7 | Baseline averaging compared with the secure pipeline's filtering. |
| 2 | Four honest robots and one admitted faulty robot reporting 1.0 | A message can be authentic yet contain dishonest sensor data. |
| 3 | Five honest robots plus five unadmitted fake identities reporting 1.0 | Fake submissions influence the baseline but are rejected by secure admission checks. |
| 4 | The same attack configuration as scenario 3, with fresh state and readings | Another execution of the protected comparison. |

Scenarios 3 and 4 currently use the same configuration; they are not different attack models. Both print baseline and secure results. The demo does not currently exercise multiple sequential rounds; the tests cover round transitions.

### `SwarmSecureCoordination.csproj`

Defines the executable project, its .NET target, implicit imports, and nullable reference checking. It excludes `Tests/**/*.cs` from application compilation so test code is built only by the test project.

### `SwarmSecureCoordination.sln`

Groups the application and test projects so one solution-level build or test command can operate on both.


### `SECURITY.md`

Documents the trust model, authenticated payload, replay lifecycle, processing limits, and security limitations in more detail.

## Models: the data passed between components

### `Models/Message.cs`

An immutable-by-initialization record representing a robot's submission.

| Field | Purpose |
| --- | --- |
| `RobotId` | Identifies the sender whose admission and authentication key must be checked. |
| `RoundId` | Associates the reading with the expected positive round number. |
| `Value` | Carries a finite reading in the inclusive range `[0, 1]`. |
| `MessageId` | Gives each message a nonempty unique identifier for replay tracking. |
| `Timestamp` | Records message creation time as UTC. |
| `Signature` | Stores the hexadecimal HMAC-SHA256 authentication tag; despite the name, this is not a public-key digital signature. |
| `PrevHash` | Records the sender's observed log snapshot, initially `GENESIS`. |

`PrevHash` is authenticated context, not a proof that the message extends the current log. Multiple robots can create messages against the same snapshot before any message is processed, so the secure layer does not compare this field with the changing log tip.

### `Models/LogEntry.cs`

Stores an accepted message's robot ID, round, and value alongside the aggregator's actual previous log hash and the new entry hash. Its `PrevHash` describes the aggregator's chain link, which may differ from the snapshot in the original message.

The stored entry is a summary: it does not retain every original authenticated field or expose a complete audit/export interface.

## Interfaces: the contracts between components

Interfaces let the coordination pipeline depend on behavior rather than a specific class. Alternative implementations can be supplied through constructor arguments.

| Interface | Defined in | Responsibility |
| --- | --- | --- |
| `IMessageSigner` | `IMessageSigner.cs` | Exposes one robot's identity and authenticates messages for that identity only. |
| `IIdentityVerifier` | `IMessageSigner.cs` | Checks admission separately from cryptographic authentication. |
| `IMessageVerifier` | `IMessageVerifier.cs` | Decides whether a message is valid for an expected round. |
| `IBudgetManager` | `IBudgetManager.cs` | Initializes a round's sender allowances and consumes an allowance for an accepted submission. |
| `IHashLog` | `IHashLog.cs` | Returns the current log tip and appends an accepted message. |
| `IFilterModule` | `IFilterModule.cs` | Transforms accepted values before aggregation. |
| `IConsensusModule` | `IConsensusModule.cs` | Computes a nullable result from the filtered values. |

`IIdentityVerifier` currently shares a source file with `IMessageSigner`; it is a separate contract.

## Security: identity, authentication, and allowances

### `Security/IdentityManager.cs`

Acts as the trusted simulation provisioning authority. `Provision(robotId, admitted)` generates a fresh random 32-byte key, retains a verification copy, and returns a signer limited to that identity. Duplicate provisioning of the same ID is rejected.

IDs must contain 1–64 ASCII letters, digits, underscores, or hyphens. Keys remain in memory, are not printed or persisted, and change when the process restarts.

**Key ownership and admission are different:** a fake identity can have a valid key while remaining unauthorized to participate. The trusted setup code controls the `admitted` flag. Robots and attackers receive signing capabilities, not the shared provisioning authority or honest robots' keys.

Verification recomputes the HMAC and compares tag bytes using a constant-time comparison. Since HMAC uses shared secrets, the trusted verifier can also forge tags; this is not a public-verification signature scheme.

### `Security/AuthenticatedPayload.cs`

Defines exactly which bytes are authenticated. It writes a fixed-order UTF-8 JSON array containing a protocol/version marker, robot ID, round, value, message ID, timestamp ticks and kind, and `PrevHash`.

JSON number formatting is independent of the machine's culture and preserves the numeric value without the previous six-decimal rounding. The tag itself is excluded from its own payload. Changing any covered field requires computing a new valid tag.

### `Security/MessageVerifier.cs`

Checks a message's structure and expected round before accepting its authentication. It rejects null messages, invalid identities, empty message IDs, non-UTC timestamps, nonfinite or out-of-range values, invalid snapshot formats, and invalid authentication tags.

The permitted timestamp window is five minutes in the past and thirty seconds in the future. A snapshot must be `GENESIS` or 64 hexadecimal characters. The sender must be admitted as well as cryptographically authenticated.

An optional `TimeProvider` allows callers to supply a clock. Replay tracking belongs to the coordination layer, not this verifier. Lower-level authentication methods assume suitable input; use this verifier for incoming messages.

### `Security/BudgetManager.cs`

Allocates a configurable number of accepted submissions per listed identity for the current round; the default is one. `Consume` returns false for missing identities, exhausted allowances, or a different round.

Rounds must increase when initialized. Reinitializing the same round is rejected to prevent refilling its allowances. Duplicate IDs in the initialization list do not create extra budget.

A budget is a participation allowance, not identity admission. Giving an unadmitted identity a budget does not make its messages valid. Budget consumption occurs after verification, so this mechanism does not bound the cost of checking invalid authentication tags.

## Core: robots and processing

### `Core/Robot.cs`

Models an honest sender with a true sensor value and its own signer. `Sense` adds random noise from approximately −0.05 to +0.05 and clamps the reading to `[0, 1]`.

`CreateMessage` calls `Sense`, generates a new message ID and UTC timestamp, records the supplied round and observed snapshot, then authenticates the completed message. The robot never receives the shared identity manager.

### `Core/FaultyRobot.cs`

Overrides sensing to return a chosen malicious value while retaining normal message creation and authentication. It demonstrates why authentication alone cannot establish that a reading is truthful.

### `Core/SybilAttacker.cs`

Creates submissions using a supplied collection of attacker-owned signing capabilities. Each fake identity reports the chosen malicious value. The attacker cannot register identities or obtain honest signing capabilities through this class.

In the demo, trusted setup provisions fake identities without admitting them. Their messages still affect the unprotected baseline, but the secure verifier rejects them.

### `Core/SecureCoordinationLayer.cs`

Coordinates verification, replay tracking, budget consumption, logging, filtering, and aggregation through interfaces.

It maintains one active round and a set of accepted `(RobotId, MessageId)` pairs. A duplicate pair is rejected within that round. Failed verification or a rejected budget does not add a pair to the replay set. Advancing to a newer round clears the active replay set and incoming counter; calls for older rounds return no result.

The default incoming limit is 1,024 examined messages per round, shared across calls on the same layer. Invalid and duplicate messages also count toward this limit. Enumeration stops at the remaining allowance. The limit can be changed through the layer constructor.

Keep the same layer alive across batches. Its processing method serializes calls internally, but callers must also coordinate round initialization and changes to shared dependencies. Each call aggregates only its newly accepted batch; it does not accumulate a round-wide estimate across calls.

### `Core/HashLog.cs`

Maintains an in-memory sequence of accepted entries. An empty log has the tip `GENESIS`. On append, it hashes the actual current tip together with the complete authenticated payload and the message's tag, then stores a summary entry.

The chain links accepted submissions in processing order. It does not independently verify messages, implement distributed agreement, persist data, or provide an audit verification API. The coordination layer is responsible for checking messages before appending them.

### `Core/FilterModule.cs`

For more than two accepted values, sorts them and removes one lowest and one highest value. For two or fewer values, returns them unchanged.

This is a simple trimmed-mean preparation step. It can reduce the influence of an extreme reading but does not identify malicious robots or guarantee robustness against multiple coordinated attackers. Honest readings are trimmed too.

### `Core/ConsensusModule.cs`

Calculates the arithmetic mean of the filtered values. Returns `null` for an empty or null list. It relies on the pipeline to supply validated finite values.

The class name uses “consensus,” but its operation is centralized averaging, not a protocol in which independent robots reach agreement.

### `Core/BaselineConsensus.cs`

Averages the supplied values without authentication, admission, budgets, replay checks, or filtering. Returns `null` for empty or null input.

This intentionally unprotected comparison shows how fake submissions can change an estimate. It should not be used as the protected ingestion path.

### `Core/ExperimentRunner.cs`

Exposes the two experiment paths: `RunBaselineRound` and `RunSecureRound`. It retains one secure coordination layer so replay and incoming-limit state survive repeated calls.

It does not provision identities or initialize round budgets. Trusted calling code must perform that setup before processing messages.

## Tests

`Tests/SwarmSecureCoordination.Tests.csproj` references the application and uses xUnit with the .NET test SDK.

`Tests/SecurityTests.cs` contains eight test methods covering:

| Test | Behavior checked |
| --- | --- |
| `ValidConcurrentSnapshotsAndReplay` | Valid messages sharing a snapshot are accepted; replays and messages from older rounds are rejected. |
| `EveryAuthenticatedFieldRejectsTampering` | Changes to covered fields or the tag are rejected; a forged copy cannot consume the valid original's replay entry. |
| `MalformedAndNonFinite` | Malformed fields, NaN, infinities, invalid times, null messages, and empty input are handled. |
| `NoPrematureReplayConsumptionOrBudgetReset` | A budget rejection does not mark a message as accepted, and the same round's budget cannot be refilled. |
| `FloodIsBoundedAcrossCalls` | Even an endless incoming sequence is enumerated only up to the round limit, which persists across calls. |
| `KeyPossessionDoesNotGrantAdmission` | A correctly authenticated fake identity is rejected even with a budget; its signer cannot impersonate an honest identity. |
| `CultureIndependentAndRandomKeys` | Verification survives a culture change, and independent provisioning produces different authentication tags for the same payload. |
| `BudgetsApplyOnlyAfterVerification` | Invalid tags do not consume a robot's allowance; accepted submissions do. |

These are regression checks for the implemented behavior, not a proof of security or exhaustive validation.

## Extending the experiments

- Change robot counts, true values, malicious values, or fake identity counts in `Program.cs`.
- Supply alternative filter or aggregation implementations through `IFilterModule` and `IConsensusModule`.
- Adjust per-identity allowances through the `BudgetManager` constructor.
- To study multiple rounds, reuse the same runner and dependencies, initialize each strictly increasing round's budget, create messages for that round, and process them before advancing again.
- To study weak admission, deliberately admit attacker-controlled identities in a clearly labeled experiment. Valid keys and per-identity budgets alone cannot prevent that form of Sybil attack.

The console demo is an illustration, not a statistical evaluation. Repeated trials, reproducible randomness, explicit attack assumptions, and metrics would be needed for research comparisons.

## Boundaries and limitations

- All coordination, keys, replay state, budgets, and logs reside in one process. Restarting loses that state.
- The trusted authority decides admission; the project does not establish that an identity corresponds to one physical robot.
- In-process signer separation does not protect against arbitrary code execution or reflection in the same process.
- Incoming limits bound examined items, not network bytes, upstream allocations, blocking enumerators, or log growth. An attacker can exhaust the limit before honest messages arrive.
- The local hash chain has no external anchor and cannot prevent a privileged operator from rewriting history.
- Timestamp checks depend on an appropriate clock. There is no durable replay store or restart recovery protocol.
- There is no robot transport, deployment system, persistent key store, leader election, replicated ledger, or distributed consensus implementation.

See [SECURITY.md](SECURITY.md) for the detailed security model.
