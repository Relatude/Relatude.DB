using System;
using System.Linq;
using System.Collections.Generic;
namespace _Relatude.DB.Demo.Models;
[Relatude.DB.Nodes.TypeGuid(Guid = "949f9ccf-671d-13ee-81b1-9b4eb822689e")]
public sealed class _DemoParagraph :Relatude.DB.Nodes.IValueMapper{
static Guid g949f9ccf671d13ee81b19b4eb822689e = Guid.Parse("949f9ccf-671d-13ee-81b1-9b4eb822689e");
static Guid g5d3b9a44329ea8630861c97b232553c7 = Guid.Parse("5d3b9a44-329e-a863-0861-c97b232553c7");
static Guid gaadf8f3924d7e53a33625259459166c5 = Guid.Parse("aadf8f39-24d7-e53a-3362-5259459166c5");
static Guid gb835577e84a24fa3a85044ab2112e6cf = Guid.Parse("b835577e-84a2-4fa3-a850-44ab2112e6cf");
static Guid g1e282f9f3bd24230abcbf9e840145159 = Guid.Parse("1e282f9f-3bd2-4230-abcb-f9e840145159");
static Guid g57c752bfe36443e19163d8ffea004bad = Guid.Parse("57c752bf-e364-43e1-9163-d8ffea004bad");
static Guid gcf885adf112141d885e670c553345dd0 = Guid.Parse("cf885adf-1121-41d8-85e6-70c553345dd0");
static Guid gc1ea2c8adbe84fa0a020ae05507305b6 = Guid.Parse("c1ea2c8a-dbe8-4fa0-a020-ae05507305b6");
public Relatude.DB.Datamodels.INodeDataExternal CreateNodeDataFromObject(object obj, Relatude.DB.Nodes.RelatedCollection related, Relatude.DB.Nodes.NodeStore store, Relatude.DB.Common.PropertyPath? propertyPath){
var values = new Relatude.DB.Datamodels.Properties<object>(2);
var node = (Relatude.DB.Demo.Models.DemoParagraph)obj;
Guid gid = node.Id;
int uid = 0;
if(uid == 0 && gid == Guid.Empty) gid = Guid.NewGuid();
{
var nodePath = propertyPath == null ? new (gid) : propertyPath.CreateInnerNodePath(gid);
values.Add(g5d3b9a44329ea8630861c97b232553c7, Relatude.DB.Common.FileValue.CopyAndEnsurePropertyPath(node.File, nodePath.CreatePropertyPath(g5d3b9a44329ea8630861c97b232553c7)));
}
values.Add(gaadf8f3924d7e53a33625259459166c5, node.Code);
var createdUtc = DateTime.MinValue;
var changedUtc = DateTime.UtcNow;
var nodeData = new Relatude.DB.Datamodels.NodeData(gid, uid, g949f9ccf671d13ee81b19b4eb822689e, createdUtc, changedUtc, values, null);
if(related!=null){
}
return nodeData;
}
public object NodeDataToObject(Relatude.DB.Datamodels.INodeDataExternal nodeData, Relatude.DB.Nodes.NodeStore store,Relatude.DB.Common.PropertyPath? propertyPath){
var relations = nodeData.Relations;
var obj = new Relatude.DB.Demo.Models.DemoParagraph();
obj.Id = nodeData.Id;
{
var nodePath = propertyPath == null ? new(nodeData.Id) : propertyPath.CreateInnerNodePath(nodeData.Id);
var filePropertyPath = nodePath.CreatePropertyPath(g5d3b9a44329ea8630861c97b232553c7);
if (nodeData.TryGetValue(g5d3b9a44329ea8630861c97b232553c7, out var v) && v is Relatude.DB.Common.FileValue fv) {
obj.File = Relatude.DB.Common.FileValue.CopyAndEnsurePropertyPath(fv, filePropertyPath);
} else {
obj.File = Relatude.DB.Common.FileValue.CreateEmptyWithPropertyPath(filePropertyPath);
}
}
{ obj.Code = nodeData.TryGetValue(gaadf8f3924d7e53a33625259459166c5, out var v) ? (string)v : ""; }
return obj;
}
public bool TryGetIdGuidAndCreateIfPossible(object obj, out Guid id){
var node = (Relatude.DB.Demo.Models.DemoParagraph)obj;
id = node.Id;
if(id == Guid.Empty){
  id = Guid.NewGuid();
  node.Id = id;
}
return true;
}
public bool TryGetIdGuid(object obj, out Guid id){
var node = (Relatude.DB.Demo.Models.DemoParagraph)obj;
id = node.Id;
if(id == Guid.Empty) return false;
return true;
}
public bool TryGetIdUInt(object obj, out int id){
var node = (Relatude.DB.Demo.Models.DemoParagraph)obj;
id = 0;
return false;
}
}
