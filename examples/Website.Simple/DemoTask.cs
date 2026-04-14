using Relatude.DB.DataStores;
using Relatude.DB.IO;
using Relatude.DB.Nodes;
using Relatude.DB.Tasks;
using System.Diagnostics;

namespace WebApplication1;

public class DemoTask : TaskData {
    public int MyId { get; set; }
    public string DemoText { get; set; } = string.Empty;
}
public class DemoTaskRunner(NodeStore db) : TaskRunner<DemoTask> {
    public override BatchTaskPriority Priority => BatchTaskPriority.Medium;
    public override async Task ExecuteAsync(Batch<DemoTask> batch, TaskLogger? taskLogger) {
        var random = new Random();
        foreach (var t in batch.Tasks) {
            await Task.Delay(random.Next(10, 100));
            Console.WriteLine("Executing task with ID: " + t.MyId + " and text: " + t.DemoText);
        }
    }
    public override bool PersistToDisk => true;
    public override bool DeleteOnSuccess => true;
    public override int MaxTaskCountPerBatch => 100;
    public override TimeSpan GetMaximumAgeInQueueAfterExecution() => TimeSpan.FromHours(24);
    public override DemoTask TaskFromBytes(byte[] bytes) { 
        using MemoryStream ms = new(bytes);
        using BinaryReader reader = new(ms);
        return new DemoTask {
            MyId = reader.ReadInt32(),
            DemoText = reader.ReadString()
        };
    }
    public override byte[] TaskToBytes(DemoTask task) {
        using MemoryStream ms = new();
        using BinaryWriter writer = new(ms);
        writer.Write(task.MyId);
        writer.Write(task.DemoText);
        return ms.ToArray();
    }
}
