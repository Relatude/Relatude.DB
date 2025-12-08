import { API } from "./api";
export interface EventData<T> {
    id: string;
    timestamp: Date;
    eventName: string;
    filter?: string;
    payload: T;
}
export interface EventSubscription {
    eventName: string;
    filter?: string;
}
class EventListener<T> {
    onEvent: (data: T, filter?: string) => void;
    onError?: (error: any, filter?: string) => void;
    Subscription: EventSubscription;

    constructor(name: string, filter: string | undefined,
        onEvent: (data: T, filter?: string) => void,
        onError?: (error: any, filter?: string) => void) {
        this.onEvent = onEvent;
        this.onError = onError;
        this.Subscription = { eventName: name, filter };
    }
}
export class ServerEventHub {
    private api: API;
    private onAnyEvent: ((eventData: EventData<unknown>) => void) | null;
    private onEventError: ((error: any) => void) | null;
    private onConnectionError: ((error: any) => void) | null;

    constructor(
        api: API,
        onAnyEvent: ((eventData: EventData<unknown>) => void) | null = null,
        onEventError: ((error: any) => void) | null = null,
        onConnectionError: ((error: any) => void) | null = null,
    ) {
        this.api = api;
        this.onAnyEvent = onAnyEvent;
        this.onEventError = onEventError;
        this.onConnectionError = onConnectionError;
    }
    private _eventSource: EventSource | null = null;
    private _connectionId: string | null = null;
    private _onReceivedConnectionId: ((id: string) => void) | null = null;
    private _onErrorReceivingConnectionId: ((error: any) => void) | null = null;
    private _eventListeners: EventListener<any>[] = [];
    connect = () => {
        if (this._eventSource) this.disconnect();
        this._eventSource = this.api.status.connect();
        this._eventSource.onmessage = (event: MessageEvent) => {
            if (!this._connectionId) this.onFirstMessage(event);
            else this.onEventMessage(event);
        };
        this._eventSource.onerror = (error: any) => {
            if (this.onConnectionError) this.onConnectionError(error);
            else console.log("EventSource connection error", error);
        }
        return new Promise<string>((resolve, reject) => {
            this._onReceivedConnectionId = resolve;
            this._onErrorReceivingConnectionId = reject;
        });
    }
    addEventListener = async <T>(eventName: string, eventFilter: string | undefined, onEvent: (data: T, filter?: string | undefined) => void) => {
        this._eventListeners.push(new EventListener<T>(eventName, eventFilter, onEvent));
        await this.api.status.setSubscriptions(this._connectionId!, this._eventListeners.map(l => l.Subscription));
    }
    removeEventListener = async <T>(eventName: string, onEvent: (data: T) => void) => {
        this._eventListeners = this._eventListeners.filter(l => l.Subscription.eventName != eventName || l.onEvent != onEvent);
        await this.api.status.setSubscriptions(this._connectionId!, this._eventListeners.map(l => l.Subscription));
    }
    disconnect = () => {
        this._connectionId == null;
        this._onReceivedConnectionId = null;
        this._onErrorReceivingConnectionId = null;
        this._eventListeners = [];
        if (this._eventSource) {
            try {
                this._eventSource.close();
            } catch (err) {
                console.log("Error closing EventSource", err);
            }
            this._eventSource = null;
        }
    }
    private toEventData = <T>(message: MessageEvent): EventData<T> => JSON.parse(message.data) as EventData<T>;
    private onFirstMessage = (event: MessageEvent) => { // first message contains connection id
        try {
            if (!this._onReceivedConnectionId) throw new Error("No handler for connection id");
            var eventData = this.toEventData<string>(event);
            this._connectionId = eventData.payload;
            if (!this._connectionId) throw new Error("Invalid connection id");
            this._onReceivedConnectionId(this._connectionId);
        } catch (err) {
            if (this._onErrorReceivingConnectionId) this._onErrorReceivingConnectionId(err);
        }
    }
    private onEventMessage = (event: MessageEvent) => {
        try {
            const eventData = this.toEventData(event);
            if (this.onAnyEvent) this.onAnyEvent(eventData);
            const listeners = this._eventListeners.filter(l =>
                l.Subscription.eventName == eventData.eventName
                && (l.Subscription.filter == eventData.filter || !l.Subscription.filter)
            );
            for (const l of listeners) {
                try {
                    l.onEvent(eventData.payload, eventData.filter);
                } catch (err) {
                    if (l.onError) l.onError(err, eventData.filter);
                    else console.log("Error in event listener", err);
                }
            }
        } catch (err) {
            if (this.onEventError) this.onEventError(err);
            else console.log("Error processing event", err);
        }
    }
}
