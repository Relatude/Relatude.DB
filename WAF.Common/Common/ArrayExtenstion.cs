namespace WAF.Common;
public static class ArrayExtenstion {
    public static T[] CopyAndRemoveAt<T>(this T[] source, int index) {
        T[] dest = new T[source.Length - 1];
        if (index > 0) Array.Copy(source, 0, dest, 0, index);
        if (index < source.Length - 1) Array.Copy(source, index + 1, dest, index, source.Length - index - 1);
        return dest;
    }
    public static IEnumerable<IEnumerable<T>> Split<T>(this T[] array, int size) {
        for (var i = 0; i < (float)array.Length / size; i++) {
            yield return array.Skip(i * size).Take(size);
        }
    }
    public static unsafe uint GetChecksum(this byte[] array) {
        unchecked {
            uint checksum = 0;
            fixed (byte* arrayBase = array) {
                byte* arrayPointer = arrayBase;
                for (int i = array.Length - 1; i >= 0; i--) {
                    checksum += *arrayPointer;
                    arrayPointer++;
                }
            }
            return checksum;
        }
    }
    public static unsafe void EvaluateChecksum(this byte[] array, ref uint prev) {
        unchecked {
            uint checksum = 0;
            fixed (byte* arrayBase = array) {
                byte* arrayPointer = arrayBase;
                for (int i = array.Length - 1; i >= 0; i--) {
                    checksum += *arrayPointer;
                    arrayPointer++;
                }
            }
            prev = checksum ^ prev;
        }
    }
}
