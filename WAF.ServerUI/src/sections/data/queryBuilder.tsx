
import { useApp } from "../../start/useApp";
import { observer } from "mobx-react-lite";
import { Datamodel } from "../../waf/datamodel";
import { MultiSelect } from "@mantine/core";
import { makeAutoObservable } from "mobx";
import { useState } from "react";
import { QueryResult } from "./queryResult";
import { QueryOfNodes } from "../../waf/query";
import { Connection } from "../../waf/connection";
export class QueryBuilderStore {
    id: string;
    get name() {
        if (this._fromTypes.length === 0) return "[Query]";
        return `${this._fromTypes.map(t => this.datamodel.getNodeType(t).codeName).join(", ")}`;
    }
    datamodel: Datamodel;
    private _fromTypes: string[] = []; get fromTypes() { return this._fromTypes; }
    private _searchText: string = ""; get searchText() { return this._searchText; } set searchText(text: string) { this._searchText = text; }
    private _page: { page: number, pageSize: number } = { page: 0, pageSize: 10 }; get page() { return this._page; } set page(p: { page: number, pageSize: number }) { this._page = p; }
    setTypes = (types: string[]) => this._fromTypes = types;
    getQuery() {
        let fromName: string;
        let whereTypes: string[] | undefined;
        if (this._fromTypes.length > 0) {
            fromName = this.datamodel.getBaseNodeType().codeName;
            whereTypes = this._fromTypes.map(t => this.datamodel.getNodeType(t).codeName);
        } else if (this._fromTypes.length === 1) { 
            fromName = this.datamodel.getNodeType(this._fromTypes[0]).codeName;
        } else { // length === 0
            fromName = this.datamodel.getBaseNodeType().codeName;
        }
        let query = new QueryOfNodes<any>(fromName, this._connection, this._storeId);
        if (whereTypes) query = query.WhereTypes(whereTypes);
        if (this._searchText) query.Search(this._searchText);
        if (this._page) query = query.Page(this._page.page, this._page.pageSize);
        return query;
    }
    private _connection: Connection;
    private _storeId: string;
    constructor(datamodel: Datamodel, connection: Connection, storeId: string) {
        makeAutoObservable(this);
        this.id = crypto.randomUUID();
        this.datamodel = datamodel;
        this._connection = connection;
        this._storeId = storeId;
    }
}
export const component = (P: { store: QueryBuilderStore, storeId: string }) => {
    const [dropdownOpened, setDropdownOpened] = useState(false);
    const comboTypes = P.store.datamodel.getNodeTypes().map(nt => ({
        value: nt.id,
        label: nt.codeName,
        selected: P.store.fromTypes.includes(nt.id),
    }));
    return (
        <>
            <div style={{ width: "100%", height: "100%", minWidth: 200, maxWidth: 400 }}>
                <MultiSelect
                    defaultValue={P.store.fromTypes}
                    searchable
                    clearable
            
                    dropdownOpened={dropdownOpened}
                    onDropdownOpen={() => setDropdownOpened(true)}
                    onDropdownClose={() => setDropdownOpened(false)}
                    label="Select Node Types" placeholder="Pick value" data={comboTypes}
                    onChange={(v) => { P.store.setTypes(v); if (v.length === 1) setDropdownOpened(false) }}

                />
                <div style={{ marginTop: "10px" }}>
                    <input autoFocus type="text" placeholder="Search..." value={P.store.searchText} onChange={(e) => P.store.searchText = e.target.value} />
                </div>
            </div>
            <div style={{ width: "100%", height: "100%" }}>
                <QueryResult store={P.store} />
            </div></>
    );
}

export const QueryBuilder = observer(component);



