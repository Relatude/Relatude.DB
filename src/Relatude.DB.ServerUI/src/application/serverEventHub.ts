import { API } from "./api";

export interface EventData<T> {
    id: string;
    timestamp: Date;
    maxAge: number;
    name: string;
    data: T;
}
class EventListener<T> {
    constructor(
        public name: string,
        public onEvent: (data: T) => void,
        public onError?: (error: any) => void) { }
}
export class ServerEventHub {
    constructor(
        private api: API,
        private onAnyEvent: ((eventData: EventData<unknown>) => void) | null = null,
        private onEventError: ((error: any) => void) | null = null,
        private onConnectionError: ((error: any) => void) | null = null,
    ) { }
    private _eventSource: EventSource | null = null;
    private _subscriptionId: string | null = null;
    private _onReceivedSubscriptionId: ((id: string) => void) | null = null;
    private _onErrorReceivingSubscriptionId: ((error: any) => void) | null = null;
    private _eventListeners: EventListener<any>[] = [];
    connect = () => {
        if (this._eventSource) this.disconnect();
        this._eventSource = this.api.status.createEventSource();
        this._eventSource.onmessage = (event: MessageEvent) => {
            if (!this._subscriptionId) this.onFirstMessage(event);
            else this.onEventMessage(event);
        };
        this._eventSource.onerror = (error: any) => {
            if (this.onConnectionError) this.onConnectionError(error);
            else console.log("EventSource connection error", error);
        }
        return new Promise((resolve, reject) => {
            this._onReceivedSubscriptionId = resolve;
            this._onErrorReceivingSubscriptionId = reject;
        });
    }
    addEventListener = async <T>(eventName: string, onEvent: (data: T) => void) => {
        this._eventListeners.push(new EventListener<T>(eventName, onEvent));
        await this.api.status.changeSubscription(this._subscriptionId!, this._eventListeners.map(l => l.name));
    }
    removeEventListener = async <T>(eventName: string, onEvent: (data: T) => void) => {
        this._eventListeners = this._eventListeners.filter(l => l.name != eventName || l.onEvent != onEvent);
        await this.api.status.changeSubscription(this._subscriptionId!, this._eventListeners.map(l => l.name));
    }
    disconnect = () => {
        this._subscriptionId == null;
        this._onReceivedSubscriptionId = null;
        this._onErrorReceivingSubscriptionId = null;
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
    private onFirstMessage = (event: MessageEvent) => { // first message contains subscription id
        try {
            if (!this._onReceivedSubscriptionId) throw new Error("No handler for subscription id");
            var eventData = this.toEventData<string>(event);
            this._subscriptionId = eventData.data
            if (!this._subscriptionId) throw new Error("Invalid subscription id");
            this._onReceivedSubscriptionId(this._subscriptionId);
        } catch (err) {
            if (this._onErrorReceivingSubscriptionId) this._onErrorReceivingSubscriptionId(err);
        }
    }
    private onEventMessage = (event: MessageEvent) => {
        try {
            const eventData = this.toEventData(event);
            if (this.onAnyEvent) this.onAnyEvent(eventData);
            const listeners = this._eventListeners.filter(l => l.name == eventData.name);
            for (const l of listeners) {
                try {
                    l.onEvent(eventData.data);
                } catch (err) {
                    if (l.onError) l.onError(err);
                    else console.log("Error in event listener", err);
                }
            }
        } catch (err) {
            if (this.onEventError) this.onEventError(err);
            else console.log("Error processing event", err);
        }
    }
}
