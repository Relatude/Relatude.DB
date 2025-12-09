import { Combobox, Group, Input, InputBase, useCombobox } from "@mantine/core";
import React, { useEffect } from "react";
import { iconSize, iconStroke } from "../application/constants";
import { observer } from "mobx-react-lite";
import { useApp } from "../start/useApp";
import { IconDatabase } from "@tabler/icons-react";
import { IoSetting } from "../application/models";

const component = (p: { ioSettings?: IoSetting[], selectedIo?: string, onChange?: (ioId: string) => void }) => {
    const app = useApp();
    const storeCombobox = useCombobox();
    const selectedIoSetting = p.ioSettings?.find(c => c.id === p.selectedIo);
    return (
        <Combobox store={storeCombobox} >
            <Combobox.Target>
                <InputBase
                    w={300}
                    component="button"
                    type="button"
                    pointer
                    rightSection={<Combobox.Chevron />}
                    rightSectionPointerEvents="none"
                    onClick={() => storeCombobox.toggleDropdown()}
                >
                    {selectedIoSetting ?
                        <Group>
                            <IconDatabase size={iconSize} stroke={iconStroke} />
                            {selectedIoSetting.name}
                        </Group>
                        : <Input.Placeholder>{p.ioSettings && p.ioSettings.length > 0 ? "Pick IO provider" : "No IO provider"}</Input.Placeholder>}
                </InputBase>
            </Combobox.Target>
            <Combobox.Dropdown>
                <Combobox.Options mah={200} style={{ overflowY: 'auto' }}>
                    {p.ioSettings?.map((store) => (
                        <Combobox.Option key={store.id} value={store.id} onClick={() => { p.onChange?.(store.id); storeCombobox.closeDropdown() }}>
                            <Group>
                                <IconDatabase size={iconSize} stroke={iconStroke} />
                                {store.name}
                            </Group>
                        </Combobox.Option>
                    ))}
                </Combobox.Options>
            </Combobox.Dropdown>
        </Combobox>

    );
}
const observableComponent = observer(component);
export default observableComponent;
