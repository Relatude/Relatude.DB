import React, { useEffect, useState } from "react";
import { observer } from "mobx-react";
import { Table, Button, Group, Tabs, } from "@mantine/core";
import { useApp } from "../../start/useApp";
import { ServerLogEntry } from "../../application/models";
import { Poller } from "../../application/poller";

export const component = () => {
  const app = useApp();

  const [serverLog, setServerLog] = useState<ServerLogEntry[]>();
  useEffect(() => {
    const poller = new Poller(async () => {
      setServerLog(await app.api.server.getServerLog());
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
  return (
    <>
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
