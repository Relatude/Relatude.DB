import { api } from "./api";
import type { Blade, BladeContext } from "./blades";
import { clear, debounce, el, toast } from "./dom";
import { model } from "./model";
import type { NodeDto, NodeSummaryDto, PropertyDto, RelationValueDto } from "./types";

export interface EditorTarget {
    nodeId?: string;       // edit an existing node
    newOfTypeId?: string;  // or create a new node of this type
}

// Editor blade: a generated form for any node, plus its relation properties.
// Following a relation opens the related node in a new blade to the right.
export function editorBlade(target: EditorTarget): Blade {
    let body: HTMLElement;
    let context: BladeContext;
    let node: NodeDto | null = null;
    const typeId = target.newOfTypeId;
    const isNew = !!typeId;
    const type = isNew ? model.type(typeId!) : undefined;
    const fieldReaders = new Map<string, () => unknown>();

    async function load(): Promise<void> {
        clear(body);
        fieldReaders.clear();
        if (!isNew) {
            try {
                node = await api.getNode(target.nodeId!);
            } catch (error) {
                body.append(el("p", { class: "empty" }, "Could not load node: " + (error instanceof Error ? error.message : error)));
                return;
            }
            updateHeader();
        }
        renderForm();
    }

    function updateHeader(): void {
        if (!node) return;
        const header = body.parentElement?.querySelector(".blade-titles");
        if (!header) return;
        const title = header.querySelector("h2");
        if (title) title.textContent = node.displayName || node.typeName;
        let subtitle = header.querySelector("p");
        if (!subtitle) {
            subtitle = el("p", {});
            header.append(subtitle);
        }
        subtitle.textContent = node.typeName;
    }

    function properties(): PropertyDto[] {
        const t = isNew ? type : model.type(node!.typeId);
        return t?.properties ?? [];
    }

    function renderForm(): void {
        const form = el("div", { class: "editor-form" });

        for (const property of properties().filter(p => !p.isRelation)) {
            form.append(renderValueField(property));
        }

        const actions = el("div", { class: "editor-actions" },
            el("button", { class: "btn primary", onclick: () => void save() }, isNew ? "Create" : "Save"),
            !isNew ? el("button", { class: "btn danger", onclick: () => void remove() }, "Delete") : null,
        );
        form.append(actions);
        body.append(form);

        if (!isNew && node) {
            const relationsHost = el("div", { class: "relations" }, el("h3", {}, "Relations"));
            for (const relation of node.relations) {
                relationsHost.append(renderRelation(relation));
            }
            body.append(relationsHost);
            body.append(el("p", { class: "meta-line" },
                `Created ${formatDate(node.createdUtc)} · changed ${formatDate(node.changedUtc)}`));
        } else if (isNew) {
            body.append(el("p", { class: "meta-line" }, "Relations can be linked after the node is created."));
        }
    }

    // ---- value fields ----------------------------------------------------

    function renderValueField(property: PropertyDto): HTMLElement {
        const value = node?.values[property.name];
        const field = el("label", { class: "field" },
            el("span", { class: "field-label" }, property.name + (property.isDisplayName ? " ★" : "")),
        );
        if (!property.editable) {
            field.append(el("span", { class: "field-readonly" }, value === null || value === undefined ? "—" : String(value)));
            return field;
        }
        const input = createInput(property, value);
        field.append(input.element);
        fieldReaders.set(property.name, input.read);
        return field;
    }

    interface FieldInput { element: HTMLElement; read(): unknown; }

    function createInput(property: PropertyDto, value: unknown): FieldInput {
        switch (property.propertyType) {
            case "Boolean": {
                const input = el("input", { type: "checkbox", class: "checkbox" });
                input.checked = value === true;
                return { element: input, read: () => input.checked };
            }
            case "Integer":
            case "Long": {
                const input = el("input", { type: "number", step: "1", class: "input" });
                if (value !== null && value !== undefined) input.value = String(value);
                return { element: input, read: () => input.value === "" ? null : Math.trunc(Number(input.value)) };
            }
            case "Double":
            case "Float":
            case "Decimal": {
                const input = el("input", { type: "number", step: "any", class: "input" });
                if (value !== null && value !== undefined) input.value = String(value);
                return { element: input, read: () => input.value === "" ? null : Number(input.value) };
            }
            case "DateTime":
            case "DateTimeOffset": {
                const input = el("input", { type: "datetime-local", class: "input" });
                if (typeof value === "string" && value) input.value = isoToLocal(value);
                return {
                    element: input,
                    read: () => input.value === "" ? null : new Date(input.value).toISOString(),
                };
            }
            case "StringArray": {
                const textarea = el("textarea", { class: "input", rows: "3", placeholder: "One value per line" });
                if (Array.isArray(value)) textarea.value = value.join("\n");
                return {
                    element: textarea,
                    read: () => textarea.value.split("\n").map(v => v.trim()).filter(v => v.length > 0),
                };
            }
            default: { // String, Guid, TimeSpan and anything string-like
                const text = typeof value === "string" ? value : value === null || value === undefined ? "" : String(value);
                const isLong = property.propertyType === "String" && (text.length > 80 || text.includes("\n"));
                if (isLong) {
                    const textarea = el("textarea", { class: "input", rows: "6" });
                    textarea.value = text;
                    return { element: textarea, read: () => textarea.value };
                }
                const input = el("input", { type: "text", class: "input" });
                input.value = text;
                if (property.constraints?.maxLength) input.maxLength = property.constraints.maxLength;
                return { element: input, read: () => input.value };
            }
        }
    }

    // ---- relations ---------------------------------------------------------

    function renderRelation(relation: RelationValueDto): HTMLElement {
        const host = el("div", { class: "relation" });
        const relatedTypeName = model.typeName(relation.relatedTypeId);
        host.append(el("div", { class: "relation-header" },
            el("span", { class: "relation-name" }, relation.name),
            el("span", { class: "relation-type" }, (relation.isMany ? "many " : "one ") + relatedTypeName),
            el("button", { class: "btn small", onclick: () => togglePicker(host, relation) }, "Edit"),
        ));
        const chips = el("div", { class: "chips" });
        for (const item of relation.items) {
            chips.append(el("button", {
                class: "chip",
                title: `Open ${item.displayName}`,
                onclick: () => context.openChild(editorBlade({ nodeId: item.id })),
            }, item.displayName, el("span", { class: "chip-type" }, item.typeName)));
        }
        if (relation.items.length === 0) chips.append(el("span", { class: "empty" }, "—"));
        if (relation.totalCount > relation.items.length) {
            chips.append(el("span", { class: "empty" }, `… ${relation.totalCount - relation.items.length} more`));
        }
        host.append(chips);
        return host;
    }

    function togglePicker(host: HTMLElement, relation: RelationValueDto): void {
        const existing = host.querySelector(".picker");
        if (existing) { existing.remove(); return; }
        if (!relation.relatedTypeId) return;
        const selected = new Set(relation.items.map(i => i.id));
        const results = el("div", { class: "picker-results" });

        async function loadCandidates(search: string): Promise<void> {
            const list = await api.listNodes(relation.relatedTypeId!, { search, pageSize: 50 });
            clear(results);
            for (const candidate of list.items) {
                results.append(renderCandidate(candidate));
            }
            if (list.items.length === 0) results.append(el("p", { class: "empty" }, "No candidates found."));
        }

        function renderCandidate(candidate: NodeSummaryDto): HTMLElement {
            const row = el("button", {
                class: "picker-row" + (selected.has(candidate.id) ? " selected" : ""),
                onclick: () => {
                    if (relation.isMany) {
                        selected.has(candidate.id) ? selected.delete(candidate.id) : selected.add(candidate.id);
                    } else {
                        const wasSelected = selected.has(candidate.id);
                        selected.clear();
                        if (!wasSelected) selected.add(candidate.id);
                        results.querySelectorAll(".picker-row.selected").forEach(r => r.classList.remove("selected"));
                    }
                    row.classList.toggle("selected", selected.has(candidate.id));
                },
            }, candidate.displayName);
            return row;
        }

        const picker = el("div", { class: "picker" },
            el("input", {
                class: "input", type: "search", placeholder: `Search ${model.typeName(relation.relatedTypeId)}…`,
                oninput: debounce((e: Event) => void loadCandidates((e.target as HTMLInputElement).value), 250) as unknown as EventListener,
            }),
            results,
            el("div", { class: "picker-actions" },
                el("button", {
                    class: "btn primary small",
                    onclick: async () => {
                        try {
                            node = await api.setRelation(node!.id, relation.propertyId, [...selected]);
                            toast("Relation updated");
                            clear(body);
                            fieldReaders.clear();
                            renderForm();
                            context.refreshLeft();
                        } catch (error) {
                            toast(String(error instanceof Error ? error.message : error), true);
                        }
                    },
                }, "Apply"),
                el("button", { class: "btn small", onclick: () => picker.remove() }, "Cancel"),
            ),
        );
        host.append(picker);
        void loadCandidates("");
    }

    // ---- actions -----------------------------------------------------------

    async function save(): Promise<void> {
        const values: Record<string, unknown> = {};
        for (const [name, read] of fieldReaders) {
            const value = read();
            if (value !== null) values[name] = value;
        }
        try {
            if (isNew) {
                const created = await api.createNode(typeId!, values);
                toast(`${created.typeName} created`);
                context.refreshLeft();
                context.replaceSelf(editorBlade({ nodeId: created.id }));
            } else {
                node = await api.updateNode(node!.id, values);
                toast("Saved");
                updateHeader();
                context.refreshLeft();
                clear(body);
                fieldReaders.clear();
                renderForm();
            }
        } catch (error) {
            toast(String(error instanceof Error ? error.message : error), true);
        }
    }

    async function remove(): Promise<void> {
        if (!node) return;
        if (!window.confirm(`Delete "${node.displayName}"? This cannot be undone.`)) return;
        try {
            await api.deleteNode(node.id);
            toast("Deleted");
            context.refreshLeft();
            context.closeSelf();
        } catch (error) {
            toast(String(error instanceof Error ? error.message : error), true);
        }
    }

    return {
        title: isNew ? `New ${type?.name ?? ""}` : "Edit",
        width: 520,
        refresh: () => void load(),
        render(bodyElement, bladeContext) {
            body = bodyElement;
            context = bladeContext;
            void load();
        },
    };
}

function isoToLocal(iso: string): string {
    const date = new Date(iso);
    if (isNaN(date.getTime())) return "";
    const pad = (n: number) => String(n).padStart(2, "0");
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

function formatDate(iso: string): string {
    const date = new Date(iso);
    return isNaN(date.getTime()) ? iso : date.toLocaleString();
}
