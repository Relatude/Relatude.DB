// The public authentication endpoints ({ApiUrlRoot}/auth/...) — the only part of the
// admin API reachable without a login cookie. Everything else goes over the channel.
import { publicBase } from "./base";

const base = publicBase;

async function post<T>(action: string, body?: unknown): Promise<T> {
  const response = await postRaw(action, body);
  return response.json() as Promise<T>;
}

async function postRaw(action: string, body?: unknown): Promise<Response> {
  const response = await fetch(`${base}/${action}/`, {
    method: "POST",
    headers: body !== undefined ? { "content-type": "application/json" } : undefined,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  if (!response.ok) throw new Error(`${action} failed (HTTP ${response.status}).`);
  return response;
}

export function isLoggedIn(): Promise<boolean> {
  return post<boolean>("is-logged-in");
}

// false when the server has no master user configured, so logging in is impossible
export function haveUsers(): Promise<boolean> {
  return post<boolean>("have-users");
}

// Whether the master account can be used from here: set up at all (a user name and a password), and
// - from anywhere but the server itself - allowed remotely (AllowMasterLoginOutsideLocalhost). When
// it cannot, the login page leaves its whole form out.
export interface MasterLoginOptions {
  available: boolean;
  /** why not: no master user or password set, or master login refused from outside the server */
  reason: "no-user" | "remote" | null;
}

export async function masterLoginOptions(): Promise<MasterLoginOptions> {
  try {
    return await post<MasterLoginOptions>("master-login-options");
  } catch {
    // a server from before the endpoint: all it can say is whether a master user is set up
    const configured = await haveUsers();
    return { available: configured, reason: configured ? null : "no-user" };
  }
}

export async function login(userName: string, password: string, remember: boolean): Promise<boolean> {
  const result = await post<{ success: boolean }>("login", { userName, password, remember });
  return result.success;
}

export async function logout(): Promise<void> {
  await postRaw("logout"); // empty response body
}

// Sign in with Relatude.License (see LicenseLogin.cs on the server). Whether the login page should
// offer it: the server has the license and API keys and AllowLicenseeAdminLogin is on.
export interface LicenseLoginOptions {
  available: boolean;
  serverUrl: string | null;
}

export function licenseLoginOptions(): Promise<LicenseLoginOptions> {
  return post<LicenseLoginOptions>("license-login-options");
}

// Starts the sign-in: tells the server the address this page is open on, which it checks against its
// own public addresses, keeps a ticket for in memory and in a cookie, and registers with the license
// server. The answer is where to send the browser - or, with loginUrl null, why not. The browser
// comes back to the callback next to this url, and from there to the UI root.
export interface LicenseLoginBegin {
  loginUrl: string | null;
  error: string | null;
}

export function beginLicenseLogin(colours: Record<string, string>): Promise<LicenseLoginBegin> {
  return post<LicenseLoginBegin>("license-login/begin", { url: window.location.href, ...colours });
}
