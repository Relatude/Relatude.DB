
export const iconSize = "1.5rem";
export const iconStroke = 1;
export function sleep(ms: number) {
    return new Promise(resolve => setTimeout(resolve, ms));
}

export function formatNumber(num: number|undefined): string {
    if(num === undefined) return "0";
    return num.toLocaleString(undefined, { maximumFractionDigits: 2 });
}
export function formatBytes(bytes: number): string {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'Kb', 'Mb', 'Gb', 'Tb', 'Pb', 'Eb', 'Zb', 'Yb'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
}
export function formatTimeSpan(ms: number): string {
    ms = Math.floor(ms);
    const milliseconds = ms % 1000;
    const seconds = Math.floor((ms / 1000) % 60);
    const minutes = Math.floor((ms / (1000 * 60)) % 60);
    const hours = Math.floor((ms / (1000 * 60 * 60)) % 24);
    const days = Math.floor(ms / (1000 * 60 * 60 * 24));
    const s = (s: number) => (s == 1 ? "" : "s"); 
    let timestring = "";
    if(days > 0) timestring += days + " day" + s(days) + ", ";
    if(hours > 0) timestring += hours + " hour" + s(hours) + ", ";
    if(minutes > 0) timestring += minutes + " minute" + s(minutes) + ", ";
    if(seconds > 0 && days == 0) timestring += seconds + " second" + s(seconds);
    if(ms<3000) timestring = milliseconds + " ms";
    return timestring;
}
// HH:mm:ss
export function formatTimeOfDay(date: Date): string {
    const hours = date.getHours().toString().padStart(2, '0');
    const minutes = date.getMinutes().toString().padStart(2, '0');
    const seconds = date.getSeconds().toString().padStart(2, '0');
    return `${hours}:${minutes}:${seconds}`;
}