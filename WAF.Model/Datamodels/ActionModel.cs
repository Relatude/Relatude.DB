namespace WAF.Datamodels;
public class ActionModel {
    public required string TargetName { get; set; }
    public required string OperationName { get; set; }
    public required Guid NodeId;
    public Dictionary<string, string>? Values;
}
