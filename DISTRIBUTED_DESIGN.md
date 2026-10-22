# Distributed agreement design

## Scope

This implementation replaces a single decision maker in the default demonstration with independent per-robot agreement state. It retains the earlier centralized aggregator behind `--centralized` for comparison and possible later removal.

The protocol is a limited, single-view, leader-based, signed proposal/prepare/commit experiment. It borrows quorum structure from [Practical Byzantine Fault Tolerance, Castro and Liskov, 1999](https://www.usenix.org/conference/osdi-99/presentation/practical-byzantine-fault-tolerance); it does not claim to implement full PBFT.

## Trusted setup and threat model

Trusted setup establishes a common session ID, protocol version, public-key roster, fault bound `f`, and starting round. The roster has `n >= 3f + 1` admitted members, each with a distinct P-256 key. Each node imports its own public-only verifier. No honest node has another node's signing capability. The experiment harness can script attacks with Byzantine credentials; those credentials never get handed to honest peers.

A Byzantine participant may lie about its sensor reading, omit messages, sign conflicting proposals/votes, and send different messages to different recipients. An outsider may generate arbitrary keys and messages but is not admitted by doing so. The scheduler can delay, reorder, duplicate, or drop deliveries. Signatures and SHA-256 are assumed secure; in-process arbitrary code execution, compromised trusted setup, and more than `f` admitted Byzantine participants are outside the stated safety assumptions.

## State at each node

Each node owns:

- A private signing capability and an independent public-key verifier.
- A fixed copy of the membership configuration and session ID.
- The current round, an open/closed status, and an incoming-work counter.
- One accepted reading per admitted origin.
- One locked proposal, containing a sorted vector with at least `n - f` signed readings.
- At most one accepted prepare and commit vote per admitted origin.
- Replay identities scoped to `(sender, message kind, message ID)`.
- A local decision, if earned, and an in-memory history of its own decisions.

No shared object stores or assigns the agreed estimate. The transport has no access to node private keys.

## Transitions

1. `BeginRound(r, value)` requires `r` greater than the previous round and the previous round closed. It clears transient state and emits one authenticated reading. Nonfinite and out-of-range local input is rejected.
2. The network delivers readings. A receiver validates the whole message and the embedded reading before recording either replay or sender allowance state.
3. At the collection deadline, the rotating proposer may propose if it has at least `n - f` distinct valid readings. Other peers do nothing at this step.
4. A receiver validates the full signed proposal and vector, then locks its first valid proposal and emits exactly one prepare vote for that digest. A later competing proposal cannot change that lock.
5. At `n - f` distinct matching prepares, a node emits one commit vote.
6. At `n - f` distinct matching commits, provided the node itself has the matching prepare quorum, it computes the fixed trimmed mean, records a certificate, and appends its local log entry.
7. `CloseRound()` stops reception for that round. No evidence means no decision. Starting another round uses a new round number and proposer, never resets the lock for the old round.

Prepare/commit packets arriving before a proposal can be retained within per-sender limits, but cannot create a decision without a validated proposal and local prepare quorum. A Byzantine sender whose conflicting vote arrives first may lose its usefulness for an honest proposal; enough honest participants still suffice in the healthy schedule.

## Safety argument and limits

With quorum `q = n - f`, any two prepare quorums share at least `2q - n = n - 2f > f` members. Honest members prepare at most one digest for a session/roster/round. Two conflicting prepare quorums would therefore require an honest member to sign both, contrary to node behavior. Any quorum of commits includes honest committers, which only commit after a prepare quorum. This supports nonconflicting honest decisions under the configured fault bound.

This argument depends on the exact no-unlock behavior implemented here. Adding retries, view changes, dynamic membership, or restart recovery requires a new safety analysis, not merely clearing state or choosing another proposer. Regression tests are evidence about code paths, not a machine-checked proof.

The protocol chooses a signed reading vector, not the physical truth. A proposer may omit up to `f` readings. Trimming one minimum and maximum is illustrative and does not guarantee a correct estimate for arbitrary Byzantine sensor values.

## Liveness and timing

The default schedule collects readings for two ticks, triggers the proposer, then allows twelve further ticks. Healthy one-tick all-to-all communication and an honest proposer yield decisions in these experiments. Longer delays can be tested by changing the delivery rule and extending the deadline.

There is no automatic leader change or retransmission protocol. Faulty proposers, insufficient communication, exhausted rate limits, and partitions can leave some or all honest nodes undecided. A node may decide while another has not received enough evidence; this is reported separately from conflicting decisions. Delayed old messages are rejected after round advancement.

Logical time is scheduler-controlled. Timestamp validity separately uses UTC with a five-minute age and thirty-second future tolerance. More realistic timing would require per-node simulated clocks, link models, retransmission, and independently scheduled deadlines.

## Authentication and canonical encoding

The reading domain is `swarm-reading/v1`; the envelope domain is `swarm-agreement/v1`; proposal digests use `swarm-proposal/v1`. Fixed-order UTF-8 JSON arrays authenticate context, type, exact numeric value, IDs, timestamp ticks/kind, digests and embedded signatures. Envelope signatures exclude their own signature only. A roster fingerprint includes sorted IDs, public keys, the fault bound, and a version marker. Any change to roster or fault policy changes that fingerprint.

The default estimation policy is fixed for this protocol version. It uses the existing filter and averaging interfaces internally; peers cannot silently select different policies through their constructors. A future configurable policy must be explicitly bound to the session/configuration and validated by every peer.

## Local logs and certificates

A local log links each node's previous local hash with the current proposal digest. The digest binds the signed vector; a decision retains the complete vector and a distinct quorum of commit votes. `CommitCertificate.Verify` checks signatures, context, distinct voters, the proposal digest, and the default estimate using public keys. Historical certificates do not expire simply because their creation timestamps have aged.

These logs are not a replicated blockchain or durable state machine. Nodes can miss rounds and have different histories. There is no state transfer, audit-log verification API, durable anti-replay storage, or protection from a privileged process rewriting memory. The log hash itself does not commit to the selected certificate or claim to prove the wall-clock time of a decision.

## Next steps

Potential later stages are a reviewed view-change/recovery protocol; dynamic topology and realistic wireless loss/mobility; independent simulated clocks; fairness and admission/rate-limit experiments; persistent keys and crash recovery; reproducible statistical trials; and eventual process/robot transport integration. Removing the centralized comparison is a separate, optional cleanup.
