namespace Relatude.DB.Common;
public static class ArrayExtenstion {
    /// <summary>
    /// Creates a new array by copying all elements from the source array except for the element at the specified index.
    /// </summary>
    /// <remarks>The order of the remaining elements is preserved. The source array is not modified.</remarks>
    /// <typeparam name="T">The type of the elements in the array.</typeparam>
    /// <param name="source">The source array from which to copy elements. Cannot be null.</param>
    /// <param name="index">The zero-based index of the element to remove from the source array. Must be within the bounds of the array.</param>
    /// <returns>A new array containing all elements from the source array except the element at the specified index.</returns>
    public static T[] CopyAndRemoveAt<T>(this T[] source, int index) {
        T[] dest = new T[source.Length - 1];
        if (index > 0) Array.Copy(source, 0, dest, 0, index);
        if (index < source.Length - 1) Array.Copy(source, index + 1, dest, index, source.Length - index - 1);
        return dest;
    }
    /// <summary>
    /// Calculates a simple checksum for the specified byte array.
    /// </summary>
    /// <remarks>The checksum is computed by summing the values of all bytes in the array. This method does
    /// not provide cryptographic security and should not be used for security-sensitive purposes.</remarks>
    /// <param name="array">The byte array for which to compute the checksum. Cannot be null.</param>
    /// <returns>A 32-bit unsigned integer representing the checksum of the array contents.</returns>
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
    /// <summary>
    /// Calculates a checksum for the specified byte array and updates the provided checksum value by combining it with
    /// the result.
    /// </summary>
    /// <remarks>This method processes the entire array and updates the referenced checksum value by applying
    /// a bitwise exclusive OR (XOR) with the computed checksum. The method does not perform any validation on the input
    /// array; callers should ensure the array is not null to avoid a runtime exception.</remarks>
    /// <param name="array">The byte array for which to compute the checksum. Cannot be null.</param>
    /// <param name="prev">A reference to a 32-bit unsigned integer that holds the previous checksum value. This value is updated with the
    /// new checksum result.</param>
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
    /// <summary>
    /// Calculates a checksum over a specified number of bytes in the array and updates the provided checksum value.
    /// </summary>
    /// <remarks>This method processes the array sequentially from the first element up to the specified
    /// count. The operation is performed in an unchecked context, and the result is combined with the previous checksum
    /// value using a bitwise XOR.</remarks>
    /// <param name="array">The byte array containing the data to compute the checksum for. Cannot be null.</param>
    /// <param name="prev">A reference to the checksum value to update. The computed checksum is XORed with this value and the result is
    /// stored back in this parameter.</param>
    /// <param name="count">The number of bytes from the start of the array to include in the checksum calculation. Must be non-negative and
    /// less than or equal to the length of the array.</param>
    public static unsafe void EvaluateChecksum(this byte[] array, ref uint prev, int count) {
        unchecked {
            uint checksum = 0;
            fixed (byte* arrayBase = array) {
                byte* arrayPointer = arrayBase;
                for (int i = count - 1; i >= 0; i--) {
                    checksum += *arrayPointer;
                    arrayPointer++;
                }
            }
            prev = checksum ^ prev;
        }
    }
}
