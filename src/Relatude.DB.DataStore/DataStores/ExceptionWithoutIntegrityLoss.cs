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
    // what a refused write needed of its user
    public enum WriteAccessRight {
        Create, // the type's edit group, to make a node of it
        Edit, // the node's edit group, for any change to it
        Publish, // the node's publish group, for a change readers see
        Property, // a property's own write group
        UserType, // an administrator, to change a user's type
        Relation, // the edit group of either node, to link, unlink or reorder
    }
    /// <summary>A transaction would change what its user may not change. Nothing of it is written.</summary>
    public class WriteAccessException : ExceptionWithoutIntegrityLoss {
        public WriteAccessException(string message, Guid nodeId, WriteAccessRight right, Guid groupId, Guid propertyId = default) : base(message) {
            NodeId = nodeId;
            Right = right;
            GroupId = groupId;
            PropertyId = propertyId;
        }
        public Guid NodeId { get; }
        public WriteAccessRight Right { get; }
        public Guid GroupId { get; } // the group the user would have to be in
        public Guid PropertyId { get; } // for WriteAccessRight.Property
    }
}
