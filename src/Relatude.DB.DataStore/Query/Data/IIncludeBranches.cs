using Relatude.DB.Datamodels;

namespace Relatude.DB.Query.Data; 
public interface IIncludeBranches {
    void EnsureRetrivalOfRelationNodesDataBeforeExitingReadLock(Metrics metrics);
    void IncludeBranch(IncludeBranch relationPropertyIdBranch);
}
