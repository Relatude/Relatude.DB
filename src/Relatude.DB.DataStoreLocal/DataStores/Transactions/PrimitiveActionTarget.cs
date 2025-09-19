using Relatude.DB.Transactions;

namespace Relatude.DB.DataStores.Transactions;
public enum PrimitiveActionTarget: byte {
    Node = 0,
    Relation = 1,
    Binary = 2,
}
