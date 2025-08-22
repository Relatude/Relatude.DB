import React from "react";
import { observer } from "mobx-react-lite";
import { AppShell, Burger, Button, Group, ScrollArea } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconBrightnessUp, IconMoon } from "@tabler/icons-react";
import LogoSmall from "../components/logoSmall";
import { useApp } from "./useApp";
import { iconSize, iconStroke } from "../application/constants";
import { MainMenu } from "../components/mainMenu";
import Status from "../sections/status/status";
import { Datamodel } from "../sections/datamodel/datamodel";
import Data from "../sections/data/data";
import Files from "../sections/files/files";
import { Settings } from "../sections/settings/settings";
import Server from "../sections/server/server";
import { API } from "../sections/api/api";
import Monitor from "../sections/monitor/monitor";

const component = () => {
  const app = useApp();
  const [opened, { toggle }] = useDisclosure();
  const storeId = app.ui.selectedStoreId;
  return (
    <AppShell
      header={{ height: 60 }}
      navbar={{
        width: 200,
        breakpoint: "sm",
        collapsed: { mobile: !opened },
      }}
      padding="md"
    >
      <AppShell.Header>
        <Group h="100%" px="md" w={"100%"} justify="space-between">
          <Group h="100%" px="md">
            <Burger
              opened={opened}
              onClick={toggle}
              hiddenFrom="sm"
              size="sm"
            />
            <LogoSmall padding="10px" />
          </Group>
          <Group justify="flex-end">
            <Button
              variant="subtle"
              onClick={() => (app.ui.darkTheme = !app.ui.darkTheme)}
            >
              {app.ui.darkTheme ? (
                <IconBrightnessUp size={iconSize} stroke={iconStroke} />
              ) : (
                <IconMoon size={iconSize} stroke={iconStroke} />
              )}
            </Button>
            <Button
              variant="light"
              onClick={async () => {
                await app.api.auth.logout();
                app.ui.appState = "login";
              }}
            >
              Logout
            </Button>
          </Group>
        </Group>
      </AppShell.Header>
      <AppShell.Navbar p="md">
        <ScrollArea>
          <MainMenu />
        </ScrollArea>
      </AppShell.Navbar>
      <AppShell.Main>
        {storeId === app.ui.menu.selected && <Status />}
        {app.ui.menu.selected === "server" && <Server />}
        {storeId && (<>
          {app.ui.menu.selected === "datamodel" && <Datamodel storeId={storeId}/>}
          {app.ui.menu.selected === "data" && <Data storeId={storeId} />}
          {app.ui.menu.selected === "api" && <API storeId={storeId}/>}
          {app.ui.menu.selected === "files" && <Files storeId={storeId} />}
          {app.ui.menu.selected === "logs" && <Monitor storeId={storeId}/>}
          {app.ui.menu.selected === "settings" && <Settings storeId={storeId}/>}
        </>)}
      </AppShell.Main>
    </AppShell>
  );
};
export const Main = observer(component);
