using WAF.Datamodels;

namespace WAF.Transactions {
    public enum ActionTarget : byte {
        Node = 0,
        Relation = 1,
        NodeProperty = 2,
        Collection = 3,
    }
    public abstract class ActionBase {
        public int _index; // for internal use only, do not set
        public ActionBase(ActionTarget target) { ActionTarget = target; }
        public ActionTarget ActionTarget { get; }
        abstract public string OperationName();
        abstract override public string ToString();
        abstract public string ToString(Datamodel dm);
    }
}
