using System;
using System.Linq;
using System.Collections.Generic;
namespace Relatude.DB.Native.Models;
public sealed class __ISystemUserGroup :Relatude.DB.Native.Models.ISystemUserGroup ,Relatude.DB.Datamodels.INodeShellAccess {
[System.Text.Json.Serialization.JsonIgnore]
public Relatude.DB.Datamodels.NodeDataShell __NodeDataShell { get; }
public __ISystemUserGroup(Relatude.DB.Datamodels.NodeDataShell shell){    this.__NodeDataShell = shell;
}
public Guid Id{ 
get { return __NodeDataShell.NodeData.Id; } 
set { __NodeDataShell.NodeData.Id = value; } 
 }
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
public string GroupName{ 
get { return __NodeDataShell.GetValue<string>(g49fd93845f686aca947292640550e9e2); } 
set { __NodeDataShell.SetValue(g49fd93845f686aca947292640550e9e2,value); } 
 }
Relatude.DB.Native.Models.UsersToGroups.Users _UserMembers = null;
public Relatude.DB.Native.Models.UsersToGroups.Users UserMembers{ 
set{ throw new Exception("Relations properties cannot be set. "); }
get{
if(_UserMembers == null) {
var nodeData = this.__NodeDataShell.NodeData;
var store = this.__NodeDataShell.Store;
var relations = nodeData.Relations;
_UserMembers = new ();
if(relations.TryGetManyRelation(gc845e4b47e1d1991c92225f00eb5a5a2, out var vgc845e4b47e1d1991c92225f00eb5a5a2)){
_UserMembers.Initialize(store, nodeData.Id, gc845e4b47e1d1991c92225f00eb5a5a2, (Relatude.DB.Datamodels.NodeDataWithRelations[])vgc845e4b47e1d1991c92225f00eb5a5a2);

}else{
_UserMembers.Initialize(store, nodeData.Id, gc845e4b47e1d1991c92225f00eb5a5a2, null);

}
 }
return _UserMembers;
 }
 }
Relatude.DB.Native.Models.GroupsToGroups.Memberships _GroupMemberships = null;
public Relatude.DB.Native.Models.GroupsToGroups.Memberships GroupMemberships{ 
set{ throw new Exception("Relations properties cannot be set. "); }
get{
if(_GroupMemberships == null) {
var nodeData = this.__NodeDataShell.NodeData;
var store = this.__NodeDataShell.Store;
var relations = nodeData.Relations;
_GroupMemberships = new ();
if(relations.TryGetManyRelation(g6cf54ce8ed2cc7db15c6406c5a8810e3, out var vg6cf54ce8ed2cc7db15c6406c5a8810e3)){
_GroupMemberships.Initialize(store, nodeData.Id, g6cf54ce8ed2cc7db15c6406c5a8810e3, (Relatude.DB.Datamodels.NodeDataWithRelations[])vg6cf54ce8ed2cc7db15c6406c5a8810e3);

}else{
_GroupMemberships.Initialize(store, nodeData.Id, g6cf54ce8ed2cc7db15c6406c5a8810e3, null);

}
 }
return _GroupMemberships;
 }
 }
Relatude.DB.Native.Models.GroupsToGroups.Members _GroupMembers = null;
public Relatude.DB.Native.Models.GroupsToGroups.Members GroupMembers{ 
set{ throw new Exception("Relations properties cannot be set. "); }
get{
if(_GroupMembers == null) {
var nodeData = this.__NodeDataShell.NodeData;
var store = this.__NodeDataShell.Store;
var relations = nodeData.Relations;
_GroupMembers = new ();
if(relations.TryGetManyRelation(gd5d61b7f9d28c98e4128d476a5e0fa25, out var vgd5d61b7f9d28c98e4128d476a5e0fa25)){
_GroupMembers.Initialize(store, nodeData.Id, gd5d61b7f9d28c98e4128d476a5e0fa25, (Relatude.DB.Datamodels.NodeDataWithRelations[])vgd5d61b7f9d28c98e4128d476a5e0fa25);

}else{
_GroupMembers.Initialize(store, nodeData.Id, gd5d61b7f9d28c98e4128d476a5e0fa25, null);

}
 }
return _GroupMembers;
 }
 }
 }
