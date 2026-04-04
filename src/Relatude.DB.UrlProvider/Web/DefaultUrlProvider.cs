using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.DataStores;
using Relatude.DB.FileConverter;

namespace Relatude.DB.Web;

public class DefaultUrlProvider : IUrlProvider {
    readonly UrlFileAdjustmentEncoder _encoder;
    readonly IDataStore _dataStore;
    readonly UrlProviderSettings _settings;
    public DefaultUrlProvider(IDataStore dataStore, UrlProviderSettings settings) {
        _settings = settings;
        _dataStore = dataStore;
        _encoder = new(settings.HaskKey);
    }
    public FileIdWithAdjustment GetFileIdWithAdjustment(string url) {
        throw new NotImplementedException();
    }
    public IdKeyWithCultureId GetIdKeyWithCultureId(string url) {
        throw new NotImplementedException();
    }
    public string GetUrl(FileIdWithAdjustment fileIdWithAdjustment) {
        throw new NotImplementedException();
    }
    public string GetUrl(INodeData node) {
        throw new NotImplementedException();
    }
    public UrlTargetType GetUrlTargetType(string url) {
        throw new NotImplementedException();
    }
    public void RegisterlDomainPatternAsInternal(string url) {
        throw new NotImplementedException();
    }
}






















