// Where the admin API lives. The server maps the whole admin UI on one configurable root
// (RelatudeDBServer.ApiUrlRoot, "/relatude.db" unless DBAdminUIUrlPath says otherwise) and
// serves this page at exactly that url, so the page's own path is the root - no need for the
// server to inject it. Under the vite dev server the page is at "/" instead, and the configured
// proxy forwards the default root to the .NET host, so that is the fallback.
function resolveBase(): string {
  const path = window.location.pathname.replace(/\/+$/, "");
  return path.length > 0 ? path : "/relatude.db";
}

/** The admin root, without a trailing slash, e.g. "/relatude.db". */
export const adminBase = resolveBase();

/** The unauthenticated part of the admin API (login, session checks, the UI's own files). */
export const publicBase = adminBase + "/auth";
