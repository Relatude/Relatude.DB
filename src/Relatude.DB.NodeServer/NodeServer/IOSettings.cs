using Relatude.DB.IO;
namespace Relatude.DB.NodeServer {
    public enum IOTypes {
        Memory = 0,
        LocalDisk = 1,
        AzureBlobStorage = 2,
    }
    public class IOSettings {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? Path { get; set; }
        public string? BlobConnectionString { get; set; }
        public string? BlobContainerName { get; set; }
        public bool LockBlob { get; set; }
        public IOTypes IOType { get; set; }
        public static IIOProvider Create(IOSettings settings, string appRootPath) {
            switch (settings.IOType) {
                case IOTypes.Memory: {
                        return new IOProviderMemory();
                    }
                case IOTypes.LocalDisk: {
                        var path = settings.Path;
                        if (string.IsNullOrEmpty(path)) path = "~";
                        path = path.EnsureDirectorySeparatorChar();
                        if (path.StartsWith('~')) path = appRootPath.SuperPathCombine(path[1..]);
                        if (!System.IO.Path.IsPathRooted(path)) path = appRootPath.SuperPathCombine(path);
                        if (!path.VerifyPathIsUnderThis(appRootPath)) throw new Exception("Path not under root");
                        return new IODisk(path);
                    }
                case IOTypes.AzureBlobStorage: {
                        return LateBindings.CreateAzureBlobIOProvider(settings);
                    }
                default:
                    throw new NotImplementedException();
            }
        }
    }
}
