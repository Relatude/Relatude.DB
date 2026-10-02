using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.DB.DataStores {
    public class ExceptionWithoutIntegrityLoss : Exception {
        public ExceptionWithoutIntegrityLoss(string message) : base(message) { }
        public ExceptionWithoutIntegrityLoss(string message, Exception err) : base(message, err) { }
    }
    public class ValueConstraintException : ExceptionWithoutIntegrityLoss {
        public ValueConstraintException(string message, Guid propertyId) : base(message) {
            PropertyId = propertyId;
        }
        public Guid PropertyId { get; }
    }
    public class NodeLockedException : ExceptionWithoutIntegrityLoss {
        public NodeLockedException(string message) : base(message) {
        }
        public NodeLockedException(string message, Exception err) : base(message, err) {
        }
    }
    public class NodeConstraintException : ExceptionWithoutIntegrityLoss {
        public NodeConstraintException(string message, Guid nodeId) : base(message) {
            NodeId = nodeId;
        }
        public Guid NodeId { get; }
    }
    /// <summary>A transaction would leave a node type with fewer nodes than its MinNoInstances or more than its MaxNoInstances.</summary>
    public class NodeTypeConstraintException : ExceptionWithoutIntegrityLoss {
        public NodeTypeConstraintException(string message, Guid nodeTypeId) : base(message) {
            NodeTypeId = nodeTypeId;
        }
        public Guid NodeTypeId { get; }
    }
}
