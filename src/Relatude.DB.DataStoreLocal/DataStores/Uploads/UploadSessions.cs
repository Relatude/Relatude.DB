using Relatude.DB.Common;
using Relatude.DB.DataStores.Files;

namespace Relatude.DB.DataStores.Uploads;

internal class UploadSessions(DataStoreLocal store) {
    readonly static TimeSpan _maxStaleAgeForUploadSessions = TimeSpan.FromMinutes(10);
    Dictionary<Guid, UploadSession> _uploadSessions = [];
    public IFileStoreMultiPartSupport getMultiPartStore(UploadSession session) {
        if (store.getFileStore(session.FileValue.StorageId) is not IFileStoreMultiPartSupport fileStore)
            throw new Exception("File store does not support multipart upload");
        return fileStore;
    }
    public void removeSession(Guid fileId) {
        lock (_uploadSessions) {
            if (!_uploadSessions.TryGetValue(fileId, out var session)) return;
            session.Hash.Dispose();
            _uploadSessions.Remove(fileId);
        }
    }
    public UploadSession getSession(Guid fileId) {
        lock (_uploadSessions) {
            if (!_uploadSessions.TryGetValue(fileId, out var session)) 
                throw new Exception("Upload session not found");
            session.Touch();
            return session;
        }
    }
    public async Task removeOldSessions() {
        List<UploadSession> toRemove;
        lock (_uploadSessions) {
            toRemove = _uploadSessions.Values.Where(s => DateTime.UtcNow - s.LastAccessed > _maxStaleAgeForUploadSessions).ToList();
            foreach (var s in toRemove) 
                _uploadSessions.Remove(s.FileValue.FileId);
        }
        foreach (var s in toRemove) {
            try {
                var fileStore = store.getFileStore(s.FileValue.StorageId);
                await fileStore.DeleteAsync(s.FileValue);
            } catch (Exception e) {
                store.LogError($"Failed to remove old upload session for file {s.FileValue.FileId}", e);
            }
        }
    }
    public void AddSession(FileValue fileValue) {
        lock (_uploadSessions) {
            _uploadSessions[fileValue.FileId] = new UploadSession(fileValue);
        }
    }
}
