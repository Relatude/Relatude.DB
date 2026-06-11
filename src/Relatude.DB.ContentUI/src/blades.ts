import { clear, el } from "./dom";

// A blade is one vertical panel in the horizontal stack. Opening a blade from
// blade N first closes every blade to the right of N - Azure portal style.

export interface Blade {
    title: string;
    subtitle?: string;
    width: number;
    render(body: HTMLElement, context: BladeContext): void;
    /** called when content to the right (or this blade itself) changed data */
    refresh?(): void;
}

export interface BladeContext {
    /** open a blade directly to the right of this one */
    openChild(blade: Blade): void;
    /** replace this blade with another one (used when a new node is saved) */
    replaceSelf(blade: Blade): void;
    /** close this blade and everything right of it */
    closeSelf(): void;
    /** ask blades to the left to reload their data (e.g. after save/delete) */
    refreshLeft(): void;
}

interface Mounted {
    blade: Blade;
    element: HTMLElement;
    body: HTMLElement;
}

export class BladeManager {
    private readonly container: HTMLElement;
    private stack: Mounted[] = [];

    constructor(container: HTMLElement) {
        this.container = container;
    }

    /** close everything and open a single root blade */
    openRoot(blade: Blade): void {
        this.truncate(0);
        this.push(blade);
    }

    private push(blade: Blade): void {
        const body = el("div", { class: "blade-body" });
        const index = this.stack.length;
        const element = el("section", { class: "blade", style: `width:${blade.width}px;min-width:${blade.width}px` },
            el("header", { class: "blade-header" },
                el("div", { class: "blade-titles" },
                    el("h2", {}, blade.title),
                    blade.subtitle ? el("p", {}, blade.subtitle) : null,
                ),
                index > 0
                    ? el("button", { class: "icon-btn", title: "Close blade", onclick: () => this.truncate(index) }, "✕")
                    : null,
            ),
            body,
        );
        const mounted: Mounted = { blade, element, body };
        this.stack.push(mounted);
        this.container.append(element);
        blade.render(body, this.contextFor(mounted));
        // bring the new blade into view
        requestAnimationFrame(() => element.scrollIntoView({ behavior: "smooth", inline: "end", block: "nearest" }));
    }

    private contextFor(mounted: Mounted): BladeContext {
        const manager = this;
        return {
            openChild(blade: Blade): void {
                const index = manager.stack.indexOf(mounted);
                if (index < 0) return;
                manager.truncate(index + 1);
                manager.push(blade);
            },
            replaceSelf(blade: Blade): void {
                const index = manager.stack.indexOf(mounted);
                if (index < 0) return;
                manager.truncate(index);
                manager.push(blade);
            },
            closeSelf(): void {
                const index = manager.stack.indexOf(mounted);
                if (index >= 0) manager.truncate(index);
            },
            refreshLeft(): void {
                const index = manager.stack.indexOf(mounted);
                for (let i = 0; i < index; i++) manager.stack[i].blade.refresh?.();
            },
        };
    }

    private truncate(fromIndex: number): void {
        while (this.stack.length > fromIndex) {
            const removed = this.stack.pop()!;
            removed.element.remove();
        }
    }

    rerender(mounted: Mounted): void {
        clear(mounted.body);
        mounted.blade.render(mounted.body, this.contextFor(mounted));
    }
}
