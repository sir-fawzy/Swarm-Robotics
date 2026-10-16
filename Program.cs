using SwarmSecureCoordination.Core;
using SwarmSecureCoordination.Security;
for (int scenario = 1; scenario <= 4; scenario++)
{
    var identities = new IdentityManager();
    var robots = Enumerable.Range(1, 5).Select(i => {
        var signer = identities.Provision($"R{i}", admitted: true);
        return scenario == 2 && i == 5 ? (Robot)new FaultyRobot(signer, 0.7, 1) : new Robot(signer, 0.7);
    }).ToList();
    var attacker = new SybilAttacker(Enumerable.Range(1, 5)
        .Select(i => identities.Provision($"S{i}", admitted: false)).ToArray(), 1);
    var budget = new BudgetManager();
    budget.InitializeRound(1, robots.Select(r => r.RobotId).ToList());
    var log = new HashLog();
    var runner = new ExperimentRunner(new MessageVerifier(identities), budget, log, new FilterModule(), new ConsensusModule());
    var messages = robots.Select(r => r.CreateMessage(1, log.GetLastHash())).ToList();
    if (scenario >= 3) messages.AddRange(attacker.CreateSybilMessages(1, log.GetLastHash()));
    Console.WriteLine($"Scenario {scenario}: baseline={Format(runner.RunBaselineRound(messages))}, secure={Format(runner.RunSecureRound(messages, 1))}");
}
static string Format(double? value) => value?.ToString("F4") ?? "no consensus";
