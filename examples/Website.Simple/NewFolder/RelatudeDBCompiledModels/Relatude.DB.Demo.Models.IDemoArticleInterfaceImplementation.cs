using System;
using System.Linq;
using System.Collections.Generic;
namespace Relatude.DB.Demo.Models;
public sealed class __IDemoArticle :Relatude.DB.Demo.Models.IDemoArticle ,Relatude.DB.Datamodels.INodeShellAccess {
[System.Text.Json.Serialization.JsonIgnore]
public Relatude.DB.Datamodels.NodeDataShell __NodeDataShell { get; }
public __IDemoArticle(Relatude.DB.Datamodels.NodeDataShell shell){    this.__NodeDataShell = shell;
    this.Meta = new Relatude.DB.Datamodels.NodeMeta(shell.NodeData);
}
public Guid Id{ 
get { return __NodeDataShell.NodeData.Id; } 
set { __NodeDataShell.NodeData.Id = value; } 
 }
public Relatude.DB.Datamodels.NodeMeta Meta{ get; set; }
static Guid g44f9a2d1283d947ec6f744e1c1d15aba = Guid.Parse("44f9a2d1-283d-947e-c6f7-44e1c1d15aba");
static Guid g60822e37ebe21f68bee87a3048f47b9a = Guid.Parse("60822e37-ebe2-1f68-bee8-7a3048f47b9a");
static Guid g9579321523a2cf9b849ba8d3e7991c35 = Guid.Parse("95793215-23a2-cf9b-849b-a8d3e7991c35");
static Guid g560c1b8ea10a7496e9f5a8dadc602d73 = Guid.Parse("560c1b8e-a10a-7496-e9f5-a8dadc602d73");
static Guid gdf67bd653e80dc5c046139e4c7718533 = Guid.Parse("df67bd65-3e80-dc5c-0461-39e4c7718533");
static Guid g8ed295892f504f723e2ba390e1d97d94 = Guid.Parse("8ed29589-2f50-4f72-3e2b-a390e1d97d94");
static Guid g4ff2f5a5975e23912e2d02fd13bc0135 = Guid.Parse("4ff2f5a5-975e-2391-2e2d-02fd13bc0135");
static Guid g866117f60f8596f4de41741b4e495a3e = Guid.Parse("866117f6-0f85-96f4-de41-741b4e495a3e");
static Guid g866117f60f8596f4de41741b4e495a3e_KeyProperty = Guid.Parse("4f75758e-6bfc-96ee-b233-4cca03f3cd64");
static Guid gb835577e84a24fa3a85044ab2112e6cf = Guid.Parse("b835577e-84a2-4fa3-a850-44ab2112e6cf");
static Guid g1e282f9f3bd24230abcbf9e840145159 = Guid.Parse("1e282f9f-3bd2-4230-abcb-f9e840145159");
static Guid g57c752bfe36443e19163d8ffea004bad = Guid.Parse("57c752bf-e364-43e1-9163-d8ffea004bad");
static Guid gcf885adf112141d885e670c553345dd0 = Guid.Parse("cf885adf-1121-41d8-85e6-70c553345dd0");
static Guid gc1ea2c8adbe84fa0a020ae05507305b6 = Guid.Parse("c1ea2c8a-dbe8-4fa0-a020-ae05507305b6");
public string Title{ 
get { return __NodeDataShell.GetValue<string>(g60822e37ebe21f68bee87a3048f47b9a); } 
set { __NodeDataShell.SetValue(g60822e37ebe21f68bee87a3048f47b9a,value); } 
 }
public string Content{ 
get { return __NodeDataShell.GetValue<string>(g9579321523a2cf9b849ba8d3e7991c35); } 
set { __NodeDataShell.SetValue(g9579321523a2cf9b849ba8d3e7991c35,value); } 
 }
public int Size{ 
get { return __NodeDataShell.GetValue<int>(g560c1b8ea10a7496e9f5a8dadc602d73); } 
set { __NodeDataShell.SetValue(g560c1b8ea10a7496e9f5a8dadc602d73,value); } 
 }
public DateTime CreatedAt{ 
get { return __NodeDataShell.GetValue<DateTime>(gdf67bd653e80dc5c046139e4c7718533); } 
set { __NodeDataShell.SetValue(gdf67bd653e80dc5c046139e4c7718533,value); } 
 }
public DateTime UpdatedAt{ 
get { return __NodeDataShell.GetValue<DateTime>(g8ed295892f504f723e2ba390e1d97d94); } 
set { __NodeDataShell.SetValue(g8ed295892f504f723e2ba390e1d97d94,value); } 
 }
public Relatude.DB.Common.FileValue File{ 
get { return __NodeDataShell.GetValue<Relatude.DB.Common.FileValue>(g4ff2f5a5975e23912e2d02fd13bc0135); } 
set { __NodeDataShell.SetValue(g4ff2f5a5975e23912e2d02fd13bc0135,value); } 
 }
public Relatude.DB.Nodes.InnerNodes<string, Relatude.DB.Demo.Models.IDemoParagraph> Paragraphs{ 
get {
 var v= __NodeDataShell.GetValue<Relatude.DB.Nodes.InnerNodes<string, Relatude.DB.Demo.Models.IDemoParagraph>>(g866117f60f8596f4de41741b4e495a3e);
if(v == null) {
v = [];
__NodeDataShell.SetValue(g866117f60f8596f4de41741b4e495a3e, v);
}
return v;
} 
set { __NodeDataShell.SetValue(g866117f60f8596f4de41741b4e495a3e,value); } 
 }
 }
