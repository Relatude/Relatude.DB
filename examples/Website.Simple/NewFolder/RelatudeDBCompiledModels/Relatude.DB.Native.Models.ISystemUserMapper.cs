using System;
using System.Linq;
using System.Collections.Generic;
namespace _Relatude.DB.Native.Models;
[Relatude.DB.Nodes.TypeGuid(Guid = "243f1514-46c3-4106-9c6a-4a25fb39238b")]
public sealed class _ISystemUser :Relatude.DB.Nodes.IValueMapper{
static Guid g243f151446c341069c6a4a25fb39238b = Guid.Parse("243f1514-46c3-4106-9c6a-4a25fb39238b");
static Guid g4f64452a7dbcf83fade1c265a040b423 = Guid.Parse("4f64452a-7dbc-f83f-ade1-c265a040b423");
static Guid gd476891ef1d0f541283c4abf258da8bd = Guid.Parse("d476891e-f1d0-f541-283c-4abf258da8bd");
static Guid gb835577e84a24fa3a85044ab2112e6cf = Guid.Parse("b835577e-84a2-4fa3-a850-44ab2112e6cf");
static Guid g1e282f9f3bd24230abcbf9e840145159 = Guid.Parse("1e282f9f-3bd2-4230-abcb-f9e840145159");
static Guid g57c752bfe36443e19163d8ffea004bad = Guid.Parse("57c752bf-e364-43e1-9163-d8ffea004bad");
static Guid gcf885adf112141d885e670c553345dd0 = Guid.Parse("cf885adf-1121-41d8-85e6-70c553345dd0");
static Guid gc1ea2c8adbe84fa0a020ae05507305b6 = Guid.Parse("c1ea2c8a-dbe8-4fa0-a020-ae05507305b6");
public Relatude.DB.Datamodels.INodeDataExternal CreateNodeDataFromObject(object obj, Relatude.DB.Nodes.RelatedCollection related, Relatude.DB.Nodes.NodeStore store){
var values = new Relatude.DB.Datamodels.Properties<object>(1);
var node = (Relatude.DB.Native.Models.ISystemUser)obj;
Guid gid = node.Id;
int uid = 0;
if(uid == 0 && gid == Guid.Empty) gid = Guid.NewGuid();
values.Add(g4f64452a7dbcf83fade1c265a040b423, (int)node.UserType);
var createdUtc = DateTime.MinValue;
var changedUtc = DateTime.UtcNow;
var nodeData = new Relatude.DB.Datamodels.NodeData(gid, uid, g243f151446c341069c6a4a25fb39238b, createdUtc, changedUtc, values, null);
if(related!=null){
}
return nodeData;
}
public object NodeDataToObject(Relatude.DB.Datamodels.INodeDataExternal nodeData, Relatude.DB.Nodes.NodeStore store){
var relations = nodeData.Relations;
var obj = new Relatude.DB.Native.Models.__ISystemUser(new Relatude.DB.Datamodels.NodeDataShell(store , nodeData, true));
return obj;
}
public bool TryGetIdGuidAndCreateIfPossible(object obj, out Guid id){
var node = (Relatude.DB.Native.Models.ISystemUser)obj;
id = node.Id;
if(id == Guid.Empty){
  id = Guid.NewGuid();
  node.Id = id;
}
return true;
}
public bool TryGetIdGuid(object obj, out Guid id){
var node = (Relatude.DB.Native.Models.ISystemUser)obj;
id = node.Id;
if(id == Guid.Empty) return false;
return true;
}
public bool TryGetIdUInt(object obj, out int id){
var node = (Relatude.DB.Native.Models.ISystemUser)obj;
id = 0;
return false;
}
}
