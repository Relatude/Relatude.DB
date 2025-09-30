namespace Benchmark;
public class TestOptions {
    public string DataFileRootDefault { get; set; } = GetTempDataFolder("DBTester");
    public int GenerationSeed { get; set; } = 1234;
    public int UserCount { get; set; } = 5000;
    public int CompanyCount { get; set; } = 1000;
    public int DocumentCount { get; set; } = 20000;
    public bool RecreateDatabase { get; set; } = true;
    public bool DeleteAllOnExit { get; set; } = true;
    public int MaxDegreeOfParallelism { get; set; } = 1;

    public static string GetTempDataFolder(string folderName) {
        string rootPath;
        if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
            // On Windows, use the system drive (usually C:)
            rootPath = Path.GetPathRoot(Environment.SystemDirectory)!;
        } else {
            // On Linux/macOS, root is always "/"
            rootPath = Path.DirectorySeparatorChar.ToString();
        }
        string tempDataPath = Path.Combine(rootPath, folderName);        
        return tempDataPath;
    }
}