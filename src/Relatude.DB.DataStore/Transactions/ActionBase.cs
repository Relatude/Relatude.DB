using Relatude.DB.Datamodels;

namespace Relatude.DB.Transactions {
    public enum ActionTarget : byte {
        Node = 0,
        Relation = 1,
        NodeProperty = 2,
        NodeRevision = 3,
    }
    public abstract class ActionBase {
        public int _i; // for internal use only, do not set. Used to connect actions to operation results for plugins
        public ActionBase(ActionTarget target) { ActionTarget = target; }
        public ActionTarget ActionTarget { get; }
        abstract public string OperationName();
        abstract override public string ToString();
        abstract public string ToString(Datamodel dm);
    }
}
