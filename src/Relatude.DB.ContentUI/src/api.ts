import type { ModelDto, NodeDto, NodeListDto, SearchResultDto } from "./types";

export class ApiError extends Error { }

async function request<T>(url: string, init?: RequestInit): Promise<T> {
    const response = await fetch("/api" + url, {
        headers: { "Content-Type": "application/json" },
        ...init,
    });
    if (!response.ok) {
        let message = `${response.status} ${response.statusText}`;
        try {
            const body = await response.json();
            if (body?.error) message = body.error;
        } catch { /* not json */ }
        throw new ApiError(message);
    }
    if (response.status === 204) return undefined as T;
    return await response.json() as T;
}

export const api = {
    model: () =>
        request<ModelDto>("/model"),

    listNodes: (typeId: string, options: { search?: string; page?: number; pageSize?: number } = {}) => {
        const params = new URLSearchParams();
        if (options.search) params.set("search", options.search);
        params.set("page", String(options.page ?? 0));
        params.set("pageSize", String(options.pageSize ?? 30));
        return request<NodeListDto>(`/types/${typeId}/nodes?${params}`);
    },

    getNode: (id: string) =>
        request<NodeDto>(`/nodes/${id}`),

    createNode: (typeId: string, values: Record<string, unknown>) =>
        request<NodeDto>("/nodes", { method: "POST", body: JSON.stringify({ typeId, values }) }),

    updateNode: (id: string, values: Record<string, unknown>) =>
        request<NodeDto>(`/nodes/${id}`, { method: "PUT", body: JSON.stringify({ values }) }),

    deleteNode: (id: string) =>
        request<void>(`/nodes/${id}`, { method: "DELETE" }),

    setRelation: (id: string, propertyId: string, ids: string[]) =>
        request<NodeDto>(`/nodes/${id}/relations/${propertyId}`, { method: "PUT", body: JSON.stringify({ ids }) }),

    search: (q: string, take = 25) =>
        request<SearchResultDto>(`/search?q=${encodeURIComponent(q)}&take=${take}`),
};
