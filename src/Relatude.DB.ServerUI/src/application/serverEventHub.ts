import { API } from "./api";
import { EventData } from "./models";

class EventListener<T> {
    constructor(
        public name: string,
        public onEvent: (data: T) => void,
        public onError?: (error: any) => void) { }
}
export class ServerEventHub {
    constructor(
        private api: API,
        private onAnyEvent: (event: EventData<any>) => void,
        private onEventError: (error: any) => void,
        private onConnectionError: (error: any) => void,
    ) { }
    private _eventSource: EventSource | null = null;
    private _subscriptionId: string | null = null;
    private _onReceivedSubscriptionId: ((id: string) => void) | null = null;
    private _onErrorReceivingSubscriptionId: ((error: any) => void) | null = null;
    private _eventListeners: EventListener<any>[] = [];
    connect = () => {
        if (this._eventSource) this.disconnect();
        this._eventSource = this.api.status.createEventSource();
        this._eventSource.onmessage = this.onEventSourceMessage;
        this._eventSource.onerror = this.onEventSourceError;
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
    private onEventSourceMessage = (event: MessageEvent) => {
        if (!this._subscriptionId) this.onFirstMessage(event);
        else this.onEventMessage(event);
    }
    private onEventSourceError = (error: any) => {
        console.log("EventSource error", error);
        this.onConnectionError(error);
    }
    private onFirstMessage = (event: MessageEvent) => {
        try {
            if (!this._onReceivedSubscriptionId) throw new Error("No handler for subscription id");
            var eventData = JSON.parse(event.data) as EventData<string>;
            this._subscriptionId = eventData.data
            if (!this._subscriptionId) throw new Error("Invalid subscription id");
            console.log("Subscribed to events with id", this._subscriptionId);
            this._onReceivedSubscriptionId(this._subscriptionId);
        } catch (err) {
            if (this._onErrorReceivingSubscriptionId) this._onErrorReceivingSubscriptionId(err);
        }
    }
    private onEventMessage = (event: MessageEvent) => {
        try {
            const eventData = JSON.parse(event.data) as EventData<any>;
            this.onAnyEvent(eventData);
            const listeners = this._eventListeners.filter(l => l.name == eventData.name);
            for (const l of listeners) {
                try {
                    l.onEvent(eventData.data);
                } catch (err) {
                    if (l.onError) l.onError(err);
                }
            }
        } catch (err) {
            this.onEventError(err);
        }
    }
}
