using Relatude.DB.FileConverter;

namespace Relatude.DB.Web;

public interface IUrlProvider { // includes storage and local cache
    string GetUrl(FileIdWithAdjustment fileIdWithAdjustment);
}