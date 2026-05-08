using System;
using System.Linq;
using System.Collections.Generic;
namespace _Relatude.DB.Native.Models;
[Relatude.DB.Nodes.TypeGuid(Guid = "f51d3f3a-08d4-4b56-a00b-464e037f0009")]
public sealed class _ISystemCulture :Relatude.DB.Nodes.IValueMapper{
static Guid gf51d3f3a08d44b56a00b464e037f0009 = Guid.Parse("f51d3f3a-08d4-4b56-a00b-464e037f0009");
static Guid gf97c08b8b851fe8a97cd9b1dbec99f36 = Guid.Parse("f97c08b8-b851-fe8a-97cd-9b1dbec99f36");
static Guid gbd210a8ac0071ff8a807050617ac98da = Guid.Parse("bd210a8a-c007-1ff8-a807-050617ac98da");
static Guid gd9ddd7ab5d21f46e2deadcd6d5abac97 = Guid.Parse("d9ddd7ab-5d21-f46e-2dea-dcd6d5abac97");
static Guid g0f7523f9ceac32d184693ee204c91c05 = Guid.Parse("0f7523f9-ceac-32d1-8469-3ee204c91c05");
static Guid gb835577e84a24fa3a85044ab2112e6cf = Guid.Parse("b835577e-84a2-4fa3-a850-44ab2112e6cf");
static Guid g1e282f9f3bd24230abcbf9e840145159 = Guid.Parse("1e282f9f-3bd2-4230-abcb-f9e840145159");
static Guid g57c752bfe36443e19163d8ffea004bad = Guid.Parse("57c752bf-e364-43e1-9163-d8ffea004bad");
static Guid gcf885adf112141d885e670c553345dd0 = Guid.Parse("cf885adf-1121-41d8-85e6-70c553345dd0");
static Guid gc1ea2c8adbe84fa0a020ae05507305b6 = Guid.Parse("c1ea2c8a-dbe8-4fa0-a020-ae05507305b6");
public Relatude.DB.Datamodels.INodeDataExternal CreateNodeDataFromObject(object obj, Relatude.DB.Nodes.RelatedCollection related, Relatude.DB.Nodes.NodeStore store, Relatude.DB.Common.PropertyPath? propertyPath){
var values = new Relatude.DB.Datamodels.Properties<object>(3);
var node = (Relatude.DB.Native.Models.ISystemCulture)obj;
Guid gid = node.Id;
int uid = 0;
if(uid == 0 && gid == Guid.Empty) gid = Guid.NewGuid();
values.Add(gf97c08b8b851fe8a97cd9b1dbec99f36, node.CultureCode);
values.Add(gbd210a8ac0071ff8a807050617ac98da, node.NativeName);
values.Add(gd9ddd7ab5d21f46e2deadcd6d5abac97, node.EnglishName);
var createdUtc = DateTime.MinValue;
var changedUtc = DateTime.UtcNow;
var nodeData = new Relatude.DB.Datamodels.NodeData(gid, uid, gf51d3f3a08d44b56a00b464e037f0009, createdUtc, changedUtc, values, null);
if(related!=null){
}
return nodeData;
}
public object NodeDataToObject(Relatude.DB.Datamodels.INodeDataExternal nodeData, Relatude.DB.Nodes.NodeStore store,Relatude.DB.Common.PropertyPath? propertyPath){
var relations = nodeData.Relations;
var obj = new Relatude.DB.Native.Models.__ISystemCulture(new Relatude.DB.Datamodels.NodeDataShell(store , nodeData, true));
return obj;
}
public bool TryGetIdGuidAndCreateIfPossible(object obj, out Guid id){
var node = (Relatude.DB.Native.Models.ISystemCulture)obj;
id = node.Id;
if(id == Guid.Empty){
  id = Guid.NewGuid();
  node.Id = id;
}
return true;
}
public bool TryGetIdGuid(object obj, out Guid id){
var node = (Relatude.DB.Native.Models.ISystemCulture)obj;
id = node.Id;
if(id == Guid.Empty) return false;
return true;
}
public bool TryGetIdUInt(object obj, out int id){
var node = (Relatude.DB.Native.Models.ISystemCulture)obj;
id = 0;
return false;
}
}
