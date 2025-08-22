using WAF.Datamodels;

namespace WAF.Query.Data; 
public interface IIncludeBranches {
    void EnsureRetrivalOfRelationNodesDataBeforeExitingReadLock(Metrics metrics);
    void IncludeBranch(IncludeBranch relationPropertyIdBranch);
}
