import { api } from "./api";
import type { Blade, BladeContext } from "./blades";
import { clear, debounce, el, toast } from "./dom";
import { editorBlade } from "./editorBlade";
import { model } from "./model";
import type { NodeListDto } from "./types";

const PAGE_SIZE = 30;

// Collection blade: searchable, paged list of all nodes of one type.
export function listBlade(typeId: string): Blade {
    const type = model.type(typeId);
    let search = "";
    let page = 0;
    let body: HTMLElement;
    let context: BladeContext;
    let selectedId: string | null = null;

    async function load(): Promise<void> {
        let result: NodeListDto;
        try {
            result = await api.listNodes(typeId, { search, page, pageSize: PAGE_SIZE });
        } catch (error) {
            toast(String(error instanceof Error ? error.message : error), true);
            return;
        }
        const list = el("div", { class: "node-list" });
        for (const item of result.items) {
            const row = el("button", {
                class: "node-row" + (item.id === selectedId ? " selected" : ""),
                onclick: () => {
                    selectedId = item.id;
                    list.querySelectorAll(".node-row.selected").forEach(r => r.classList.remove("selected"));
                    row.classList.add("selected");
                    context.openChild(editorBlade({ nodeId: item.id }));
                },
            },
                el("span", { class: "node-row-name" }, item.displayName),
                item.typeName !== type?.name ? el("span", { class: "badge" }, item.typeName) : null,
            );
            list.append(row);
        }
        if (result.items.length === 0) {
            list.append(el("p", { class: "empty" }, search ? "No results for this search." : "No content yet. Create the first one!"));
        }

        const pageCount = Math.max(1, Math.ceil(result.totalCount / PAGE_SIZE));
        const pager = el("div", { class: "pager" },
            el("button", { class: "btn small", disabled: page <= 0, onclick: () => { page--; void load(); } }, "‹ Prev"),
            el("span", {}, `${result.totalCount} item${result.totalCount === 1 ? "" : "s"} · page ${page + 1}/${pageCount}`),
            el("button", { class: "btn small", disabled: page >= pageCount - 1, onclick: () => { page++; void load(); } }, "Next ›"),
        );

        const content = body.querySelector(".list-content") as HTMLElement;
        clear(content);
        content.append(list, pager);
    }

    const onSearch = debounce((value: string) => {
        search = value;
        page = 0;
        void load();
    }, 250);

    return {
        title: type?.name ?? "Unknown type",
        subtitle: type?.fullName,
        width: 360,
        refresh: () => void load(),
        render(bodyElement, bladeContext) {
            body = bodyElement;
            context = bladeContext;
            body.append(
                el("div", { class: "list-toolbar" },
                    el("input", {
                        class: "input",
                        type: "search",
                        placeholder: `Search ${type?.name ?? ""}…`,
                        oninput: (e: Event) => onSearch((e.target as HTMLInputElement).value),
                    }),
                    el("button", {
                        class: "btn primary",
                        onclick: () => context.openChild(editorBlade({ newOfTypeId: typeId })),
                    }, "+ New"),
                ),
                el("div", { class: "list-content" }),
            );
            void load();
        },
    };
}
