using System;
using System.Linq;
using System.Collections.Generic;
namespace _Relatude.DB.Demo.Models;
[Relatude.DB.Nodes.TypeGuid(Guid = "398c0afb-2af9-7cc6-ebd2-a5150784b876")]
public sealed class _DemoArticle :Relatude.DB.Nodes.IValueMapper{
static Guid g398c0afb2af97cc6ebd2a5150784b876 = Guid.Parse("398c0afb-2af9-7cc6-ebd2-a5150784b876");
static Guid g1c37b7fddc93e90f8f7b191f860ab149 = Guid.Parse("1c37b7fd-dc93-e90f-8f7b-191f860ab149");
static Guid g230828fc736329459bff52f29605d846 = Guid.Parse("230828fc-7363-2945-9bff-52f29605d846");
static Guid ga3f9582bc80035e89bbc9b64bdecc1c1 = Guid.Parse("a3f9582b-c800-35e8-9bbc-9b64bdecc1c1");
static Guid g762fce84b9aa1c3d1c5ce667a0ac14fd = Guid.Parse("762fce84-b9aa-1c3d-1c5c-e667a0ac14fd");
static Guid gd43c6743c0dede84d40e328841b430a1 = Guid.Parse("d43c6743-c0de-de84-d40e-328841b430a1");
static Guid gd65cef817660f343fa7cb9591c47239c = Guid.Parse("d65cef81-7660-f343-fa7c-b9591c47239c");
static Guid g01e7e657f942ed3263a374e100cefe27 = Guid.Parse("01e7e657-f942-ed32-63a3-74e100cefe27");
static Guid g01e7e657f942ed3263a374e100cefe27_KeyProperty = Guid.Parse("aadf8f39-24d7-e53a-3362-5259459166c5");
static Guid g242c80723d58e7c99c494fd1b1537ece = Guid.Parse("242c8072-3d58-e7c9-9c49-4fd1b1537ece");
static Guid gb50afb35a60851572abcdbd5926c6461 = Guid.Parse("b50afb35-a608-5157-2abc-dbd5926c6461");
static Guid gb835577e84a24fa3a85044ab2112e6cf = Guid.Parse("b835577e-84a2-4fa3-a850-44ab2112e6cf");
static Guid g1e282f9f3bd24230abcbf9e840145159 = Guid.Parse("1e282f9f-3bd2-4230-abcb-f9e840145159");
static Guid g57c752bfe36443e19163d8ffea004bad = Guid.Parse("57c752bf-e364-43e1-9163-d8ffea004bad");
static Guid gcf885adf112141d885e670c553345dd0 = Guid.Parse("cf885adf-1121-41d8-85e6-70c553345dd0");
static Guid gc1ea2c8adbe84fa0a020ae05507305b6 = Guid.Parse("c1ea2c8a-dbe8-4fa0-a020-ae05507305b6");
public Relatude.DB.Datamodels.INodeDataExternal CreateNodeDataFromObject(object obj, Relatude.DB.Nodes.RelatedCollection related, Relatude.DB.Nodes.NodeStore store, Relatude.DB.Common.PropertyPath? propertyPath){
var values = new Relatude.DB.Datamodels.Properties<object>(7);
var node = (Relatude.DB.Demo.Models.DemoArticle)obj;
Guid gid = node.Id;
int uid = 0;
if(uid == 0 && gid == Guid.Empty) gid = Guid.NewGuid();
values.Add(g1c37b7fddc93e90f8f7b191f860ab149, node.Title);
values.Add(g230828fc736329459bff52f29605d846, node.Content);
values.Add(ga3f9582bc80035e89bbc9b64bdecc1c1, node.Size);
values.Add(g762fce84b9aa1c3d1c5ce667a0ac14fd, node.CreatedAt);
values.Add(gd43c6743c0dede84d40e328841b430a1, node.UpdatedAt);
{
var nodePath = propertyPath == null ? new (gid) : propertyPath.CreateInnerNodePath(gid);
node.File = Relatude.DB.Common.FileValue.CopyAndEnsurePropertyPath(node.File, nodePath.CreatePropertyPath(gd65cef817660f343fa7cb9591c47239c));
values.Add(gd65cef817660f343fa7cb9591c47239c, node.File);
}
{
var nodePath = propertyPath == null ? new (gid) : propertyPath.CreateInnerNodePath(gid);
values.Add(g01e7e657f942ed3263a374e100cefe27, node.Paragraphs.GetNodeDataMap(nodePath.CreatePropertyPath(g01e7e657f942ed3263a374e100cefe27), g01e7e657f942ed3263a374e100cefe27_KeyProperty,store.Mapper));
}
var createdUtc = DateTime.MinValue;
var changedUtc = DateTime.UtcNow;
var nodeData = new Relatude.DB.Datamodels.NodeData(gid, uid, g398c0afb2af97cc6ebd2a5150784b876, createdUtc, changedUtc, values, null);
if(related!=null){
}
return nodeData;
}
public object NodeDataToObject(Relatude.DB.Datamodels.INodeDataExternal nodeData, Relatude.DB.Nodes.NodeStore store,Relatude.DB.Common.PropertyPath? propertyPath){
var relations = nodeData.Relations;
var obj = new Relatude.DB.Demo.Models.DemoArticle();
obj.Id = nodeData.Id;
obj.DisplayName = nodeData.DisplayName;
obj.Address = nodeData.Address;
obj.Meta = new Relatude.DB.Datamodels.NodeMeta(nodeData);
{ obj.Title = nodeData.TryGetValue(g1c37b7fddc93e90f8f7b191f860ab149, out var v) ? (string)v : ""; }
{ obj.Content = nodeData.TryGetValue(g230828fc736329459bff52f29605d846, out var v) ? (string)v : ""; }
{ obj.Size = nodeData.TryGetValue(ga3f9582bc80035e89bbc9b64bdecc1c1, out var v) ? (int)v : 0; }
{ obj.CreatedAt = nodeData.TryGetValue(g762fce84b9aa1c3d1c5ce667a0ac14fd, out var v) ? (DateTime)v : new DateTime(0, DateTimeKind.Utc); }
{ obj.UpdatedAt = nodeData.TryGetValue(gd43c6743c0dede84d40e328841b430a1, out var v) ? (DateTime)v : new DateTime(0, DateTimeKind.Utc); }
{
var nodePath = propertyPath == null ? new(nodeData.Id) : propertyPath.CreateInnerNodePath(nodeData.Id);
var filePropertyPath = nodePath.CreatePropertyPath(gd65cef817660f343fa7cb9591c47239c);
if (nodeData.TryGetValue(gd65cef817660f343fa7cb9591c47239c, out var v) && v is Relatude.DB.Common.FileValue fv) {
obj.File = Relatude.DB.Common.FileValue.CopyAndEnsurePropertyPath(fv, filePropertyPath);
} else {
obj.File = Relatude.DB.Common.FileValue.CreateEmptyWithPropertyPath(filePropertyPath);
}
}
{
if(nodeData.TryGetValue(g01e7e657f942ed3263a374e100cefe27, out var v)){
var vT = (Relatude.DB.Datamodels.InnerNodeDataMap<string>)v;
obj.Paragraphs = new(g01e7e657f942ed3263a374e100cefe27_KeyProperty, vT, store.Mapper);
} else{ 
obj.Paragraphs = [];
}
}
Relatude.DB.Datamodels.NodeDataWithRelations vg242c80723d58e7c99c494fd1b1537ece = null;
bool? vg242c80723d58e7c99c494fd1b1537ece_IsSet = false;
relations.LookUpOneRelation(g242c80723d58e7c99c494fd1b1537ece, out var vg242c80723d58e7c99c494fd1b1537ece_Included, ref vg242c80723d58e7c99c494fd1b1537ece, ref vg242c80723d58e7c99c494fd1b1537ece_IsSet);
if(vg242c80723d58e7c99c494fd1b1537ece_Included){
obj.Parent.Initialize(store, nodeData.Id, g242c80723d58e7c99c494fd1b1537ece, (Relatude.DB.Datamodels.NodeDataWithRelations)vg242c80723d58e7c99c494fd1b1537ece, true);

}else{
obj.Parent.Initialize(store, nodeData.Id, g242c80723d58e7c99c494fd1b1537ece, null, null);

}
if(relations.TryGetManyRelation(gb50afb35a60851572abcdbd5926c6461, out var vgb50afb35a60851572abcdbd5926c6461)){
obj.Children.Initialize(store, nodeData.Id, gb50afb35a60851572abcdbd5926c6461, (Relatude.DB.Datamodels.NodeDataWithRelations[])vgb50afb35a60851572abcdbd5926c6461);

}else{
obj.Children.Initialize(store, nodeData.Id, gb50afb35a60851572abcdbd5926c6461, null);

}
return obj;
}
public bool TryGetIdGuidAndCreateIfPossible(object obj, out Guid id){
var node = (Relatude.DB.Demo.Models.DemoArticle)obj;
id = node.Id;
if(id == Guid.Empty){
  id = Guid.NewGuid();
  node.Id = id;
}
return true;
}
public bool TryGetIdGuid(object obj, out Guid id){
var node = (Relatude.DB.Demo.Models.DemoArticle)obj;
id = node.Id;
if(id == Guid.Empty) return false;
return true;
}
public bool TryGetIdUInt(object obj, out int id){
var node = (Relatude.DB.Demo.Models.DemoArticle)obj;
id = 0;
return false;
}
}
