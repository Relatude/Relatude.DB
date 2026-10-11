import { useState, type ComponentType } from "react";
import { IconCalendarRepeat, IconChecklist } from "@tabler/icons-react";
import { TasksSection } from "./TasksSection";
import { SchedulerView } from "./SchedulerView";
import { databasePath, SettingsShareButton } from "./OverridesDialog";
import type { DatabaseInfo } from "../server/serverInfo";

/**
 * The database's background work, under one entry in the rail: the queue - what is waiting and
 * running now - and the scheduler, which puts jobs into that queue at set intervals.
 *
 * Like the storage module, each view keeps its own section id, so the global search finds
 * "Scheduler" and opens the page on it, and the rail's entry opens the view that was open last.
 * The scheduler's schedules are settings saved on THIS SERVER, so that view carries the module's
 * Move to shared button at the top right; the queue saves nothing.
 */
export type TasksView = "tasks" | "scheduler";

const views: { id: TasksView; label: string; icon: ComponentType<{ size?: number; stroke?: number }>; hint: string }[] = [
  { id: "tasks", label: "Queue", icon: IconChecklist, hint: "the batches waiting and running now, and the kinds of task there are" },
  { id: "scheduler", label: "Scheduler", icon: IconCalendarRepeat, hint: "jobs run at set intervals, and how their runs went" },
];

export function TasksModule({ db, view, onSelectView }: { db: DatabaseInfo; view: TasksView; onSelectView: (view: TasksView) => void }) {
  // bumped by every save, here or in the Move to shared dialog: the button reads the overrides file
  // again, and the scheduler asks for a fresh picture
  const [version, setVersion] = useState(0);
  return (
    <div className="tasks-module">
      <div className="tasks-module-head">
        <div className="module-switch" role="tablist">
          {views.map((v) => (
            <button key={v.id} role="tab" aria-selected={view === v.id} className={view === v.id ? "active" : ""} title={v.hint} onClick={() => onSelectView(v.id)}>
              <v.icon size={15} stroke={1.8} />
              {v.label}
            </button>
          ))}
        </div>
        {view === "scheduler" && (
          <SettingsShareButton
            className="at-end"
            scope={{
              title: "Schedules of " + db.name,
              filter: (entry, group) => group.storeId === db.id && /^LocalSettings\.Schedules\b/i.test(databasePath(entry, group) ?? ""),
            }}
            changeKey={version}
            onChanged={() => setVersion((n) => n + 1)}
          />
        )}
      </div>
      {view === "scheduler" ? (
        <SchedulerView key={db.id} db={db} version={version} onSaved={() => setVersion((n) => n + 1)} />
      ) : (
        <TasksSection key={db.id} db={db} />
      )}
    </div>
  );
}
