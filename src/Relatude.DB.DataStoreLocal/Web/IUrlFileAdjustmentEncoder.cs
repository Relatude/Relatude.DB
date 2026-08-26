using Relatude.DB.FileConversion;

namespace Relatude.DB.Web;

public interface IUrlFileAdjustmentEncoder {
    FileAdjustmentBase GetAdjustmentFromEncodedString(string urlString);
    string GetEncodedString(FileAdjustmentBase adj);
}

