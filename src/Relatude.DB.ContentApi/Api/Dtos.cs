using System.Text.Json;

namespace Relatude.DB.ContentApi.Api;

// Datamodel metadata exposed to the UI so it can build forms for any node type.
public record ModelDto(List<NodeTypeDto> Types);

public record NodeTypeDto(
    Guid Id,
    string Name,
    string FullName,
    bool Instantiable,
    List<Guid> Parents,
    List<PropertyDto> Properties
);

public record PropertyDto(
    Guid Id,
    string Name,
    string PropertyType,
    bool Editable,
    bool IsDisplayName,
    // relation properties
    bool IsRelation,
    bool IsMany,
    Guid? RelatedTypeId,
    // simple constraints for form validation
    PropertyConstraintsDto? Constraints
);

public record PropertyConstraintsDto(
    int? MinLength,
    int? MaxLength,
    string? RegularExpression,
    double? MinValue,
    double? MaxValue
);

// Lightweight reference to a node, used in lists, relation chips and search results.
public record NodeSummaryDto(
    Guid Id,
    string DisplayName,
    Guid TypeId,
    string TypeName
);

public record NodeListDto(
    List<NodeSummaryDto> Items,
    int TotalCount,
    int Page,
    int PageSize
);

// Full node for the editor blade.
public record NodeDto(
    Guid Id,
    Guid TypeId,
    string TypeName,
    string DisplayName,
    DateTime CreatedUtc,
    DateTime ChangedUtc,
    Dictionary<string, object?> Values,
    List<RelationValueDto> Relations
);

public record RelationValueDto(
    Guid PropertyId,
    string Name,
    bool IsMany,
    Guid? RelatedTypeId,
    List<NodeSummaryDto> Items,
    int TotalCount
);

public record SearchHitDto(
    NodeSummaryDto Node,
    double Score,
    string Sample
);

public record SearchResultDto(
    List<SearchHitDto> Hits,
    int TotalCount,
    double DurationMs
);

// Request payloads.
public record CreateNodeRequest(Guid TypeId, Dictionary<string, JsonElement>? Values);
public record UpdateNodeRequest(Dictionary<string, JsonElement>? Values);
public record SetRelationRequest(List<Guid> Ids);
