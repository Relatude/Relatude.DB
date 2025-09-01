using System;
namespace Relatude.DB.Datamodels;
public enum RelationType {
    OneOne = 0, // OneToOne, symmetric, where both sides is the same relation. PAIR. Example: "Spouse". A husband has a ONE wife, and a wife has ONE a relation back to the SAME husband
    OneToOne = 1, // OneToOne, direction nal and both sides are different. CHAIN. "IncomingLink" "OutgoingLink"
    OneToMany = 2, // OneToMany, where the source has one and the target has many. TREE. "Parent" "Children"
    ManyMany = 3, // ManyToMany, where both sides can have many. NETWORK WITHOUT DIRECTION. "two way roads"
    ManyToMany = 4, // ManyToMany, where both sides can have many, but the relation is not symmetric. NETWORK WITH DIRECTION. "one way roads"
}