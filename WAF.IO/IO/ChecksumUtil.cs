using WAF.Common;
namespace WAF.IO;
public class ChecksumUtil {
    bool _recordingChecksum;
    uint _checkSum;
    public void RecordChecksum() {
        _checkSum = 0;
        _recordingChecksum = true;
    }
    public void EvaluateChecksumIfRecording(byte[] bytes) {
        if (_recordingChecksum) bytes.EvaluateChecksum(ref _checkSum);
    }
    public void ValidateChecksum(IReadStream stream) {
        _recordingChecksum = false;
        var checkSum = stream.ReadUInt();
        if (checkSum != _checkSum) throw new Exception("Invalid checksum");
        _checkSum = 0;
    }
    public void WriteChecksum(IAppendStream stream) {
        _recordingChecksum = false;
        stream.WriteUInt(_checkSum);
    }
}
