//using System;
//namespace Relatude.DB.Native.Models;
//public sealed class __ISystemUser : Relatude.DB.Native.Models.ISystemUser, Relatude.DB.Datamodels.INodeShellAccess {
//    public Relatude.DB.Datamodels.NodeDataShell __NodeDataShell { get; }
//    public __ISystemUser(Relatude.DB.Datamodels.NodeDataShell shell) {
//        this.__NodeDataShell = shell;
//    }
//    public Guid Id {
//        get { return __NodeDataShell.NodeData.Id; }
//        set { __NodeDataShell.NodeData.Id = value; }
//    }
//    static Guid g243f151446c341069c6a4a25fb39238b = Guid.Parse("243f1514-46c3-4106-9c6a-4a25fb39238b");
//    static Guid g4f64452a7dbcf83fade1c265a040b423 = Guid.Parse("4f64452a-7dbc-f83f-ade1-c265a040b423");
//    static Guid gd476891ef1d0f541283c4abf258da8bd = Guid.Parse("d476891e-f1d0-f541-283c-4abf258da8bd");
//    static Guid g02b454826988f2c2157e4d7862381ef0 = Guid.Parse("02b45482-6988-f2c2-157e-4d7862381ef0");
//    static Guid gb835577e84a24fa3a85044ab2112e6cf = Guid.Parse("b835577e-84a2-4fa3-a850-44ab2112e6cf");
//    static Guid g1e282f9f3bd24230abcbf9e840145159 = Guid.Parse("1e282f9f-3bd2-4230-abcb-f9e840145159");
//    public Relatude.DB.Native.SystemUserType UserType {
//        get { return __NodeDataShell.GetValue<Relatude.DB.Native.SystemUserType>(g4f64452a7dbcf83fade1c265a040b423); }
//        set { __NodeDataShell.SetValue(g4f64452a7dbcf83fade1c265a040b423, value); }
//    }
//    Relatude.DB.Native.Models.UsersToGroups.Groups _Memberships = null;
//    public Relatude.DB.Native.Models.UsersToGroups.Groups Memberships {
//        set { throw new Exception("Relations properties cannot be set. "); }
//        get {
//            if (_Memberships == null) {
//                var nodeData = this.__NodeDataShell.NodeData;
//                var store = this.__NodeDataShell.Store;
//                var relations = nodeData.Relations;
//                _Memberships = new();
//                if (relations.TryGetManyRelation(gd476891ef1d0f541283c4abf258da8bd, out var vgd476891ef1d0f541283c4abf258da8bd)) {
//                    _Memberships.Initialize(store, nodeData.__Id, nodeData.Id, gd476891ef1d0f541283c4abf258da8bd, (Relatude.DB.Datamodels.NodeDataWithRelations[])vgd476891ef1d0f541283c4abf258da8bd);

//                } else {
//                    _Memberships.Initialize(store, nodeData.__Id, nodeData.Id, gd476891ef1d0f541283c4abf258da8bd, null);

//                }
//            }
//            return _Memberships;
//        }
//    }
//    Relatude.DB.Native.Models.AAAAUser[] _Messssmberships = null;
//    public Relatude.DB.Native.Models.AAAAUser[] Messssmberships {
//        set { throw new Exception("Relations properties cannot be set. "); }
//        get {
//            if (_Messssmberships == null) {
//                var nodeData = this.__NodeDataShell.NodeData;
//                var store = this.__NodeDataShell.Store;
//                var relations = nodeData.Relations;
//                if (relations.TryGetManyRelation(g02b454826988f2c2157e4d7862381ef0, out var vg02b454826988f2c2157e4d7862381ef0)) {
//                    _Messssmberships = store.GetRelated<Relatude.DB.Native.Models.AAAAUser>((Relatude.DB.Datamodels.NodeDataWithRelations[])vg02b454826988f2c2157e4d7862381ef0).ToArray();
//                }
//            }
//            return _Messssmberships;
//        }
//    }
//}
