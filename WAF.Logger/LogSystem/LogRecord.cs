namespace WAF.LogSystem;
public class LogRecord {
    internal LogRecord(DateTime timeStamp, byte[] data) {
        TimeStamp = timeStamp;
        Data = data;
    }
    public readonly DateTime TimeStamp;
    public readonly byte[] Data;
}
