using System;
using System.Linq;
using System.Collections.Generic;
namespace Relatude.DB.Native.Models;
public sealed class __ISystemCulture :Relatude.DB.Native.Models.ISystemCulture ,Relatude.DB.Datamodels.INodeShellAccess {
[System.Text.Json.Serialization.JsonIgnore]
public Relatude.DB.Datamodels.NodeDataShell __NodeDataShell { get; }
public __ISystemCulture(Relatude.DB.Datamodels.NodeDataShell shell){    this.__NodeDataShell = shell;
}
public Guid Id{ 
get { return __NodeDataShell.NodeData.Id; } 
set { __NodeDataShell.NodeData.Id = value; } 
 }
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
public string CultureCode{ 
get { return __NodeDataShell.GetValue<string>(gf97c08b8b851fe8a97cd9b1dbec99f36); } 
set { __NodeDataShell.SetValue(gf97c08b8b851fe8a97cd9b1dbec99f36,value); } 
 }
public string NativeName{ 
get { return __NodeDataShell.GetValue<string>(gbd210a8ac0071ff8a807050617ac98da); } 
set { __NodeDataShell.SetValue(gbd210a8ac0071ff8a807050617ac98da,value); } 
 }
public string EnglishName{ 
get { return __NodeDataShell.GetValue<string>(gd9ddd7ab5d21f46e2deadcd6d5abac97); } 
set { __NodeDataShell.SetValue(gd9ddd7ab5d21f46e2deadcd6d5abac97,value); } 
 }
Relatude.DB.Native.Models.CollectionsToCultures.Collections _Collections = null;
public Relatude.DB.Native.Models.CollectionsToCultures.Collections Collections{ 
set{ throw new Exception("Relations properties cannot be set. "); }
get{
if(_Collections == null) {
var nodeData = this.__NodeDataShell.NodeData;
var store = this.__NodeDataShell.Store;
var relations = nodeData.Relations;
_Collections = new ();
if(relations.TryGetManyRelation(g0f7523f9ceac32d184693ee204c91c05, out var vg0f7523f9ceac32d184693ee204c91c05)){
_Collections.Initialize(store, nodeData.Id, g0f7523f9ceac32d184693ee204c91c05, (Relatude.DB.Datamodels.NodeDataWithRelations[])vg0f7523f9ceac32d184693ee204c91c05);

}else{
_Collections.Initialize(store, nodeData.Id, g0f7523f9ceac32d184693ee204c91c05, null);

}
 }
return _Collections;
 }
 }
 }
