using Relatude.DB.Common;

namespace Relatude;

[TestClass]
public class StreamExtensionsTests {

    [TestMethod]
    public void PrimitiveValues_RoundTripThroughStream() {
        // WriteFloat used to write only 2 of 4 bytes (and ReadFloat always threw), corrupting
        // any value protocol it was part of; pin the full primitive suite while at it
        using var s = new MemoryStream();
        s.WriteFloat(3.14159f);
        s.WriteFloat(float.MinValue);
        s.WriteFloat(float.MaxValue);
        s.WriteDouble(2.71828);
        s.WriteInt(int.MinValue);
        s.WriteLong(long.MaxValue);
        s.WriteDecimal(123.456m);
        s.WriteGuid(new Guid("11111111-2222-3333-4444-555555555555"));
        s.WriteString("hello æøå");
        s.Position = 0;
        Assert.AreEqual(3.14159f, s.ReadFloat());
        Assert.AreEqual(float.MinValue, s.ReadFloat());
        Assert.AreEqual(float.MaxValue, s.ReadFloat());
        Assert.AreEqual(2.71828, s.ReadDouble());
        Assert.AreEqual(int.MinValue, s.ReadInt());
        Assert.AreEqual(long.MaxValue, s.ReadLong());
        Assert.AreEqual(123.456m, s.ReadDecimal());
        Assert.AreEqual(new Guid("11111111-2222-3333-4444-555555555555"), s.ReadGuid());
        Assert.AreEqual("hello æøå", s.ReadString());
        Assert.AreEqual(s.Length, s.Position, "Everything written must have been consumed exactly");
    }

    [TestMethod]
    public void ArrayValues_RoundTripThroughStream() {
        using var s = new MemoryStream();
        var guids = new[] { Guid.NewGuid(), Guid.Empty, Guid.NewGuid() };
        var floats = new[] { 1.5f, -2.25f, 0f };
        s.WriteGuidArray(guids);
        s.WriteFloatArray(floats);
        s.WriteStringArray(["a", "", "æøå"]);
        s.WriteGuidArray([]);
        s.Position = 0;
        CollectionAssert.AreEqual(guids, s.ReadGuidArray());
        CollectionAssert.AreEqual(floats, s.ReadFloatArray());
        CollectionAssert.AreEqual(new[] { "a", "", "æøå" }, s.ReadStringArray());
        Assert.AreEqual(0, s.ReadGuidArray().Length);
        Assert.AreEqual(s.Length, s.Position);
    }

    [TestMethod]
    public void TruncatedStream_ThrowsInsteadOfReturningGarbage() {
        using var s = new MemoryStream();
        s.WriteGuid(Guid.NewGuid());
        var truncated = new MemoryStream(s.ToArray(), 0, 10); // cut mid-value
        try {
            truncated.ReadGuid();
            Assert.Fail("Reading a truncated value must throw, not return garbage");
        } catch (EndOfStreamException) { }
    }
}
