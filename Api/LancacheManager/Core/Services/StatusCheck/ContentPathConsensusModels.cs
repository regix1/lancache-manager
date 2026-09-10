namespace LancacheManager.Core.Services.StatusCheck;

internal sealed record ProtocolConsensus(string Status, string? Reason, int ConsensusEdges);
