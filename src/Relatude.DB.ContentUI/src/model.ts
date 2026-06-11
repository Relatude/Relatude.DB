import { api } from "./api";
import type { ModelDto, NodeTypeDto, PropertyDto } from "./types";

// The datamodel is loaded once at startup and used everywhere to drive the UI.

export class Model {
    private byId = new Map<string, NodeTypeDto>();
    types: NodeTypeDto[] = [];

    async load(): Promise<void> {
        const dto: ModelDto = await api.model();
        this.types = dto.types;
        this.byId = new Map(dto.types.map(t => [t.id, t]));
    }

    type(id: string): NodeTypeDto | undefined {
        return this.byId.get(id);
    }

    typeName(id: string | null): string {
        return (id && this.byId.get(id)?.name) || "?";
    }

    /** node types that can be created and listed in the sidebar */
    get contentTypes(): NodeTypeDto[] {
        return this.types.filter(t => t.instantiable);
    }

    editableProperties(typeId: string): PropertyDto[] {
        return this.type(typeId)?.properties.filter(p => !p.isRelation) ?? [];
    }
}

export const model = new Model();
