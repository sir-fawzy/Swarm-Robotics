# Security model and limitations

## Distributed mode (default)

The default application now runs a single-process simulation of independent all-to-all robot nodes. It uses a limited single-view proposal/prepare/commit agreement protocol. This is experimental, not production security or full PBFT. See [DISTRIBUTED_DESIGN.md](DISTRIBUTED_DESIGN.md) for transitions, the quorum safety argument, and omitted features.

Trusted admission supplies a fixed common roster of P-256 public keys. Nodes receive only their own private signing capability and independent public-only verifiers. A valid key does not grant membership. The session ID, roster fingerprint (including fault bound), round, message kind, IDs, UTC timestamp, digest, and embedded signed readings are authenticated using deterministic versioned payloads. ECDSA P-256/SHA-256 signatures replace shared-secret verification for peer-to-peer messages. Keys are random, memory-only, and never logged or persisted by the application.

Setup requires `n >= 3f + 1`, with `n - f` distinct prepare votes and `n - f` distinct commit votes. Honest nodes lock one proposal per session/round and commit only after a prepare quorum. At most `f` admitted Byzantine peers and secure signatures are assumptions of the nonconflicting-decision argument. There is no within-round view change, so a faulty proposer can prevent completion. Partitions can leave some nodes undecided. Agreement does not prove sensor accuracy, and admission does not prove physical robot uniqueness.

Each node validates fields and signatures before consuming accepted-message replay state or per-kind sender allowances. All packets, including invalid ones, count toward its incoming-work limit. Default limits are 4,096 examined packets per node/round, 65,536 bytes per packet, and 8,192 pending transport packets. Proposal verification can require up to `n + 1` signature checks. Floods can exhaust limits and deny service; no fairness guarantee is claimed. Logs can grow over multiple rounds.

There is no durable replay state, key management service, node recovery, state transfer, or real robot transport. Fresh sessions prevent reusing old-session packets in the demo; recovering a previous session after a crash is not supported. Local logs/certificates are inspectable evidence, not a replicated blockchain. All nodes run sequentially in one process, which is not isolation against arbitrary code execution.

## Centralized comparison (`--centralized`)

This is a centralized batch aggregator for swarm experiments, not distributed consensus, a blockchain protocol, or production security.

Trusted startup code provisions a fresh random 256-bit HMAC-SHA256 key for each identity. Keys remain in process memory, are never printed or persisted, and change on restart. Each robot gets only its own signing capability. The attacker receives only its own fake identity capabilities. The trusted authority retains verification keys; HMAC verifiers can forge messages, so this is not a public digital signature system. In-process capability separation is an architectural boundary, not isolation from arbitrary code execution or reflection.

Provisioning a key and admitting an identity are distinct operations. Only the trusted composition root controls admission. The secure simulation provisions fake identities without admitting them, then rejects their correctly authenticated messages. If an operator admits many attacker-controlled identities, per-identity budgets do not prevent a Sybil attack. Admission requires an external trust policy; this implementation does not discover physical robot uniqueness.

The versioned authenticated payload is a fixed-order UTF-8 JSON array. It covers identity, round, exact round-trip numeric value, message ID, timestamp ticks and kind, and PrevHash. Signature is excluded from its own payload. UTC timestamps allow a five-minute age and thirty-second future skew. Checks reject malformed fields, nonfinite values, and invalid authentication tags; tag comparison uses constant-time byte comparison.

Message PrevHash means the sender's observed snapshot of the aggregator log. It is authenticated context, not a freshness or continuity proof. Concurrent messages may share a snapshot. It is never compared with a mutable log tip. The aggregator independently chains accepted entries using its own actual previous tip and the complete authenticated payload and signature. The local hash log has no external anchor and is neither durable nor protected from a privileged operator rewriting it.

The coordination layer persists across ExperimentRunner calls. It retains accepted (identity, message ID) pairs for the active round only, records them after verification and budget acceptance, and rejects older rounds after advancing. Budget initialization requires monotonically increasing rounds and cannot refill the same round. Callers must serialize trusted round initialization with processing and keep one long-lived layer per experiment. Each Process result summarizes only the accepted messages in that batch; no accepted values yields null, displayed as 'no consensus', never zero. Replay state and budgets are not durable across restarts.

At most 1024 incoming items are enumerated per round by default, across all calls; tests use a smaller limit. This bounds verification work and replay memory per round, but not upstream allocation, payload parsing, time spent in a blocking enumerator, or the lifetime size of the log. An attacker can exhaust the cap and deny honest submissions. Transport byte limits, bounded queues, timeouts and fair scheduling remain future work. Per-robot budget control applies only AFTER verification and cannot bound cryptographic verification cost. Filter trimming offers no general Byzantine robustness guarantee.

Run `dotnet build SwarmSecureCoordination.sln` and `dotnet test SwarmSecureCoordination.sln` from this project's directory. The Tests directory contains adversarial regression checks. Runtime key material and build outputs must not be committed. The ignore file is defense in depth, not a guarantee against deliberate secret publication.
