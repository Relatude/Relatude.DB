using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Indexes;
public class JobResult(int count, int total, string description) {
    public int Dequeued => count;
    public int TotalInQueue => total;
    public string Description => description;
}
