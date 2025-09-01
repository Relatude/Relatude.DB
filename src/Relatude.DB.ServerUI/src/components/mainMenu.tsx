import { NavLink } from '@mantine/core';
import { makeAutoObservable } from 'mobx';
import { observer } from 'mobx-react-lite';
import React from 'react';
import { iconSize, iconStroke } from '../application/constants';
import { useApp } from '../start/useApp';
export class menuData {
    private _icon: any = null; get icon() { return this._icon; } set icon(value) { this._icon = value; }
    private _label: string = ""; get label() { return this._label; } set label(value) { this._label = value; }
    private _key: string = ""; get key() { return this._key; } set key(value) { this._key = value; }
    private _children: menuData[] = []; get children() { return this._children; } set children(value) { this._children = value; }
    add(icon: any, label: string, key: string) {
        var child = new menuData(icon, label, key);
        this.children.push(child);
        return child;
    }
    private _color: string | null = null; get color() { return this._color; } set color(value) { this._color = value; }
    constructor(icon: any, label: string, key: string) {
        this._icon = icon;
        this._label = label;
        this._key = key;
        makeAutoObservable(this);
    }
}
export class menuStore {
    private _items: menuData[] = []; get items() { return this._items; } set items(value) { this._items = value; }
    private _path: string[] = []; get path() { return this._path; }
    clearPath() {
        this._path.length = 0;
    }    
    setSelected(key: string, level: number) {
        this._path.length = level;
        this._path[level] = key;
    }
    get selected() { return this.path[this.path.length - 1]; }
    constructor(items: menuData[]) {
        this._items = items;
        makeAutoObservable(this);
    }
}
const renderItems = (items: menuData[], level: number, store: menuStore) => {
    return items.map((item, i) => {
        const selected = store.path[level] === item.key && store.path.length === level + 1;
        const parent = store.path.includes(item.key) && !selected;
        return (
            <div key={item.key} style={{ paddingLeft: level * 20 }}>
                <NavLink
                    onClick={() => store.setSelected(item.key, level)}
                    active={selected}
                    label={item.label}
                    leftSection={<item.icon size={iconSize} stroke={iconStroke} color={item.color ? item.color : undefined} />}
                />
                {(parent || selected) && item.children ? renderItems(item.children, level + 1, store) : null}
            </div>
        );
    });
}
const component = () => {
    const app = useApp();
    const store = app.ui.menu;
    return (
        <> {
            renderItems(store.items, 0, app.ui.menu)
        } </>
    );
}
export const MainMenu = observer(component);

