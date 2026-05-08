using System;
using System.Linq;
using System.Collections.Generic;
namespace _Relatude.DB.Native.Models;
[Relatude.DB.Nodes.TypeGuid(Guid = "afd3b9e4-7565-49ae-ac3b-ed20b5ccfe6a")]
public sealed class _ISystemUserGroup :Relatude.DB.Nodes.IValueMapper{
static Guid gafd3b9e4756549aeac3bed20b5ccfe6a = Guid.Parse("afd3b9e4-7565-49ae-ac3b-ed20b5ccfe6a");
static Guid g49fd93845f686aca947292640550e9e2 = Guid.Parse("49fd9384-5f68-6aca-9472-92640550e9e2");
static Guid gc845e4b47e1d1991c92225f00eb5a5a2 = Guid.Parse("c845e4b4-7e1d-1991-c922-25f00eb5a5a2");
static Guid g6cf54ce8ed2cc7db15c6406c5a8810e3 = Guid.Parse("6cf54ce8-ed2c-c7db-15c6-406c5a8810e3");
static Guid gd5d61b7f9d28c98e4128d476a5e0fa25 = Guid.Parse("d5d61b7f-9d28-c98e-4128-d476a5e0fa25");
static Guid gb835577e84a24fa3a85044ab2112e6cf = Guid.Parse("b835577e-84a2-4fa3-a850-44ab2112e6cf");
static Guid g1e282f9f3bd24230abcbf9e840145159 = Guid.Parse("1e282f9f-3bd2-4230-abcb-f9e840145159");
static Guid g57c752bfe36443e19163d8ffea004bad = Guid.Parse("57c752bf-e364-43e1-9163-d8ffea004bad");
static Guid gcf885adf112141d885e670c553345dd0 = Guid.Parse("cf885adf-1121-41d8-85e6-70c553345dd0");
static Guid gc1ea2c8adbe84fa0a020ae05507305b6 = Guid.Parse("c1ea2c8a-dbe8-4fa0-a020-ae05507305b6");
public Relatude.DB.Datamodels.INodeDataExternal CreateNodeDataFromObject(object obj, Relatude.DB.Nodes.RelatedCollection related, Relatude.DB.Nodes.NodeStore store, Relatude.DB.Common.PropertyPath? propertyPath){
var values = new Relatude.DB.Datamodels.Properties<object>(1);
var node = (Relatude.DB.Native.Models.ISystemUserGroup)obj;
Guid gid = node.Id;
int uid = 0;
if(uid == 0 && gid == Guid.Empty) gid = Guid.NewGuid();
values.Add(g49fd93845f686aca947292640550e9e2, node.GroupName);
var createdUtc = DateTime.MinValue;
var changedUtc = DateTime.UtcNow;
var nodeData = new Relatude.DB.Datamodels.NodeData(gid, uid, gafd3b9e4756549aeac3bed20b5ccfe6a, createdUtc, changedUtc, values, null);
if(related!=null){
}
return nodeData;
}
public object NodeDataToObject(Relatude.DB.Datamodels.INodeDataExternal nodeData, Relatude.DB.Nodes.NodeStore store,Relatude.DB.Common.PropertyPath? propertyPath){
var relations = nodeData.Relations;
var obj = new Relatude.DB.Native.Models.__ISystemUserGroup(new Relatude.DB.Datamodels.NodeDataShell(store , nodeData, true));
return obj;
}
public bool TryGetIdGuidAndCreateIfPossible(object obj, out Guid id){
var node = (Relatude.DB.Native.Models.ISystemUserGroup)obj;
id = node.Id;
if(id == Guid.Empty){
  id = Guid.NewGuid();
  node.Id = id;
}
return true;
}
public bool TryGetIdGuid(object obj, out Guid id){
var node = (Relatude.DB.Native.Models.ISystemUserGroup)obj;
id = node.Id;
if(id == Guid.Empty) return false;
return true;
}
public bool TryGetIdUInt(object obj, out int id){
var node = (Relatude.DB.Native.Models.ISystemUserGroup)obj;
id = 0;
return false;
}
}
