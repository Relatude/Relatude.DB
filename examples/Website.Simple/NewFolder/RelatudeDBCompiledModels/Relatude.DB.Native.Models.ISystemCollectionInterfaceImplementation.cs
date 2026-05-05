using System;
using System.Linq;
using System.Collections.Generic;
namespace Relatude.DB.Native.Models;
public sealed class __ISystemCollection :Relatude.DB.Native.Models.ISystemCollection ,Relatude.DB.Datamodels.INodeShellAccess {
[System.Text.Json.Serialization.JsonIgnore]
public Relatude.DB.Datamodels.NodeDataShell __NodeDataShell { get; }
public __ISystemCollection(Relatude.DB.Datamodels.NodeDataShell shell){    this.__NodeDataShell = shell;
}
public Guid Id{ 
get { return __NodeDataShell.NodeData.Id; } 
set { __NodeDataShell.NodeData.Id = value; } 
 }
static Guid gbe94c3592b084f58b116bb5fef89a5cc = Guid.Parse("be94c359-2b08-4f58-b116-bb5fef89a5cc");
static Guid gdecb98300b7fc9cb69a6fe2b24f7a647 = Guid.Parse("decb9830-0b7f-c9cb-69a6-fe2b24f7a647");
static Guid g9f38e5830c9e6e95a42fca2f0ca1fbc7 = Guid.Parse("9f38e583-0c9e-6e95-a42f-ca2f0ca1fbc7");
static Guid gb835577e84a24fa3a85044ab2112e6cf = Guid.Parse("b835577e-84a2-4fa3-a850-44ab2112e6cf");
static Guid g1e282f9f3bd24230abcbf9e840145159 = Guid.Parse("1e282f9f-3bd2-4230-abcb-f9e840145159");
static Guid g57c752bfe36443e19163d8ffea004bad = Guid.Parse("57c752bf-e364-43e1-9163-d8ffea004bad");
static Guid gcf885adf112141d885e670c553345dd0 = Guid.Parse("cf885adf-1121-41d8-85e6-70c553345dd0");
static Guid gc1ea2c8adbe84fa0a020ae05507305b6 = Guid.Parse("c1ea2c8a-dbe8-4fa0-a020-ae05507305b6");
public string Name{ 
get { return __NodeDataShell.GetValue<string>(gdecb98300b7fc9cb69a6fe2b24f7a647); } 
set { __NodeDataShell.SetValue(gdecb98300b7fc9cb69a6fe2b24f7a647,value); } 
 }
Relatude.DB.Native.Models.CollectionsToCultures.Cultures _Cultures = null;
public Relatude.DB.Native.Models.CollectionsToCultures.Cultures Cultures{ 
set{ throw new Exception("Relations properties cannot be set. "); }
get{
if(_Cultures == null) {
var nodeData = this.__NodeDataShell.NodeData;
var store = this.__NodeDataShell.Store;
var relations = nodeData.Relations;
_Cultures = new ();
if(relations.TryGetManyRelation(g9f38e5830c9e6e95a42fca2f0ca1fbc7, out var vg9f38e5830c9e6e95a42fca2f0ca1fbc7)){
_Cultures.Initialize(store, nodeData.Id, g9f38e5830c9e6e95a42fca2f0ca1fbc7, (Relatude.DB.Datamodels.NodeDataWithRelations[])vg9f38e5830c9e6e95a42fca2f0ca1fbc7);

}else{
_Cultures.Initialize(store, nodeData.Id, g9f38e5830c9e6e95a42fca2f0ca1fbc7, null);

}
 }
return _Cultures;
 }
 }
 }
