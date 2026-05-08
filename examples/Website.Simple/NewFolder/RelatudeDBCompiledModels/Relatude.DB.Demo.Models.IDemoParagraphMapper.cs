using System;
using System.Linq;
using System.Collections.Generic;
namespace _Relatude.DB.Demo.Models;
[Relatude.DB.Nodes.TypeGuid(Guid = "64b40d41-8563-c51e-cbf2-5956a0eddc25")]
public sealed class _IDemoParagraph :Relatude.DB.Nodes.IValueMapper{
static Guid g64b40d418563c51ecbf25956a0eddc25 = Guid.Parse("64b40d41-8563-c51e-cbf2-5956a0eddc25");
static Guid g08baf457a5343b364a3cfe55f71378d3 = Guid.Parse("08baf457-a534-3b36-4a3c-fe55f71378d3");
static Guid g4f75758e6bfc96eeb2334cca03f3cd64 = Guid.Parse("4f75758e-6bfc-96ee-b233-4cca03f3cd64");
static Guid gb835577e84a24fa3a85044ab2112e6cf = Guid.Parse("b835577e-84a2-4fa3-a850-44ab2112e6cf");
static Guid g1e282f9f3bd24230abcbf9e840145159 = Guid.Parse("1e282f9f-3bd2-4230-abcb-f9e840145159");
static Guid g57c752bfe36443e19163d8ffea004bad = Guid.Parse("57c752bf-e364-43e1-9163-d8ffea004bad");
static Guid gcf885adf112141d885e670c553345dd0 = Guid.Parse("cf885adf-1121-41d8-85e6-70c553345dd0");
static Guid gc1ea2c8adbe84fa0a020ae05507305b6 = Guid.Parse("c1ea2c8a-dbe8-4fa0-a020-ae05507305b6");
public Relatude.DB.Datamodels.INodeDataExternal CreateNodeDataFromObject(object obj, Relatude.DB.Nodes.RelatedCollection related, Relatude.DB.Nodes.NodeStore store, Relatude.DB.Common.PropertyPath? propertyPath){
var values = new Relatude.DB.Datamodels.Properties<object>(2);
var node = (Relatude.DB.Demo.Models.IDemoParagraph)obj;
Guid gid = node.Id;
int uid = 0;
if(uid == 0 && gid == Guid.Empty) gid = Guid.NewGuid();
{
var nodePath = propertyPath == null ? new (gid) : propertyPath.CreateInnerNodePath(gid);
node.File = Relatude.DB.Common.FileValue.CopyAndEnsurePropertyPath(node.File, nodePath.CreatePropertyPath(g08baf457a5343b364a3cfe55f71378d3));
values.Add(g08baf457a5343b364a3cfe55f71378d3, node.File);
}
values.Add(g4f75758e6bfc96eeb2334cca03f3cd64, node.Code);
var createdUtc = DateTime.MinValue;
var changedUtc = DateTime.UtcNow;
var nodeData = new Relatude.DB.Datamodels.NodeData(gid, uid, g64b40d418563c51ecbf25956a0eddc25, createdUtc, changedUtc, values, null);
if(related!=null){
}
return nodeData;
}
public object NodeDataToObject(Relatude.DB.Datamodels.INodeDataExternal nodeData, Relatude.DB.Nodes.NodeStore store,Relatude.DB.Common.PropertyPath? propertyPath){
var relations = nodeData.Relations;
var obj = new Relatude.DB.Demo.Models.__IDemoParagraph(new Relatude.DB.Datamodels.NodeDataShell(store , nodeData, true));
return obj;
}
public bool TryGetIdGuidAndCreateIfPossible(object obj, out Guid id){
var node = (Relatude.DB.Demo.Models.IDemoParagraph)obj;
id = node.Id;
if(id == Guid.Empty){
  id = Guid.NewGuid();
  node.Id = id;
}
return true;
}
public bool TryGetIdGuid(object obj, out Guid id){
var node = (Relatude.DB.Demo.Models.IDemoParagraph)obj;
id = node.Id;
if(id == Guid.Empty) return false;
return true;
}
public bool TryGetIdUInt(object obj, out int id){
var node = (Relatude.DB.Demo.Models.IDemoParagraph)obj;
id = 0;
return false;
}
}
