import { api } from "./api";
import type { Blade, BladeContext } from "./blades";
import { clear, el, toast } from "./dom";
import { editorBlade } from "./editorBlade";

// Global search results blade: ranked full text hits across all node types,
// with highlighted text samples. Clicking a hit opens it in an editor blade.
export function searchBlade(query: string): Blade {
    let body: HTMLElement;
    let context: BladeContext;

    async function load(): Promise<void> {
        clear(body);
        body.append(el("p", { class: "empty" }, "Searching…"));
        try {
            const result = await api.search(query);
            clear(body);
            body.append(el("p", { class: "meta-line" },
                `${result.totalCount} hit${result.totalCount === 1 ? "" : "s"} in ${result.durationMs.toFixed(1)} ms`));
            const list = el("div", { class: "node-list" });
            for (const hit of result.hits) {
                const sample = el("span", { class: "search-sample" });
                sample.innerHTML = sanitizeSample(hit.sample);
                list.append(el("button", {
                    class: "node-row search-hit",
                    onclick: () => context.openChild(editorBlade({ nodeId: hit.node.id })),
                },
                    el("span", { class: "node-row-name" },
                        hit.node.displayName,
                        el("span", { class: "badge" }, hit.node.typeName),
                    ),
                    sample,
                ));
            }
            if (result.hits.length === 0) list.append(el("p", { class: "empty" }, "Nothing found."));
            body.append(list);
        } catch (error) {
            clear(body);
            toast(String(error instanceof Error ? error.message : error), true);
        }
    }

    return {
        title: `Search: ${query}`,
        width: 420,
        refresh: () => void load(),
        render(bodyElement, bladeContext) {
            body = bodyElement;
            context = bladeContext;
            void load();
        },
    };
}

// The API highlights matches with <mark> tags; escape everything else.
function sanitizeSample(sample: string): string {
    const escaped = sample
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;");
    return escaped
        .replace(/&lt;mark&gt;/g, "<mark>")
        .replace(/&lt;\/mark&gt;/g, "</mark>");
}
