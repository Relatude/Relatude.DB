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

// A navigation, not a fetch: the server registers the sign-in with the license server and sends
// the browser there; it comes back to the callback next to this url, and from there to the UI root.
export const licenseLoginStartUrl = `${base}/license-login/start/`;
