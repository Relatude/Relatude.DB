using System;
using System.Linq;
using System.Collections.Generic;
namespace _Relatude.DB.Native.Models;
[Relatude.DB.Nodes.TypeGuid(Guid = "be94c359-2b08-4f58-b116-bb5fef89a5cc")]
public sealed class _ISystemCollection :Relatude.DB.Nodes.IValueMapper{
static Guid gbe94c3592b084f58b116bb5fef89a5cc = Guid.Parse("be94c359-2b08-4f58-b116-bb5fef89a5cc");
static Guid gdecb98300b7fc9cb69a6fe2b24f7a647 = Guid.Parse("decb9830-0b7f-c9cb-69a6-fe2b24f7a647");
static Guid g9f38e5830c9e6e95a42fca2f0ca1fbc7 = Guid.Parse("9f38e583-0c9e-6e95-a42f-ca2f0ca1fbc7");
static Guid gb835577e84a24fa3a85044ab2112e6cf = Guid.Parse("b835577e-84a2-4fa3-a850-44ab2112e6cf");
static Guid g1e282f9f3bd24230abcbf9e840145159 = Guid.Parse("1e282f9f-3bd2-4230-abcb-f9e840145159");
static Guid g57c752bfe36443e19163d8ffea004bad = Guid.Parse("57c752bf-e364-43e1-9163-d8ffea004bad");
static Guid gcf885adf112141d885e670c553345dd0 = Guid.Parse("cf885adf-1121-41d8-85e6-70c553345dd0");
static Guid gc1ea2c8adbe84fa0a020ae05507305b6 = Guid.Parse("c1ea2c8a-dbe8-4fa0-a020-ae05507305b6");
public Relatude.DB.Datamodels.INodeDataExternal CreateNodeDataFromObject(object obj, Relatude.DB.Nodes.RelatedCollection related, Relatude.DB.Nodes.NodeStore store){
var values = new Relatude.DB.Datamodels.Properties<object>(1);
var node = (Relatude.DB.Native.Models.ISystemCollection)obj;
Guid gid = node.Id;
int uid = 0;
if(uid == 0 && gid == Guid.Empty) gid = Guid.NewGuid();
values.Add(gdecb98300b7fc9cb69a6fe2b24f7a647, node.Name);
var createdUtc = DateTime.MinValue;
var changedUtc = DateTime.UtcNow;
var nodeData = new Relatude.DB.Datamodels.NodeData(gid, uid, gbe94c3592b084f58b116bb5fef89a5cc, createdUtc, changedUtc, values, null);
if(related!=null){
}
return nodeData;
}
public object NodeDataToObject(Relatude.DB.Datamodels.INodeDataExternal nodeData, Relatude.DB.Nodes.NodeStore store){
var relations = nodeData.Relations;
var obj = new Relatude.DB.Native.Models.__ISystemCollection(new Relatude.DB.Datamodels.NodeDataShell(store , nodeData, true));
return obj;
}
public bool TryGetIdGuidAndCreateIfPossible(object obj, out Guid id){
var node = (Relatude.DB.Native.Models.ISystemCollection)obj;
id = node.Id;
if(id == Guid.Empty){
  id = Guid.NewGuid();
  node.Id = id;
}
return true;
}
public bool TryGetIdGuid(object obj, out Guid id){
var node = (Relatude.DB.Native.Models.ISystemCollection)obj;
id = node.Id;
if(id == Guid.Empty) return false;
return true;
}
public bool TryGetIdUInt(object obj, out int id){
var node = (Relatude.DB.Native.Models.ISystemCollection)obj;
id = 0;
return false;
}
}
