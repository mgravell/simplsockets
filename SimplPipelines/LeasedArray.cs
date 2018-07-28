using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SimplPipelines
{
    public static class LeasedArray
    {
        /// <summary>
        /// Creates a lease over the provided array; the contents are not copied - the array
        /// provided will be handed to the pool when disposed
        /// </summary>
        public static LeasedArray<T> CreateLease<T>(this T[] source, int length = -1)
        {
            if (source == null) return null; // GIGO
            if (length < 0) length = source.Length;
            else if (length > source.Length) throw new ArgumentOutOfRangeException(nameof(length));
            return new LeasedArray<T>(source, length);
        }
        /// <summary>
        /// Creates a lease from the provided sequence, copying the data out into a linear vector
        /// </summary>
        public static LeasedArray<T> CreateLease<T>(this ReadOnlySequence<T> source)
        {
            if (source.IsEmpty) return new LeasedArray<T>(Array.Empty<T>(), 0);

            int len = checked((int)source.Length);
            var arr = ArrayPool<T>.Shared.Rent(len);
            source.CopyTo(arr);
            return new LeasedArray<T>(arr, len);
        }
        /// <summary>
        /// Decode a blob to a leased char array
        /// </summary>
        public static LeasedArray<char> Decode(this LeasedArray<byte> bytes, Encoding encoding = null)
            => Decode(bytes.Memory, encoding);

        /// <summary>
        /// Decode a blob to a leased char array
        /// </summary>
        public static LeasedArray<char> Decode(this ReadOnlyMemory<byte> bytes, Encoding encoding = null)
        {
            if (encoding == null) encoding = Encoding.UTF8;
            if (!MemoryMarshal.TryGetArray(bytes, out var blob))
                throw new InvalidOperationException("Not an array - can fix on netcoreapp2.1 or via unsafe, but...");

            var charCount = encoding.GetCharCount(blob.Array, blob.Offset, blob.Count);
            var clob = ArrayPool<char>.Shared.Rent(charCount);
            encoding.GetChars(blob.Array, blob.Offset, blob.Count, clob, 0);
            return new LeasedArray<char>(clob, charCount);
        }
        /// <summary>
        /// Encode a string to a leased byte array
        /// </summary>
        public static LeasedArray<byte> Encode(this string value, Encoding encoding = null)
        {
            if (encoding == null) encoding = Encoding.UTF8;

            var byteCount = encoding.GetByteCount(value);
            var blob = ArrayPool<byte>.Shared.Rent(byteCount);
            Encoding.UTF8.GetBytes(value, 0, value.Length, blob, 0);
            return new LeasedArray<byte>(blob, byteCount);
        }
    }
    /// <summary>
    /// A thin wrapper around a leased array; when disposed, the array
    /// is returned to the pool; the caller is responsible for not retaining
    /// a reference to the array (via .Memory / .ArraySegment) after using Dispose()
    /// </summary>
    public class LeasedArray<T> : IDisposable
    {
        /// <summary>
        /// The effective size of the leased array
        /// </summary>
        public int Length { get; }

        private T[] _oversized;

        internal LeasedArray(T[] oversized, int length)
        {
            Length = length;
            _oversized = oversized;
        }
        private T[] GetArray() => Interlocked.CompareExchange(ref _oversized, null, null)
                    ?? throw new ObjectDisposedException(ToString());
        /// <summary>
        /// Gets the array contents as a Memory<T>
        /// </summary>
        public Memory<T> Memory => new Memory<T>(GetArray(), 0, Length);
        /// <summary>
        /// Gets the array contents as a Span<T>
        /// </summary>
        public Span<T> Span => new Span<T>(GetArray(), 0, Length);
        /// <summary>
        /// Gets the array contents as an ArraySegment<T>
        /// </summary>
        public ArraySegment<T> ArraySegment => new ArraySegment<T>(GetArray(), 0, Length);

        public bool IsEmpty => Length == 0;

        /// <summary>
        /// Release the array back to the pool
        /// </summary>
        public void Dispose()
        {
            var arr = Interlocked.Exchange(ref _oversized, null);
            if (arr != null && arr.Length != 0) ArrayPool<T>.Shared.Return(arr);
        }
    }
}
