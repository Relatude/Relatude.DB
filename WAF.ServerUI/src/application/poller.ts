export class Poller {
    private _callback: () => Promise<void>;
    private _activeInterval = 500;
    private _lastUserActivity = Date.now();
    private _lastPoll = 0;
    private _lastTimeout = 0;
    private _disposed = false;
    constructor(callback: () => Promise<void>) {
        window.addEventListener("mousemove", () => this.updateLastUserActivity());
        window.addEventListener("click", () => this.updateLastUserActivity());
        window.addEventListener("keydown", () => this.updateLastUserActivity());
        this._callback = callback;
        this.poll();
    }
    private updateLastUserActivity = async () => {
        this._lastUserActivity = Date.now();
        if (Date.now() - this._lastPoll > this._activeInterval * 2) await this.pollNow();
    }
    private poll = async () => {
        try {
            await this.pollNow();
        } catch (e) { }
        const active = this._activeInterval; // 500ms
        const semiActive = active * 10; // 5s
        const inactive = semiActive * 10; // 50s
        const thresholdActive = 120; // 2 min,  no activity after this time will be considered semi-active
        const thresholdSemiActive = 240; // 4 min,  no activity after this time will be considered inactive
        const lastSecAgo = (Date.now() - this._lastUserActivity) / 1000;
        let intervalMs = lastSecAgo > thresholdSemiActive ? inactive : (lastSecAgo > thresholdActive ? semiActive : active);
        this._lastTimeout = window.setTimeout(() => this.poll(), intervalMs);
    }
    private pollNow = async () => {
        if (this._disposed) return;
        this._lastPoll = Date.now();
        await this._callback();
    }
    dispose = () => {
        this._disposed = true;
        window.removeEventListener("mousemove", () => this.updateLastUserActivity());
        window.removeEventListener("click", () => this.updateLastUserActivity());
        window.removeEventListener("keydown", () => this.updateLastUserActivity());
        window.clearTimeout(this._lastTimeout);
    }
}