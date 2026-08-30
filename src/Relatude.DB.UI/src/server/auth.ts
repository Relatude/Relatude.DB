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
