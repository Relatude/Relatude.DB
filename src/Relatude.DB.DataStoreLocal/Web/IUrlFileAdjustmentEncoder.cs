using Relatude.DB.FileConversion;

namespace Relatude.DB.Web;

public interface IUrlFileAdjustmentEncoder {
    FileAdjustment GetAdjustmentFromEncodedString(string urlString);
    string GetEncodedString(FileAdjustment adj);
}

