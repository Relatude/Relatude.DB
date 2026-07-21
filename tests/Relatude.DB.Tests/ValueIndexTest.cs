
using System.Text;
using Relatude.DB.DataStores.Indexes;
using Relatude.DB.DataStores.Sets;
using Relatude.DB.IO;

namespace Tests;
[TestClass]
public class ValueIndexTest {

    [TestMethod]
    public void TestingCacheKeyLogic() {
        var s = new SetRegister(100);
        var memIo = new IOProviderMemory();
        var file = memIo.OpenAppend("test");
        var fileKeyUtil = new FileKeyUtility(null);
        var index = new ValueIndex<int>(s, "test", "Test", memIo, fileKeyUtil, (v, s) => s.WriteInt(v), (s) => s.ReadInt());

        index.Add(10, 1);
        index.Add(11, 2);
        index.Add(12, 3);
        index.Add(13, 4);

        index.Add(18, 9);
        index.Add(19, 10);
        index.Add(20, 11);

        StringBuilder sb = new StringBuilder();

        var keytoString = (object[] keys) => string.Join("|", keys);

        foreach (var queryType in Enum.GetValues<QueryType>()) {
            for (int value = 0; value < 15; value++) {
                sb.Append(value + " " + (index.ContainsValue(value) ? "*" : " ") + " ");
                var key = index.GetCacheKey(value, queryType);
                sb.Append($"{keytoString(key)}");
                sb.AppendLine();
            }

        }

        var report = sb.ToString();




    }
}