# SwarmSecureCoordination

A C# simulation for studying swarm agreement under faulty sensor readings, replay, tampering, fake identities, conflicting proposals, and communication failures.

The default mode models **independent robot nodes communicating over an all-to-all network**. Each node checks messages, keeps its own state, exchanges votes, calculates an estimate, and decides locally. The earlier centralized model remains available as a comparison.

This is a single-process distributed-protocol simulation, not deployment software for physical robots or a production implementation of Byzantine fault tolerance.

## Requirements and quick start

Use the .NET 10 SDK. The first test dependency restore requires access to NuGet.

Run these commands from the project directory:

```sh
dotnet build SwarmSecureCoordination.sln
dotnet test SwarmSecureCoordination.sln
dotnet run --project SwarmSecureCoordination.csproj
```

The default runs distributed experiments. You can select either mode explicitly:

```sh
dotnet run --project SwarmSecureCoordination.csproj -- --distributed
dotnet run --project SwarmSecureCoordination.csproj -- --centralized
```

## Distributed agreement model

The implementation uses a **single-view, quorum-based proposal/prepare/commit protocol**, informed by [Castro and Liskov's PBFT work](https://www.usenix.org/conference/osdi-99/presentation/practical-byzantine-fault-tolerance). It is not full PBFT: it has no view-change protocol, checkpoint transfer, recovery, or general replicated state machine.

The roster is fixed and trusted for a session. For `n` admitted robots and a configured bound of `f` Byzantine robots, setup requires `n >= 3f + 1`; a quorum contains `n - f` distinct admitted identities. The demonstration uses four admitted robots, a fault bound of one, and quorums of three.

### How robots decide

1. Trusted setup gives every robot its own ECDSA P-256 private signing capability and a copy of the roster containing public keys only. A fresh session ID and the roster fingerprint bind all messages to that experiment.
2. Each robot broadcasts one signed sensor reading for the round.
3. After the reading-collection window, the designated proposer suggests a sorted vector of at least `n - f` distinct, independently signed readings. The proposer rotates by round number.
4. Each recipient validates the proposer, the entire vector, and every reading signature. It locks one valid proposal for that round and broadcasts a signed **prepare** vote for its digest.
5. A node that gathers `n - f` matching prepare votes broadcasts a signed **commit** vote.
6. A node decides only after it has its own matching prepare quorum and `n - f` matching commit votes. It calculates the trimmed mean itself and records the local result and certificate.

The proposer cannot establish a decision alone. The network scheduler never selects a proposal, calculates a result, or fabricates votes.

Two quorums of size `n - f` intersect in at least `n - 2f > f` members. Under the stated fault bound, that intersection includes an honest robot, which never prepares two proposals for the same session and round. This is the basis for rejecting conflicting decisions in this limited protocol. Tests exercise the implementation; they do not constitute a formal proof.

### Agreement, participation, and accuracy

- **Agreement safety:** honest nodes that decide should not decide different proposal digests for the same session, roster, and round.
- **Completion:** enough messages and votes must arrive before the simulation closes the round. An honest proposer and timely delivery are needed for normal progress; a faulty or unavailable proposer can stall the round.
- **Accuracy:** honest nodes may agree on a bad physical estimate. A signature authenticates who supplied a reading, not whether the sensor value is true.

A proposal may omit up to `f` members' readings. Membership validation does not force the proposer to include every honest reading. The fixed estimation policy sorts the accepted vector, removes one minimum and one maximum when there are more than two readings, and averages the rest. This policy is shared by the protocol version and is not a general Byzantine estimation guarantee.

`no decision` means that a node did not collect the required evidence. It is not represented as zero. A result is not labeled successful merely because no conflicting decisions were observed: zero nodes deciding is reported separately from all nodes deciding.

### Distributed source files

| File under `Distributed/` | Purpose |
| --- | --- |
| `ProtocolModels.cs` | Defines signed readings, proposal and vote messages, immutable local decision records, and the `INodeSigner`, `IPublicKeyVerifier`, and `IRobotNetwork` contracts. |
| `ProtocolCodec.cs` | Provides versioned deterministic authentication payloads, proposal digests, strict bounded JSON decoding, and packet serialization. |
| `NodeIdentity.cs` | Creates an independent random P-256 key per identity. Signing refuses another sender ID. Public-key verifiers import only the roster's public keys. |
| `SwarmMembership.cs` | Validates the trusted roster, key uniqueness, fault bound, and quorum size; computes a fingerprint; provides the rotating proposer. The simulation supports 1–32 members, subject to the fault bound. |
| `AgreementNode.cs` | Implements one robot's reading reception, proposal validation, replay checks, per-kind allowances, vote collection, local decision, and local log. |
| `CommitCertificate.cs` | Independently checks a local decision's signed reading vector, digest, distinct commit quorum, and default estimate using public keys. It does not establish physical sensor truth or verify the local log chain. |
| `SimulatedNetwork.cs` | Schedules separate deliveries to each registered endpoint. Supports dropped, delayed, duplicated, and reordered packets, queue limits, and delivery metrics. |
| `DistributedExperiments.cs` | Constructs experiment scenarios, advances simulated time, injects attacker behavior using attacker-owned credentials, and prints each node's result. |

### Message authentication and state

All distributed messages bind their kind, session ID, roster fingerprint, round, sender, message ID, UTC timestamp, proposal digest, and any embedded readings. Every reading separately authenticates its context and exact numeric value. Authentication excludes only the envelope's own signature.

Distributed signatures use ECDSA P-256 with SHA-256 and fixed-width signatures. Centralized HMAC code remains in its separate comparison path. Distributing verification keys therefore gives peers no ability to sign on behalf of other robots.

Each node rejects unadmitted senders, malformed data, nonfinite/out-of-range readings, wrong contexts, invalid signatures, and timestamps more than five minutes old or thirty seconds in the future. Replay IDs are scoped by sender and message kind. Per round, each node accepts at most one reading and one prepare/commit vote per sender and locks at most one proposal. Invalid authentication or proposal contents cannot consume those accepted-message allowances.

Votes may arrive before the corresponding proposal; a node stores at most one vote per sender and kind and counts only those matching its validated proposal. A commit quorum by itself cannot make a node decide without its own prepare quorum.

`BeginRound` requires a strictly newer round and a closed previous round. `CloseRound` is the experiment deadline. The same node instances retain their local decision logs across rounds; per-round replay and vote state resets on advancement. Delayed messages from an older round cannot be counted in a newer one.

Each local log entry contains the decided vector, commit certificate, estimate, and a hash linking the previous local decision to the current proposal digest. A node that missed a decision may have a different local history. There is no automatic state synchronization, durable ledger, or globally agreed log-tip requirement.

### Distributed experiments

| Scenario | Expected behavior under the configured schedule |
| --- | --- |
| Honest swarm | All four nodes decide the same vector and estimate. |
| Faulty sensor | An admitted sender reports 1.0; peers agree, while trimming reduces that particular outlier's influence. |
| Sybil and replay | An outsider's signed submissions are rejected; repeated proposals and votes do not create extra voting power. |
| Silent peer | Three reachable honest nodes can still decide without the fourth peer. |
| Partition | A 2–2 split cannot form a quorum; nodes report no decision. |
| Equivocating proposer | A faulty proposer sends different vectors to different peers; in this schedule the honest votes split and no honest peer decides. |
| Flood | Invalid traffic exhausts deliberately reduced incoming allowances; nodes can lose availability and report no decision. |

Output includes per-node decisions, rejected messages, limit drops, how many honest nodes decided, whether decided digests conflict, delivery counts, and estimation error from the experiment's 0.7 reference. The harness knows which peers it modeled as faulty for reporting; that knowledge is not available to the protocol.
 
### Distributed tests

`Tests/DistributedAgreementTests.cs` covers independent decisions and public certificates; false readings; silent peers; unavailable proposers; partitions; delays, reordering, and duplication; insufficient distinct votes; forgery; unauthorized Sybils; envelope and reading tampering; malformed/nonfinite inputs; invalid proposal handling; duplicate-reading identities; equivocation with conflicting votes; missing prepare quorums; round/session replay; flooding; transport limits; membership validation; culture independence; seven-node operation with two silent peers; and several adversarial delivery/lock assignments.

The original eight centralized security regression tests remain in `Tests/SecurityTests.cs`.

### Resource limits and failure assumptions

- The in-memory scheduler has a default pending-queue capacity of 8,192 packets, with at most 65,536 bytes per packet. Delivery is shuffled using a seeded scheduler. Delay and duplicate counts are bounded by the transport API.
- Each node examines at most 4,096 incoming items per round by default, including its own locally emitted messages. A fully populated proposal can require up to `n + 1` signature checks. Allowances for authenticated senders apply after verification; they cannot eliminate invalid-signature cost.
- A flood may exhaust queue or incoming limits and prevent honest progress. The protocol does not claim fairness or DoS-proof availability. Ingested packets and verification work are bounded, but local logs can grow across rounds.
- Timing is controlled by logical network ticks and explicit experiment deadlines. Timestamp freshness uses each node's supplied `TimeProvider`, defaulting to wall-clock UTC. This is not a calibrated radio, mobility, or clock-drift model.
- Nodes are separate state machines in a single process, driven sequentially by the scheduler. There is no real network transport, OS-process isolation, or physical robot integration.
- Admission is trusted and static. Public-key possession does not prove one physical robot per identity. Admitting many attacker-controlled identities can violate the configured Byzantine fault bound.
- There is no within-round leader replacement. Moving to a new round starts a distinct sensor decision; it does not retry or finalize the previous round's value.

See [SECURITY.md](SECURITY.md) for the trust model and [DISTRIBUTED_DESIGN.md](DISTRIBUTED_DESIGN.md) for protocol details and next steps.

---

The remaining sections describe the preserved centralized comparison and its components.

## Centralized comparison: project layout

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

## Centralized comparison: entry point and project files

### `Program.cs` — `--centralized` mode

The centralized branch is the composition root: it creates the concrete implementations and connects them through their interfaces. It provisions honest and fake signing identities, initializes budgets, creates messages, and prints the experiment results.

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

## Centralized regression tests

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

## Centralized model boundaries and limitations

- All coordination, keys, replay state, budgets, and logs reside in one process. Restarting loses that state.
- The trusted authority decides admission; the project does not establish that an identity corresponds to one physical robot.
- In-process signer separation does not protect against arbitrary code execution or reflection in the same process.
- Incoming limits bound examined items, not network bytes, upstream allocations, blocking enumerators, or log growth. An attacker can exhaust the limit before honest messages arrive.
- The local hash chain has no external anchor and cannot prevent a privileged operator from rewriting history.
- Timestamp checks depend on an appropriate clock. There is no durable replay store or restart recovery protocol.
- There is no robot transport, deployment system, persistent key store, leader election, replicated ledger, or distributed consensus implementation.

See [SECURITY.md](SECURITY.md) for the detailed security model.
