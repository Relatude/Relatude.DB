import { useState } from "react";
import { IconAlertTriangle, IconChevronDown, IconChevronRight } from "@tabler/icons-react";
import { openInSettings } from "../navigate";
import type { SecretInFile, SecretsElsewhere, SettingsPage } from "../server/settings";
import { CopyText } from "./CopyText";

type Form = "user-secrets" | "environment";

/**
 * The warning at the top of both settings pages while relatude.db.json holds secrets - API keys, the
 * master password, storage connection strings, signing keys. That file is SHARED: part of the
 * application, usually committed to source control and deployed with it, so a secret in it is readable
 * by whoever can read the repository.
 *
 * It lists this page's secrets, each a link to its field, and names the other pages the file holds
 * secrets for. "How to move them" gives the configuration keys that would carry them instead, as user
 * secrets or as environment variables, with the ids that tie a value to its list element. A secret whose
 * value in force already comes from somewhere else says so: deleting the file's copy is all it needs.
 *
 * Nothing on the page can fix this: the admin UI never edits relatude.db.json, which is the developer's
 * file. So the warning stays until the file no longer holds them; the server reads the file afresh for
 * every page, so a reload after editing it is enough.
 */
export function SecretsWarning({ page }: { page: SettingsPage }) {
  const [showHow, setShowHow] = useState(false);
  const [form, setForm] = useState<Form>("user-secrets");
  const secrets = page.secretsInFile;
  if (!secrets) return null;
  const { items, elsewhere, file, configSection } = secrets;
  const toMove = items.filter((i) => !i.unused && i.configKey);
  const count = items.length;

  function show(item: SecretInFile): void {
    openInSettings({ scope: page.scope, storeId: page.storeId ?? null, sectionId: item.sectionId, groupId: item.groupId, path: item.path });
  }
  function showElsewhere(other: SecretsElsewhere): void {
    openInSettings({ scope: other.scope, storeId: other.storeId ?? null, sectionId: other.sectionId, groupId: other.groupId, path: other.path });
  }

  return (
    <div className="secrets-warning" role="alert">
      <IconAlertTriangle size={18} stroke={1.8} />
      <div className="secrets-warning-body">
        <div className="secrets-warning-head">
          {count > 0 ? `${count === 1 ? "A secret is" : `${count} secrets are`} stored in ${file}` : `${file} holds secrets of other pages`}
        </div>
        <p>
          That file is SHARED: part of the application, usually committed to source control and deployed with it, so whoever can read the repository can
          read {count === 1 ? "it" : "them"}.{" "}
          {configSection
            ? `Keep secrets in configuration instead — user secrets on a developer's machine, environment variables or a vault on a server — under the ${configSection} section, and delete them from the file.`
            : "This server reads no configuration section (ServerOptions.ConfigurationSectionName is null), so set them from the application's code in OnServerSettingsInit or OnContainerSettingsInit instead, or turn the section on, and delete them from the file."}
        </p>
        {count > 0 && (
          <ul className="secrets-list">
            {items.map((item) => (
              <li key={item.path}>
                <button className="secrets-link" onClick={() => show(item)} title="Show the setting">
                  {item.label}
                </button>
                <span className="muted"> · {item.where}</span>
                {item.unused && <span className="secrets-unused"> · not used ({item.unused}): only delete it from the file</span>}
              </li>
            ))}
          </ul>
        )}
        {elsewhere.length > 0 && (
          <div className="secrets-elsewhere">
            {count > 0 ? "Also in this file: " : "In this file: "}
            {elsewhere.map((other, index) => (
              <span key={other.storeId ?? "server"}>
                {index > 0 && (index === elsewhere.length - 1 ? " and " : ", ")}
                {other.count} {other.count === 1 ? "secret" : "secrets"} of{" "}
                {other.canOpen ? (
                  <button className="secrets-link" onClick={() => showElsewhere(other)} title={other.scope === "server" ? "Open the server settings" : "Open that database's settings"}>
                    {other.title}
                  </button>
                ) : (
                  <span title="This server does not have that database">{other.title}</span>
                )}
              </span>
            ))}
            .
          </div>
        )}
        {toMove.length > 0 && (
          <>
            <button className="secrets-how-toggle" onClick={() => setShowHow(!showHow)} aria-expanded={showHow}>
              {showHow ? <IconChevronDown size={14} stroke={1.8} /> : <IconChevronRight size={14} stroke={1.8} />}
              How to move {toMove.length === 1 ? "it" : "them"}
            </button>
            {showHow && <HowToMove items={toMove} form={form} onForm={setForm} file={file} />}
          </>
        )}
      </div>
    </div>
  );
}

function HowToMove({ items, form, onForm, file }: { items: SecretInFile[]; form: Form; onForm: (form: Form) => void; file: string }) {
  const text = configLines(items, form).join("\n");
  const ids = items.some((i) => i.configIds.length > 0);
  return (
    <div className="secrets-how">
      <div className="secrets-how-bar">
        <div className="module-switch compact">
          <button className={form === "user-secrets" ? "active" : ""} onClick={() => onForm("user-secrets")}>
            User secrets
          </button>
          <button className={form === "environment" ? "active" : ""} onClick={() => onForm("environment")}>
            Environment variables
          </button>
        </div>
        <span className="muted">
          {form === "user-secrets"
            ? "A developer's machine: run in the project folder (dotnet user-secrets init first, if the project has no UserSecretsId)."
            : "A server: the environment, or the application settings of an Azure App Service."}
        </span>
      </div>
      <div className="secrets-how-code">
        <pre className="secrets-how-lines">{text}</pre>
        <CopyText text={text} title="Copy these lines" small />
      </div>
      <ol className="secrets-how-steps">
        <li>Set the keys above, putting each secret's value where its name is in angle brackets.</li>
        <li>Delete the values from {file}.</li>
        <li>Restart the server, so the values are read from configuration from now on.</li>
      </ol>
      {ids && (
        <div className="muted secrets-how-note">
          The numbers are positions in the file's lists. The Id keys tie each value to its element by id, so reordering the file never sends a secret to the
          wrong database or IO provider.
        </div>
      )}
    </div>
  );
}

/**
 * The configuration lines that carry these secrets: each value's key, after the ids of the list elements
 * it is in - once each, though several secrets of one database share them.
 */
function configLines(items: SecretInFile[], form: Form): string[] {
  const lines: string[] = [];
  const seen = new Set<string>();
  const line = (key: string, value: string) =>
    form === "user-secrets" ? `dotnet user-secrets set "${key}" "${value}"` : `${key.split(":").join("__")}=${value}`;
  for (const item of items) {
    for (const id of item.configIds) {
      if (seen.has(id.key)) continue;
      seen.add(id.key);
      lines.push(line(id.key, id.value));
    }
    lines.push(line(item.configKey!, `<${item.label}>`));
  }
  return lines;
}
