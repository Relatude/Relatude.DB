using System;
using System.Linq;
using System.Collections.Generic;
namespace Relatude.DB.Demo.Models;
public sealed class __IDemoParagraph :Relatude.DB.Demo.Models.IDemoParagraph ,Relatude.DB.Datamodels.INodeShellAccess {
[System.Text.Json.Serialization.JsonIgnore]
public Relatude.DB.Datamodels.NodeDataShell __NodeDataShell { get; }
public __IDemoParagraph(Relatude.DB.Datamodels.NodeDataShell shell){    this.__NodeDataShell = shell;
}
public Guid Id{ 
get { return __NodeDataShell.NodeData.Id; } 
set { __NodeDataShell.NodeData.Id = value; } 
 }
static Guid g64b40d418563c51ecbf25956a0eddc25 = Guid.Parse("64b40d41-8563-c51e-cbf2-5956a0eddc25");
static Guid g08baf457a5343b364a3cfe55f71378d3 = Guid.Parse("08baf457-a534-3b36-4a3c-fe55f71378d3");
static Guid g4f75758e6bfc96eeb2334cca03f3cd64 = Guid.Parse("4f75758e-6bfc-96ee-b233-4cca03f3cd64");
static Guid gb835577e84a24fa3a85044ab2112e6cf = Guid.Parse("b835577e-84a2-4fa3-a850-44ab2112e6cf");
static Guid g1e282f9f3bd24230abcbf9e840145159 = Guid.Parse("1e282f9f-3bd2-4230-abcb-f9e840145159");
static Guid g57c752bfe36443e19163d8ffea004bad = Guid.Parse("57c752bf-e364-43e1-9163-d8ffea004bad");
static Guid gcf885adf112141d885e670c553345dd0 = Guid.Parse("cf885adf-1121-41d8-85e6-70c553345dd0");
static Guid gc1ea2c8adbe84fa0a020ae05507305b6 = Guid.Parse("c1ea2c8a-dbe8-4fa0-a020-ae05507305b6");
public Relatude.DB.Common.FileValue File{ 
get { return __NodeDataShell.GetValue<Relatude.DB.Common.FileValue>(g08baf457a5343b364a3cfe55f71378d3); } 
set { __NodeDataShell.SetValue(g08baf457a5343b364a3cfe55f71378d3,value); } 
 }
public string Code{ 
get { return __NodeDataShell.GetValue<string>(g4f75758e6bfc96eeb2334cca03f3cd64); } 
set { __NodeDataShell.SetValue(g4f75758e6bfc96eeb2334cca03f3cd64,value); } 
 }
 }
