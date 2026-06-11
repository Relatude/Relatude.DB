// DTOs mirroring Relatude.DB.ContentApi responses.

export interface ModelDto {
    types: NodeTypeDto[];
}

export interface NodeTypeDto {
    id: string;
    name: string;
    fullName: string;
    instantiable: boolean;
    parents: string[];
    properties: PropertyDto[];
}

export interface PropertyDto {
    id: string;
    name: string;
    propertyType: string;
    editable: boolean;
    isDisplayName: boolean;
    isRelation: boolean;
    isMany: boolean;
    relatedTypeId: string | null;
    constraints: PropertyConstraintsDto | null;
}

export interface PropertyConstraintsDto {
    minLength: number | null;
    maxLength: number | null;
    regularExpression: string | null;
    minValue: number | null;
    maxValue: number | null;
}

export interface NodeSummaryDto {
    id: string;
    displayName: string;
    typeId: string;
    typeName: string;
}

export interface NodeListDto {
    items: NodeSummaryDto[];
    totalCount: number;
    page: number;
    pageSize: number;
}

export interface NodeDto {
    id: string;
    typeId: string;
    typeName: string;
    displayName: string;
    createdUtc: string;
    changedUtc: string;
    values: Record<string, unknown>;
    relations: RelationValueDto[];
}

export interface RelationValueDto {
    propertyId: string;
    name: string;
    isMany: boolean;
    relatedTypeId: string | null;
    items: NodeSummaryDto[];
    totalCount: number;
}

export interface SearchHitDto {
    node: NodeSummaryDto;
    score: number;
    sample: string;
}

export interface SearchResultDto {
    hits: SearchHitDto[];
    totalCount: number;
    durationMs: number;
}
