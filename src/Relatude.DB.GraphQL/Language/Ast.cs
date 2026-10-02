namespace Relatude.DB.GraphQL.Language;

/// <summary>Base of all syntax nodes. <see cref="Position"/> is the index of the node's first character in the source.</summary>
public abstract class AstNode {
    public int Position { get; init; }
}

public sealed class Document : AstNode {
    public required string Source { get; init; }
    public List<Definition> Definitions { get; } = [];

    /// <summary>1-based line and column of a source position.</summary>
    public (int Line, int Column) LineColumn(int position) {
        var line = 1;
        var lineStart = 0;
        var end = Math.Min(position, Source.Length);
        for (var i = 0; i < end; i++) {
            var c = Source[i];
            if (c == '\n' || (c == '\r' && (i + 1 >= Source.Length || Source[i + 1] != '\n'))) {
                line++;
                lineStart = i + 1;
            } else if (c == '\r') {
                // the "\r\n" pair counts once, at the '\n'
            }
        }
        return (line, end - lineStart + 1);
    }
}

public abstract class Definition : AstNode { }

public enum OperationKind { Query, Mutation, Subscription }

public sealed class OperationDefinition : Definition {
    public OperationKind Operation { get; init; }
    public string? Name { get; init; }
    public List<VariableDefinition> Variables { get; } = [];
    public List<Directive> Directives { get; } = [];
    public required SelectionSet SelectionSet { get; init; }
}

public sealed class FragmentDefinition : Definition {
    public required string Name { get; init; }
    public required string TypeCondition { get; init; }
    public List<Directive> Directives { get; } = [];
    public required SelectionSet SelectionSet { get; init; }
}

public sealed class VariableDefinition : AstNode {
    public required string Name { get; init; }
    public required TypeNode Type { get; init; }
    public ValueNode? DefaultValue { get; init; }
    public List<Directive> Directives { get; } = [];
}

public sealed class SelectionSet : AstNode {
    public List<Selection> Selections { get; } = [];
}

public abstract class Selection : AstNode {
    public List<Directive> Directives { get; } = [];
}

public sealed class FieldNode : Selection {
    public string? Alias { get; init; }
    public required string Name { get; init; }
    public List<Argument> Arguments { get; } = [];
    public SelectionSet? SelectionSet { get; set; }
    /// <summary>The response key: the alias when given, else the field name.</summary>
    public string ResponseKey => Alias ?? Name;
}

public sealed class FragmentSpread : Selection {
    public required string Name { get; init; }
}

public sealed class InlineFragment : Selection {
    public string? TypeCondition { get; init; }
    public required SelectionSet SelectionSet { get; init; }
}

public sealed class Argument : AstNode {
    public required string Name { get; init; }
    public required ValueNode Value { get; init; }
}

public sealed class Directive : AstNode {
    public required string Name { get; init; }
    public List<Argument> Arguments { get; } = [];
}

public abstract class TypeNode : AstNode { }
public sealed class NamedTypeNode : TypeNode { public required string Name { get; init; } }
public sealed class ListTypeNode : TypeNode { public required TypeNode Type { get; init; } }
public sealed class NonNullTypeNode : TypeNode { public required TypeNode Type { get; init; } }

public abstract class ValueNode : AstNode { }
public sealed class VariableValue : ValueNode { public required string Name { get; init; } }
public sealed class IntValue : ValueNode { public required string Text { get; init; } }
public sealed class FloatValue : ValueNode { public required string Text { get; init; } }
public sealed class StringValue : ValueNode { public required string Value { get; init; } }
public sealed class BooleanValue : ValueNode { public bool Value { get; init; } }
public sealed class NullValue : ValueNode { }
public sealed class EnumValue : ValueNode { public required string Name { get; init; } }
public sealed class ListValue : ValueNode { public List<ValueNode> Values { get; } = []; }
public sealed class ObjectValue : ValueNode { public List<ObjectField> Fields { get; } = []; }
public sealed class ObjectField : AstNode {
    public required string Name { get; init; }
    public required ValueNode Value { get; init; }
}
