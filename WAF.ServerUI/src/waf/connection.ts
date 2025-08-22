import { IQuery, Parameter, QueryOfNodes } from "./query";

export class Connection {
    private _address: string;
    private _headers: HeadersInit;
    private _includeRequestCredentials: RequestCredentials;
    constructor(address: string, includeRequestCredentials: RequestCredentials = 'same-origin') {
        this._address = address;
        if (!this._address.endsWith("/")) this._address += "/";
        this._headers = { 'Content-Type': 'application/json' };
        this._includeRequestCredentials = includeRequestCredentials;
    }
    public SetHeader = (key: string, value: string) => this._headers[key] = value;
    public DeleteHeader = (key: string) => delete this._headers[key];
    public async PostQuery<T>(query: string, parameters: Parameter[], storeId: string): Promise<T> {
        const init: RequestInit = {
            method: 'POST',
            headers: this._headers,
            credentials: this._includeRequestCredentials,
            body: JSON.stringify({ query, parameters }),
        };
        const response = await fetch(this._address + "query?storeId=" + storeId, init);
        if (!response.ok) throw new Error("Network response error: " + response.statusText);
        return await response.json() as T;
    }
}