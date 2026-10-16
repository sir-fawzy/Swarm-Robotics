using SwarmSecureCoordination.Interfaces;
using SwarmSecureCoordination.Models;
namespace SwarmSecureCoordination.Core;
public sealed class ExperimentRunner
{
    private readonly SecureCoordinationLayer _layer;
    public ExperimentRunner(IMessageVerifier verifier, IBudgetManager budget, IHashLog log, IFilterModule filter, IConsensusModule consensus)
    { _layer = new(verifier, budget, log, filter, consensus); }
    public double? RunSecureRound(List<Message> messages, int roundId) => _layer.Process(messages, roundId);
    public double? RunBaselineRound(List<Message> messages) => new BaselineConsensus().Compute(messages.Select(m => m.Value).ToList());
}
