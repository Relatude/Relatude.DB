using Relatude.DB.IO;

namespace Relatude.DB.NodeServer;

public sealed record IOProviderCounters(Guid Id, string Name, IOTypes Type, IOCounters Counters);

public sealed record IOActivityReport(IOCounters Total, IOProviderCounters[] Providers);
