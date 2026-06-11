// Tiny DOM helpers - keeps the rest of the code declarative without a framework.

type Child = Node | string | null | undefined;

export function el<K extends keyof HTMLElementTagNameMap>(
    tag: K,
    attrs: Record<string, string | boolean | EventListener | undefined> = {},
    ...children: Child[]
): HTMLElementTagNameMap[K] {
    const element = document.createElement(tag);
    for (const [key, value] of Object.entries(attrs)) {
        if (value === undefined || value === false) continue;
        if (key.startsWith("on") && typeof value === "function") {
            element.addEventListener(key.slice(2).toLowerCase(), value);
        } else if (value === true) {
            element.setAttribute(key, "");
        } else if (typeof value === "string") {
            element.setAttribute(key, value);
        }
    }
    for (const child of children) {
        if (child === null || child === undefined) continue;
        element.append(child);
    }
    return element;
}

export function clear(element: HTMLElement): void {
    while (element.firstChild) element.removeChild(element.firstChild);
}

export function debounce<A extends unknown[]>(fn: (...args: A) => void, ms: number): (...args: A) => void {
    let timer: number | undefined;
    return (...args: A) => {
        window.clearTimeout(timer);
        timer = window.setTimeout(() => fn(...args), ms);
    };
}

let toastTimer: number | undefined;
export function toast(message: string, isError = false): void {
    let host = document.getElementById("toast");
    if (!host) {
        host = el("div", { id: "toast" });
        document.body.append(host);
    }
    host.textContent = message;
    host.className = isError ? "error visible" : "visible";
    window.clearTimeout(toastTimer);
    toastTimer = window.setTimeout(() => host!.classList.remove("visible"), isError ? 5000 : 2500);
}
