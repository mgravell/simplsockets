using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SimplPipelines
{
    public static class Leased
    {
        public static Leased<T> CreateLease<T>(this ReadOnlySequence<T> source)
        {
            if (source.IsEmpty) return new Leased<T>(Array.Empty<T>(), 0);

            int len = checked((int)source.Length);
            var arr = ArrayPool<T>.Shared.Rent(len);
            source.CopyTo(arr);
            return new Leased<T>(arr, len);
        }

        public static Leased<char> Decode(this Leased<byte> bytes, Encoding encoding = null)
        {
            if (encoding == null) encoding = Encoding.UTF8;
            var blob = bytes.ArraySegment;
            var charCount = encoding.GetCharCount(blob.Array, blob.Offset, blob.Count);
            var clob = ArrayPool<char>.Shared.Rent(charCount);
            encoding.GetChars(blob.Array, blob.Offset, blob.Count, clob, 0);
            return new Leased<char>(clob, charCount);
        }
        public static Leased<char> Decode(this ReadOnlyMemory<byte> bytes, Encoding encoding = null)
        {
            if (encoding == null) encoding = Encoding.UTF8;
            if (!MemoryMarshal.TryGetArray(bytes, out var blob))
                throw new InvalidOperationException("Not an array - can fix on netcoreapp2.1, but...");

            var charCount = encoding.GetCharCount(blob.Array, blob.Offset, blob.Count);
            var clob = ArrayPool<char>.Shared.Rent(charCount);
            encoding.GetChars(blob.Array, blob.Offset, blob.Count, clob, 0);
            return new Leased<char>(clob, charCount);
        }

        public static Leased<byte> Encode(this string value, Encoding encoding = null)
        {
            if (encoding == null) encoding = Encoding.UTF8;

            var byteCount = encoding.GetByteCount(value);
            var blob = ArrayPool<byte>.Shared.Rent(byteCount);
            Encoding.UTF8.GetBytes(value, 0, value.Length, blob, 0);
            return new Leased<byte>(blob, byteCount);
        }
    }
    /// <summary>
    /// A thin wrapper around a leased array; when disposed, the array
    /// is returned to the pool; the caller is responsible for not retaining
    /// a reference to the array (via .Memory / .ArraySegment) after using Dispose()
    /// </summary>
    public class Leased<T> : IDisposable
    {
        /// <summary>
        /// The effective size of the leased array
        /// </summary>
        public int Length { get; }

        private T[] _oversized;

        internal Leased(T[] oversized, int length)
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
