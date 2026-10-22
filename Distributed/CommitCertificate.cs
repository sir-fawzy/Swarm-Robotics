namespace SwarmSecureCoordination.Distributed;

public static class CommitCertificate
{
    // Verify the evidence, not the physical truth of the proposed readings. Historical certificates
    // are not rejected merely because their timestamps have aged since the decision.
    public static bool Verify(LocalDecision decision, SwarmMembership membership, Guid session)
    {
        if (session == Guid.Empty || decision.Round < 1 || !ProtocolCodec.IsDigest(decision.Digest) ||
            decision.CommitCertificate.Count < membership.Quorum || decision.CommitCertificate.Count > membership.Ids.Count ||
            decision.Readings.Count < membership.Quorum || decision.Readings.Count > membership.Ids.Count) return false;
        var ids = decision.Readings.Select(r => r.Sender).ToArray();
        if (!ids.SequenceEqual(ids.Order(StringComparer.Ordinal)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) return false;
        using var verifier = new PublicKeyVerifier(membership);
        foreach (var reading in decision.Readings)
            if (!membership.Contains(reading.Sender) || reading.Session != session || reading.Membership != membership.Fingerprint ||
                reading.Round != decision.Round || reading.Id == Guid.Empty || reading.Timestamp.Kind != DateTimeKind.Utc ||
                !double.IsFinite(reading.Value) || reading.Value < 0 || reading.Value > 1 || !verifier.Verify(reading)) return false;
        if (ProtocolCodec.Digest(decision.Readings) != decision.Digest) return false;
        var voters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var vote in decision.CommitCertificate)
            if (vote.Kind != AgreementKind.Commit || !membership.Contains(vote.Sender) || !voters.Add(vote.Sender) ||
                vote.Session != session || vote.Round != decision.Round || vote.Membership != membership.Fingerprint ||
                vote.Digest != decision.Digest || vote.Id == Guid.Empty || vote.Timestamp.Kind != DateTimeKind.Utc ||
                vote.Reading is not null || vote.Readings is not { Length: 0 } || !verifier.Verify(vote)) return false;
        // The default estimation policy is part of this protocol version.
        var values = decision.Readings.Select(r => r.Value).Order().ToList();
        if (values.Count > 2) values = values.Skip(1).Take(values.Count - 2).ToList();
        return double.IsFinite(decision.Estimate) && decision.Estimate == values.Average();
    }
}
