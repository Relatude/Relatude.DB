import React, { useEffect, useState } from "react";
import { observer } from "mobx-react";
import { Table, Button, Group, Tabs, Card, Modal, Text, Alert, Loader, Stack } from "@mantine/core";
import { useApp } from "../../start/useApp";
import { RestartInfo, ServerLogEntry } from "../../application/models";
import { Poller } from "../../application/poller";
import { formatTimeSpan, sleep } from "../../application/common";

type confirmKind = "soft" | "stop";

export const component = () => {
  const app = useApp();
  const [uptimeInMs, setUptimeInMs] = useState<number>(0);

  const [serverLog, setServerLog] = useState<ServerLogEntry[]>();
  const [restartInfo, setRestartInfo] = useState<RestartInfo>();
  const [confirm, setConfirm] = useState<confirmKind | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  useEffect(() => {
    const poller = new Poller(async () => {
      setServerLog(await app.api.server.getServerLog());
      const uptime =  await app.api.status.uptimeInMs();
      setUptimeInMs(uptime);
      setRestartInfo(await app.api.server.getRestartInfo());
    });
    return () => { poller.dispose(); }
  }, []);
  const createStore = async () => {
    const storeId = await app.api.server.createStore();
  };
  const removeStore = async (storeId: string) => {
    if (!window.confirm("Are you sure you want to delete this store?")) return;
    await app.api.server.removeStore(storeId);
    app.ui.storeStates.delete(storeId);
  }
  const setDefaultStore = async (storeId: string) => {
    await app.api.server.setDefaultStoreId(storeId);
  }
  // The server answers the restart call before it does the work, so both buttons finish by watching
  // for proof that the restart landed rather than by waiting on the response.
  const waitFor = async (done: () => Promise<boolean>, timeoutSec: number) => {
    const deadline = Date.now() + timeoutSec * 1000;
    while (Date.now() < deadline) {
      await sleep(1000);
      try { if (await done()) return true; } catch { /* still down: keep waiting */ }
    }
    return false;
  }
  const softRestart = async () => {
    setConfirm(null);
    setFailed(null);
    const before = restartInfo?.restartCount ?? 0;
    const result = await app.api.server.softRestart();
    if (!result.started) { setFailed(result.message); return; }
    setBusy("Reloading the settings and reopening the databases…");
    // a soft restart never replaces the process, so a higher restart count is the proof it finished
    const ok = await waitFor(async () => {
      const info = await app.api.server.getRestartInfo();
      if (info.isRestarting || info.restartCount <= before) return false;
      setRestartInfo(info);
      return true;
    }, 120);
    setBusy(null);
    if (!ok) setFailed("The restart did not report back in time. Check the server events below.");
  }
  const stopHost = async () => {
    setConfirm(null);
    setFailed(null);
    const result = await app.api.server.stopHost();
    if (!result.started) { setFailed(result.message); return; }
    setBusy("Stopping the host and waiting for a new process…");
    // the process is replaced, so the admin API goes away entirely. Wait until it has actually gone
    // before believing that a reply means the new process is up.
    let sawDown = false;
    const ok = await waitFor(async () => {
      try {
        await app.api.auth.isLoggedIn(); // public, and refused while the server is stopping
      } catch {
        sawDown = true;
        return false;
      }
      return sawDown;
    }, 180);
    setBusy(null);
    if (ok) window.location.reload(); // the login may not have survived the process
    else setFailed("The server did not come back. It may need to be started by hand.");
  }
  const hostRestarts = restartInfo?.hostRestartsAutomatically;
  return (
    <>
      <Card withBorder>
          <Group justify="space-between">
            <Group>
              <div>Server Uptime: {formatTimeSpan(uptimeInMs)}</div>
              {restartInfo && <Text c="dimmed" size="sm">Host: {restartInfo.hostDescription}</Text>}
            </Group>
            <Group gap={"sm"}>
              {restartInfo?.canSoftRestart &&
                <Button variant="light" onClick={() => setConfirm("soft")}>Soft restart</Button>}
              {restartInfo?.canStopHost &&
                <Button variant="light" color={hostRestarts === false ? "red" : "orange"} onClick={() => setConfirm("stop")}>
                  {hostRestarts === false ? "Stop application" : "Restart application"}
                </Button>}
            </Group>
          </Group>
          {failed && <Alert color="red" mt="sm" withCloseButton onClose={() => setFailed(null)}>{failed}</Alert>}
      </Card>

      <Modal opened={confirm === "soft"} onClose={() => setConfirm(null)} title="Soft restart">
        <Stack gap="sm">
          <Text size="sm">
            Closes every database, reads <code>relatude.db.json</code> again and opens them from the new settings.
            The process keeps running, so this is safe on any host.
          </Text>
          <Text size="sm" c="dimmed">
            It does not pick up changes to code, to the server options set in <code>Program.cs</code>, to the admin UI URL
            path, or to environment variables. Those need the process itself to restart.
          </Text>
          <Text size="sm">Requests to the application are refused with 503 until the databases are open again.</Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setConfirm(null)}>Cancel</Button>
            <Button onClick={softRestart}>Soft restart</Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={confirm === "stop"} onClose={() => setConfirm(null)} title="Restart the application">
        <Stack gap="sm">
          <Text size="sm">
            Stops the host: requests drain, the databases close cleanly and the process exits.
            Nothing in Relatude.DB starts it again &mdash; that is up to whatever is running the process.
          </Text>
          <Text size="sm">Detected host: <b>{restartInfo?.hostDescription}</b></Text>
          {restartInfo?.stopHostWarning &&
            <Alert color={hostRestarts === false ? "red" : "yellow"}>{restartInfo.stopHostWarning}</Alert>}
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setConfirm(null)}>Cancel</Button>
            <Button color={hostRestarts === false ? "red" : "orange"} onClick={stopHost}>
              {hostRestarts === false ? "Stop anyway" : "Restart"}
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={busy !== null} onClose={() => { }} withCloseButton={false} closeOnClickOutside={false}
        closeOnEscape={false} title="Restarting">
        <Group>
          <Loader size="sm" />
          <Text size="sm">{busy}</Text>
        </Group>
      </Modal>

      <Tabs defaultValue="databases">
        <Tabs.List>
          <Tabs.Tab value="databases" >Databases</Tabs.Tab>
          <Tabs.Tab value="log" >Server events</Tabs.Tab>
        </Tabs.List>
        <Tabs.Panel value="databases">
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Id</Table.Th>
                <Table.Th>Name</Table.Th>
                <Table.Th>Description</Table.Th>
                <Table.Th>State</Table.Th>
                <Table.Th></Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {app.ui.containers.map((s) => (
                <Table.Tr key={s.id}>
                  <Table.Td>{s.id}</Table.Td>
                  <Table.Td>
                    {s.name + (s.id === app.ui.defaultStoreId ? " - [DEFAULT]" : "")}
                  </Table.Td>
                  <Table.Td>{s.description}</Table.Td>
                  <Table.Td>{s.status.state}</Table.Td>
                  <Table.Td>
                    <Group gap={"sm"}>
                      <Button variant="light" color="green" onClick={() => (app.ui.selectedStoreId = s.id)}>View</Button>
                      <Button variant="light" color="" disabled={s.id == app.ui.defaultStoreId} onClick={() => setDefaultStore(s.id)}>Make default</Button>
                      <Button variant="light" color="red" onClick={() => removeStore(s.id)}>Remove</Button>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
          <Button onClick={createStore}>Create new</Button>

        </Tabs.Panel>
        <Tabs.Panel value="log">
          <Table>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Timestamp</Table.Th>
                <Table.Th>Event</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {serverLog?.map((entry, index) => (
                <Table.Tr key={index}>
                  <Table.Td>{entry.timestamp.toLocaleTimeString()}</Table.Td>
                  <Table.Td>{entry.description}</Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        </Tabs.Panel>
      </Tabs>
    </>
  );
};

const observableComponent = observer(component);
export default observableComponent;
