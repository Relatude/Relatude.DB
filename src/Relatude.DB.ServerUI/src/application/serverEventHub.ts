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
    constructor(name: string, filter: string | undefined,
        public onEvent: (data: T, filter?: string) => void,
        public onError?: (error: any, filter?: string) => void) {
        this.Subscription = { eventName: name, filter };
    }
    Subscription: EventSubscription;
}
export class ServerEventHub {
    constructor(
        private api: API,
        private onAnyEvent: ((eventData: EventData<unknown>) => void) | null = null,
        private onEventError: ((error: any) => void) | null = null,
        private onConnectionError: ((error: any) => void) | null = null,
    ) { }
    private _eventSource: EventSource | null = null;
    private _connectionId: string | null = null;
    private _onReceivedConnectionId: ((id: string) => void) | null = null;
    private _onErrorReceivingConnectionId: ((error: any) => void) | null = null;
    private _eventListenersBySubId: Map<string, EventListener<any>> = new Map();
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

    addEventListener = async <T>(eventName: string, eventFilter: string | undefined, onEvent: (data: T, filter?: string) => void) => {
        var subId = await this.api.status.subscribe(this._connectionId!, eventName, eventFilter);
        this._eventListenersBySubId.set(subId, new EventListener<T>(eventName, eventFilter, onEvent));
        // console.log("Subscribed to event", eventName, eventFilter, subId);
        return subId;
    }
    removeEventListener = async <T>(subId: string) => {
        this._eventListenersBySubId.delete(subId);
        await this.api.status.unsubscribe(this._connectionId!, subId);
        // console.log("Unsubscribed from event", subId);
    }
    disconnect = () => {
        this._connectionId == null;
        this._onReceivedConnectionId = null;
        this._onErrorReceivingConnectionId = null;
        this._eventListenersBySubId.clear();
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
        // console.log("Received first message (connection id)", event.data);
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
            // console.log("Received event: ", eventData.eventName);
            if (this.onAnyEvent) this.onAnyEvent(eventData);
            const listeners = Array.from(this._eventListenersBySubId.values()).filter(l =>
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
