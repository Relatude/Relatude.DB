namespace Benchmark;
public class TestOptions {
    public string DataFileRootDefault { get; set; } = GetTempDataFolder("DBTester");
    public bool FlushDiskOnEveryOperation { get; set; } = false;
    public int RandomSeed { get; set; } = 1234;
    public int UserCount { get; set; } = 1000;
    public int CompanyCount { get; set; } = 1000;
    public int DocumentCount { get; set; } = 2000;
    public bool RecreateDatabase { get; set; } = true;
    public bool DeleteAllOnExit { get; set; } = true;
    public int MaxDegreeOfParallelism { get; set; } = 1;
    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(10);
    public string[]? SelectedTests { get; set; }

    public static string GetTempDataFolder(string folderName) {
        string tempRootPath;
        if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
            // On Windows, use the system drive (usually C:)
            tempRootPath = Path.GetPathRoot(Environment.SystemDirectory)!;
        } else {
            // get a writable temp root:
            tempRootPath = Path.GetTempPath();
        }
        string tempDataPath = Path.Combine(tempRootPath, folderName);        
        return tempDataPath;
    }
}