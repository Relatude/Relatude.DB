import { BladeManager } from "./blades";
import { clear, el, toast } from "./dom";
import { listBlade } from "./listBlade";
import { model } from "./model";
import { searchBlade } from "./searchBlade";
import "./styles.css";

async function start(): Promise<void> {
    const app = document.getElementById("app")!;
    clear(app);

    try {
        await model.load();
    } catch (error) {
        app.append(el("div", { class: "boot-error" },
            el("h1", {}, "Relatude.DB Content Studio"),
            el("p", {}, "Could not load the datamodel from the API: " + (error instanceof Error ? error.message : error)),
            el("p", {}, "Is Relatude.DB.ContentApi running?"),
        ));
        return;
    }

    const bladesHost = el("main", { class: "blades" });
    const blades = new BladeManager(bladesHost);

    // sidebar with all content types
    const typeList = el("nav", { class: "type-list" });
    let activeButton: HTMLElement | null = null;
    for (const type of model.contentTypes) {
        const button = el("button", {
            class: "type-item",
            onclick: () => {
                activeButton?.classList.remove("active");
                activeButton = button;
                button.classList.add("active");
                blades.openRoot(listBlade(type.id));
            },
        }, type.name);
        typeList.append(button);
    }

    // top bar with global search
    const searchInput = el("input", {
        class: "input global-search",
        type: "search",
        placeholder: "Search all content…  (Enter)",
        onkeydown: (e: Event) => {
            const event = e as KeyboardEvent;
            if (event.key !== "Enter") return;
            const query = (event.target as HTMLInputElement).value.trim();
            if (!query) return;
            activeButton?.classList.remove("active");
            activeButton = null;
            blades.openRoot(searchBlade(query));
        },
    });

    app.append(
        el("div", { class: "shell" },
            el("aside", { class: "sidebar" },
                el("div", { class: "brand" },
                    el("span", { class: "brand-logo" }, "🗂️"),
                    el("span", {}, "Relatude.DB", el("br"), el("strong", {}, "Content Studio")),
                ),
                el("h3", { class: "sidebar-heading" }, "Content types"),
                typeList,
            ),
            el("div", { class: "workspace" },
                el("header", { class: "topbar" }, searchInput),
                bladesHost,
            ),
        ),
    );

    // open the first type by default
    const firstButton = typeList.querySelector("button");
    if (firstButton instanceof HTMLElement) firstButton.click();
    else toast("The datamodel has no instantiable node types.", true);
}

void start();
