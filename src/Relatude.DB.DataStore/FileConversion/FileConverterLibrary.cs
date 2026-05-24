using Relatude.DB.Common;
using System.Diagnostics.CodeAnalysis;

namespace Relatude.DB.FileConversion;

public class FileConverterLibrary {
    struct converterInfo(int concurrentCount, DateTime lastWork) {
        public readonly int ConcurrentCount = concurrentCount;
        public readonly DateTime LastWork = lastWork;
    }
    private readonly IFileConverter[] _converters;
    private readonly Dictionary<FormatPair, IFileConverter?> _lookUp; // from, to
    private readonly Dictionary<IFileConverter, converterInfo> _concurrentWork;
    public FileConverterLibrary(IFileConverter[] converters) {
        _converters = converters;
        _lookUp = new();
        _concurrentWork = new();
    }
    public bool TryGetConverter(FormatPair key, [MaybeNullWhen(false)] out IFileConverter converter) {
        lock (_lookUp) {
            if (_lookUp.TryGetValue(key, out var match)) {
                converter = match;
                return match != null;
            }
            converter = null;
            foreach (var c in _converters) {
                // pick first match:
                var fromBase = FileFormatUtil.GetBaseFormatFromDetailedFormat(key.From);
                var toBase = FileFormatUtil.GetBaseFormatFromDetailedFormat(key.To);
                if (c.SupportsConversion(fromBase, key.From, toBase, key.To)) {
                    converter = c;
                    break;
                }
            }
            _lookUp[key] = converter;
            return converter != null;
        }
    }
    public bool TryReserveWorkOnConverter(FormatPair key) {
        if (!TryGetConverter(key, out var converter)) return false;
        lock (_concurrentWork) {
            var i = _concurrentWork.TryGetValue(converter, out var match) ? match : new converterInfo(0, DateTime.MinValue);
            if (i.ConcurrentCount >= converter.MaxConcurrentWork) {
                // Console.WriteLine("Too many concurrent calls for converter");
                return false;
            }
            var now = DateTime.UtcNow;
            if (now.Subtract(i.LastWork).TotalMilliseconds <= converter.MinIntervalBetweenCallsInMs) {
                // Console.WriteLine("Converter called too often");
                return false;
            }
            _concurrentWork[converter] = new converterInfo(i.ConcurrentCount + 1, now);
            return true;
        }
    }
    public void ReleaseWorkFromConverter(FormatPair key) {
        if (!TryGetConverter(key, out var converter)) return;
        lock (_concurrentWork) {
            if (_concurrentWork.TryGetValue(converter, out var existing)) {
                var count = existing.ConcurrentCount - 1;
                if (count < 0)
                    count = 0;// should not happen                
                _concurrentWork[converter] = new converterInfo(count, existing.LastWork);
            }
        }
    }
}
