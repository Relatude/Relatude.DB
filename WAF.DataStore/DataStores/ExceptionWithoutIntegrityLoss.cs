using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WAF.DataStores {
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
    public class NodeConstraintException : ExceptionWithoutIntegrityLoss {
        public NodeConstraintException(string message, Guid nodeId) : base(message) {
            NodeId = nodeId;
        }
        public Guid NodeId { get; }
    }
}
